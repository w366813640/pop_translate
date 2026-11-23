using System.Text.Json;
using PopTranslate.Core.Configuration;
using PopTranslate.Core.Models;
using Xunit;

namespace PopTranslate.Tests.Core;

public sealed class DpapiAndSettingsStoreTests : IDisposable
{
    private readonly string _tempDir;

    public DpapiAndSettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PopTranslateTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore transient file lock cleanup issues in temp dir.
        }
    }

    [Fact]
    public void DpapiSecretProtector_RoundTripsSecret_WithDpapiPrefix()
    {
        var protector = new DpapiSecretProtector();
        const string secret = "sk-live-secret-api-key-987654321";

        var protectedText = protector.Protect(secret);

        Assert.StartsWith("dpapi:", protectedText, StringComparison.Ordinal);
        Assert.NotEqual(secret, protectedText);
        Assert.DoesNotContain(secret, protectedText, StringComparison.Ordinal);

        Assert.True(protector.TryUnprotect(protectedText, out var roundTripped));
        Assert.Equal(secret, roundTripped);
        Assert.Equal(secret, protector.Unprotect(protectedText));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DpapiSecretProtector_EmptyOrWhitespace_ReturnsEmpty(string input)
    {
        var protector = new DpapiSecretProtector();

        Assert.Equal(string.Empty, protector.Protect(input));
        Assert.Equal(string.Empty, protector.Unprotect(input));
        Assert.True(protector.TryUnprotect(input, out var plaintext));
        Assert.Equal(string.Empty, plaintext);
    }

    [Theory]
    [InlineData("dpapi:!!!not-valid-base64!!!")]
    [InlineData("dpapi:AAAA")]
    [InlineData("unprefixed-garbage-value")]
    public void DpapiSecretProtector_CorruptOrInvalidCiphertext_DoesNotThrow(string invalidCiphertext)
    {
        var protector = new DpapiSecretProtector();

        Assert.False(protector.TryUnprotect(invalidCiphertext, out var plaintext));
        Assert.Equal(string.Empty, plaintext);

        var unprotected = protector.Unprotect(invalidCiphertext);
        Assert.NotNull(unprotected);
    }

    [Fact]
    public async Task JsonSettingsStore_MissingFile_ReturnsZeroConfigDefaults()
    {
        var path = Path.Combine(_tempDir, "nested", "settings.json");
        var store = new JsonSettingsStore(path);

        var loaded = await store.LoadAsync();

        Assert.Equal(path, store.SettingsFilePath);
        Assert.Equal(LanguageCode.ChineseSimplified, loaded.PrimaryTargetLanguage);
        Assert.Equal(LanguageCode.English, loaded.SecondaryTargetLanguage);
        Assert.Contains(loaded.Providers, p => p.Kind == ProviderKind.FreeTraditional && p.Enabled);
    }

    [Fact]
    public async Task JsonSettingsStore_SaveAndLoad_AtomicRoundTrip_ProtectsSecretsAndRaisesEvent()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        var protector = new DpapiSecretProtector();
        var store = new JsonSettingsStore(protector, path);

        AppSettings? raisedEventSettings = null;
        store.SettingsChanged += (_, s) => raisedEventSettings = s;

        var settings = AppSettings.CreateDefault();
        settings.PrimaryTargetLanguage = LanguageCode.Japanese;
        settings.Window.PopupWidth = 500;
        settings.Providers[1].Enabled = true;
        // Pass raw plaintext to verify JsonSettingsStore never writes plaintext secrets to disk
        settings.Providers[1].EncryptedApiKey = "sk-plain-openai-key";
        settings.Ocr.EncryptedVisionApiKey = protector.Protect("sk-vision-key");
        settings.Proxy.EncryptedPassword = "proxy-secret-password";

        await store.SaveAsync(settings);

        Assert.NotNull(raisedEventSettings);
        Assert.Equal(LanguageCode.Japanese, raisedEventSettings!.PrimaryTargetLanguage);
        Assert.True(File.Exists(path));

        var rawJson = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("sk-plain-openai-key", rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-vision-key", rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("proxy-secret-password", rawJson, StringComparison.Ordinal);
        Assert.Contains("dpapi:", rawJson, StringComparison.Ordinal);

        // Verify valid JSON and reload via fresh store instance
        using var doc = JsonDocument.Parse(rawJson);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);

        var freshStore = new JsonSettingsStore(protector, path);
        var loaded = await freshStore.LoadAsync();

        Assert.Equal(LanguageCode.Japanese, loaded.PrimaryTargetLanguage);
        Assert.Equal(500, loaded.Window.PopupWidth);
        Assert.Equal("sk-plain-openai-key", protector.Unprotect(loaded.Providers[1].EncryptedApiKey));
        Assert.Equal("sk-vision-key", protector.Unprotect(loaded.Ocr.EncryptedVisionApiKey));
        Assert.Equal("proxy-secret-password", protector.Unprotect(loaded.Proxy.EncryptedPassword));
    }

    [Fact]
    public async Task JsonSettingsStore_CorruptJson_BacksUpFileAndReturnsDefaults()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        await File.WriteAllTextAsync(path, "{ this is not valid json !!!");

        var store = new JsonSettingsStore(path);
        var loaded = await store.LoadAsync();

        Assert.Equal(LanguageCode.ChineseSimplified, loaded.PrimaryTargetLanguage);
        Assert.Contains(loaded.Providers, p => p.Kind == ProviderKind.FreeTraditional && p.Enabled);
        Assert.True(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public async Task JsonSettingsStore_LoadsLegacyV1JsonAndSanitizesNullV11Fields()
    {
        var legacyPath = Path.Combine(_tempDir, "legacy-v1-settings.json");
        const string legacyV1Json = """
        {
          "SchemaVersion": 1,
          "PrimaryTargetLanguage": "zh-Hans",
          "SecondaryTargetLanguage": "en",
          "MergePdfLineBreaks": true,
          "Hotkeys": {
            "SelectionTranslate": { "Modifiers": "Alt", "VirtualKey": 68 },
            "InputTranslate": { "Modifiers": "Alt", "VirtualKey": 65 },
            "ScreenshotTranslate": { "Modifiers": "Alt", "VirtualKey": 83 }
          },
          "Providers": [
            {
              "Id": "free-traditional-default",
              "DisplayName": "Free Translate",
              "Kind": "FreeTraditional",
              "Enabled": true,
              "SortOrder": 0
            }
          ]
        }
        """;
        await File.WriteAllTextAsync(legacyPath, legacyV1Json);

        var legacyStore = new JsonSettingsStore(legacyPath);
        var loadedLegacy = await legacyStore.LoadAsync();

        Assert.False(loadedLegacy.AutoBackTranslate);
        Assert.NotNull(loadedLegacy.Polish);
        Assert.Equal(PolishStyle.General, loadedLegacy.Polish.DefaultStyle);
        Assert.False(loadedLegacy.Polish.ShowDiffByDefault);
        Assert.Equal(PolishConfig.DefaultCustomPrompt, loadedLegacy.Polish.CustomPrompt);
        Assert.Equal(HotkeyConfig.DefaultSelectionPolish, loadedLegacy.Hotkeys.SelectionPolish);

        // Explicit null fields in hand-edited JSON must also be sanitized directly on store.Current.
        var nullFieldsPath = Path.Combine(_tempDir, "null-v11-settings.json");
        const string nullFieldsJson = """
        {
          "SchemaVersion": 1,
          "Polish": null,
          "Hotkeys": {
            "SelectionTranslate": null,
            "InputTranslate": null,
            "ScreenshotTranslate": null,
            "SelectionPolish": null
          }
        }
        """;
        await File.WriteAllTextAsync(nullFieldsPath, nullFieldsJson);

        var nullStore = new JsonSettingsStore(nullFieldsPath);
        _ = await nullStore.LoadAsync();

        Assert.NotNull(nullStore.Current.Polish);
        Assert.Equal(PolishConfig.DefaultCustomPrompt, nullStore.Current.Polish.CustomPrompt);
        Assert.Equal(HotkeyConfig.DefaultSelectionTranslate, nullStore.Current.Hotkeys.SelectionTranslate);
        Assert.Equal(HotkeyConfig.DefaultInputTranslate, nullStore.Current.Hotkeys.InputTranslate);
        Assert.Equal(HotkeyConfig.DefaultScreenshotTranslate, nullStore.Current.Hotkeys.ScreenshotTranslate);
        Assert.Equal(HotkeyConfig.DefaultSelectionPolish, nullStore.Current.Hotkeys.SelectionPolish);

        // Explicit null CustomPrompt inside non-null Polish section must also be sanitized.
        var nullPromptPath = Path.Combine(_tempDir, "null-prompt-settings.json");
        const string nullPromptJson = """
        {
          "SchemaVersion": 1,
          "Polish": {
            "DefaultStyle": "Business",
            "CustomPrompt": null,
            "ShowDiffByDefault": true
          }
        }
        """;
        await File.WriteAllTextAsync(nullPromptPath, nullPromptJson);

        var nullPromptStore = new JsonSettingsStore(nullPromptPath);
        _ = await nullPromptStore.LoadAsync();

        Assert.Equal(PolishStyle.Business, nullPromptStore.Current.Polish.DefaultStyle);
        Assert.True(nullPromptStore.Current.Polish.ShowDiffByDefault);
        Assert.Equal(PolishConfig.DefaultCustomPrompt, nullPromptStore.Current.Polish.CustomPrompt);
    }

    [Fact]
    public async Task JsonSettingsStore_RoundTripsV11PolishBackTranslateAndSelectionPolishFieldsAtomically()
    {
        var path = Path.Combine(_tempDir, "v11-settings.json");
        var store = new JsonSettingsStore(path);

        var settings = AppSettings.CreateDefault();
        settings.AutoBackTranslate = true;
        settings.Polish.DefaultStyle = PolishStyle.Academic;
        settings.Polish.ShowDiffByDefault = true;
        settings.Polish.CustomPrompt = "Polish for IEEE publication.";
        settings.Hotkeys.SelectionPolish = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x50);

        await store.SaveAsync(settings);

        var reloadedStore = new JsonSettingsStore(path);
        var loaded = await reloadedStore.LoadAsync();

        Assert.True(loaded.AutoBackTranslate);
        Assert.Equal(PolishStyle.Academic, loaded.Polish.DefaultStyle);
        Assert.True(loaded.Polish.ShowDiffByDefault);
        Assert.Equal("Polish for IEEE publication.", loaded.Polish.CustomPrompt);
        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x50), loaded.Hotkeys.SelectionPolish);
    }
}
