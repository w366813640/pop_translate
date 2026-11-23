using System.Text.Json;
using System.Text.Json.Serialization;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using Xunit;

namespace PopTranslate.Tests.Contracts;

public sealed class PolishAndBackTranslationContractsTests
{
    private static readonly JsonSerializerOptions StoreCompatibleJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void HotkeyAction_SelectionPolish_HasExpectedValueAndDefaultAltPGesture()
    {
        Assert.Equal(3, (int)HotkeyAction.SelectionPolish);

        var config = new HotkeyConfig();
        Assert.Equal(HotkeyModifiers.Alt, config.SelectionPolish.Modifiers);
        Assert.Equal(0x50u, config.SelectionPolish.VirtualKey);
        Assert.Equal("Alt + P", config.SelectionPolish.DisplayText);
        Assert.Equal(HotkeyConfig.DefaultSelectionPolish, config.SelectionPolish);
    }

    [Fact]
    public void HotkeyConfig_Clone_PreservesSelectionPolishAndRecoversFromNull()
    {
        var custom = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x50);
        var config = new HotkeyConfig
        {
            SelectionPolish = custom,
        };

        var cloned = config.Clone();
        Assert.Equal(custom, cloned.SelectionPolish);

        // Explicitly disabled (Empty) gesture must stay Empty rather than reverting to Alt+P.
        config.SelectionPolish = HotkeyGesture.Empty;
        var emptyCloned = config.Clone();
        Assert.True(emptyCloned.SelectionPolish.IsEmpty);

        // Null gesture (e.g., hand-edited JSON) recovers safely to default Alt+P.
        config.SelectionPolish = null!;
        var recovered = config.Clone();
        Assert.Equal(HotkeyConfig.DefaultSelectionPolish, recovered.SelectionPolish);
    }

    [Fact]
    public void PopupMode_PolishStyle_AndDiffOperation_DefineExpectedEnumMembers()
    {
        Assert.Equal(0, (int)PopupMode.Translate);
        Assert.Equal(1, (int)PopupMode.Polish);

        Assert.Equal(0, (int)PolishStyle.General);
        Assert.Equal(1, (int)PolishStyle.Academic);
        Assert.Equal(2, (int)PolishStyle.Business);
        Assert.Equal(3, (int)PolishStyle.Casual);
        Assert.Equal(4, (int)PolishStyle.Concise);
        Assert.Equal(5, (int)PolishStyle.Custom);
        Assert.Equal(6, Enum.GetValues<PolishStyle>().Length);

        Assert.Equal(0, (int)DiffOperation.Equal);
        Assert.Equal(1, (int)DiffOperation.Delete);
        Assert.Equal(2, (int)DiffOperation.Insert);
    }

    [Fact]
    public void PolishConfig_DefaultsAndClone_AreSafeAndIndependent()
    {
        var original = new PolishConfig();
        Assert.Equal(PolishStyle.General, original.DefaultStyle);
        Assert.False(original.ShowDiffByDefault);
        Assert.Equal(PolishConfig.DefaultCustomPrompt, original.CustomPrompt);
        Assert.False(string.IsNullOrWhiteSpace(PolishConfig.DefaultCustomPrompt));

        original.DefaultStyle = PolishStyle.Academic;
        original.ShowDiffByDefault = true;
        original.CustomPrompt = "Polish as IEEE journal prose.";

        var cloned = original.Clone();
        Assert.Equal(PolishStyle.Academic, cloned.DefaultStyle);
        Assert.True(cloned.ShowDiffByDefault);
        Assert.Equal("Polish as IEEE journal prose.", cloned.CustomPrompt);

        // Mutating clone must not affect original.
        cloned.DefaultStyle = PolishStyle.Casual;
        cloned.ShowDiffByDefault = false;
        cloned.CustomPrompt = "Casual rewrite";
        Assert.Equal(PolishStyle.Academic, original.DefaultStyle);
        Assert.True(original.ShowDiffByDefault);
        Assert.Equal("Polish as IEEE journal prose.", original.CustomPrompt);
    }

    [Fact]
    public void AppSettings_CreateDefaultAndClone_IncludeAutoBackTranslateAndPolishConfig()
    {
        var defaults = AppSettings.CreateDefault();
        Assert.False(defaults.AutoBackTranslate);
        Assert.NotNull(defaults.Polish);
        Assert.Equal(PolishStyle.General, defaults.Polish.DefaultStyle);
        Assert.False(defaults.Polish.ShowDiffByDefault);
        Assert.Equal(PolishConfig.DefaultCustomPrompt, defaults.Polish.CustomPrompt);
        Assert.Equal("Alt + P", defaults.Hotkeys.SelectionPolish.DisplayText);

        defaults.AutoBackTranslate = true;
        defaults.Polish.DefaultStyle = PolishStyle.Concise;
        defaults.Polish.ShowDiffByDefault = true;
        defaults.Polish.CustomPrompt = "Make it concise.";

        var cloned = defaults.Clone();
        Assert.True(cloned.AutoBackTranslate);
        Assert.Equal(PolishStyle.Concise, cloned.Polish.DefaultStyle);
        Assert.True(cloned.Polish.ShowDiffByDefault);
        Assert.Equal("Make it concise.", cloned.Polish.CustomPrompt);

        cloned.Polish.DefaultStyle = PolishStyle.Business;
        Assert.Equal(PolishStyle.Concise, defaults.Polish.DefaultStyle);
    }

    [Fact]
    public void AppSettings_DeserializeV1JsonWithoutV11Fields_ProvidesSafeDefaults()
    {
        // Exact V1 settings.json structure before V1.1 fields existed.
        const string legacyV1Json = """
        {
          "SchemaVersion": 1,
          "PrimaryTargetLanguage": "zh-Hans",
          "SecondaryTargetLanguage": "en",
          "MergePdfLineBreaks": true,
          "LaunchAtStartup": false,
          "UseElevatedStartupTask": false,
          "Hotkeys": {
            "SelectionTranslate": { "Modifiers": "Alt", "VirtualKey": 68 },
            "InputTranslate": { "Modifiers": "Alt", "VirtualKey": 65 },
            "ScreenshotTranslate": { "Modifiers": "Alt", "VirtualKey": 83 }
          },
          "Selection": {
            "EnableHoverIcon": true,
            "EnableClipboardFallback": true,
            "QuietModeInFullscreen": true
          },
          "Ocr": {
            "ActiveProvider": "WindowsNative",
            "AutoMergeOcrLineBreaks": true
          },
          "Proxy": {
            "Mode": "System"
          },
          "Window": {
            "PositionMode": "FollowCursor",
            "PopupWidth": 460
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

        var restored = JsonSerializer.Deserialize<AppSettings>(legacyV1Json, StoreCompatibleJsonOptions);
        Assert.NotNull(restored);

        // Existing V1 values preserved.
        Assert.Equal(LanguageCode.ChineseSimplified, restored.PrimaryTargetLanguage);
        Assert.Equal(LanguageCode.English, restored.SecondaryTargetLanguage);
        Assert.Equal(460, restored.Window.PopupWidth);
        Assert.Equal("Alt + D", restored.Hotkeys.SelectionTranslate.DisplayText);
        Assert.Equal("Alt + A", restored.Hotkeys.InputTranslate.DisplayText);
        Assert.Equal("Alt + S", restored.Hotkeys.ScreenshotTranslate.DisplayText);

        // Missing V1.1 fields safely initialized to defaults.
        Assert.False(restored.AutoBackTranslate);
        Assert.NotNull(restored.Polish);
        Assert.Equal(PolishStyle.General, restored.Polish.DefaultStyle);
        Assert.False(restored.Polish.ShowDiffByDefault);
        Assert.Equal(PolishConfig.DefaultCustomPrompt, restored.Polish.CustomPrompt);
        Assert.Equal("Alt + P", restored.Hotkeys.SelectionPolish.DisplayText);

        // Cloning the deserialized V1 settings succeeds cleanly.
        var cloned = restored.Clone();
        Assert.False(cloned.AutoBackTranslate);
        Assert.Equal("Alt + P", cloned.Hotkeys.SelectionPolish.DisplayText);
        Assert.Equal(PolishStyle.General, cloned.Polish.DefaultStyle);
    }

    [Fact]
    public void AppSettings_DeserializeJsonWithExplicitNullV11Fields_CloneRecoversSafely()
    {
        const string nullFieldsJson = """
        {
          "SchemaVersion": 1,
          "Polish": null,
          "Hotkeys": {
            "SelectionTranslate": { "Modifiers": "Alt", "VirtualKey": 68 },
            "InputTranslate": { "Modifiers": "Alt", "VirtualKey": 65 },
            "ScreenshotTranslate": { "Modifiers": "Alt", "VirtualKey": 83 },
            "SelectionPolish": null
          }
        }
        """;

        var restored = JsonSerializer.Deserialize<AppSettings>(nullFieldsJson, StoreCompatibleJsonOptions);
        Assert.NotNull(restored);

        var cloned = restored.Clone();
        Assert.NotNull(cloned.Polish);
        Assert.Equal(PolishStyle.General, cloned.Polish.DefaultStyle);
        Assert.Equal(PolishConfig.DefaultCustomPrompt, cloned.Polish.CustomPrompt);
        Assert.Equal(HotkeyConfig.DefaultSelectionPolish, cloned.Hotkeys.SelectionPolish);
    }

    [Fact]
    public void V11RequestAndSegmentRecords_ConstructWithExpectedDefaults()
    {
        var polishRequest = new PolishRequest("Hello world");
        Assert.Equal("Hello world", polishRequest.Text);
        Assert.Equal(PolishStyle.General, polishRequest.Style);
        Assert.Null(polishRequest.CustomPrompt);
        Assert.Null(polishRequest.PreferredLanguage);
        Assert.True(polishRequest.ApplyTextPreprocessing);
        Assert.Null(polishRequest.ProviderInstanceIds);

        var backRequest = new BackTranslationRequest(
            ProviderInstanceId: "openai-1",
            TranslatedText: "你好，世界",
            TranslatedLanguage: LanguageCode.ChineseSimplified,
            BackTargetLanguage: LanguageCode.English);
        Assert.Equal("openai-1", backRequest.ProviderInstanceId);
        Assert.Equal("你好，世界", backRequest.TranslatedText);
        Assert.Equal(LanguageCode.ChineseSimplified, backRequest.TranslatedLanguage);
        Assert.Equal(LanguageCode.English, backRequest.BackTargetLanguage);

        var segment = new DiffSegment(DiffOperation.Insert, "excellent");
        Assert.Equal(DiffOperation.Insert, segment.Operation);
        Assert.Equal("excellent", segment.Text);
    }

    [Fact]
    public void V11ContractInterfaces_ExposeExpectedSignaturesInPopTranslateCoreContracts()
    {
        var contractsAssembly = typeof(ITranslationOrchestrator).Assembly;
        var contractNames = contractsAssembly
            .GetTypes()
            .Where(t => t.IsInterface && t.IsPublic && t.Namespace == "PopTranslate.Core.Contracts")
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(nameof(IBackTranslationService), contractNames);
        Assert.Contains(nameof(IPolishProvider), contractNames);
        Assert.Contains(nameof(IPolishOrchestrator), contractNames);
        Assert.Contains(nameof(ITextDiffService), contractNames);

        Assert.NotNull(typeof(IBackTranslationService).GetMethod(nameof(IBackTranslationService.BackTranslateStreamAsync)));
        Assert.NotNull(typeof(IBackTranslationService).GetMethod(nameof(IBackTranslationService.BackTranslateAsync)));
        Assert.NotNull(typeof(IPolishProvider).GetMethod(nameof(IPolishProvider.PolishStreamAsync)));
        Assert.NotNull(typeof(IPolishProvider).GetMethod(nameof(IPolishProvider.PolishAsync)));
        Assert.NotNull(typeof(IPolishOrchestrator).GetMethod(nameof(IPolishOrchestrator.PolishParallelAsync)));
        Assert.NotNull(typeof(IPolishOrchestrator).GetMethod(nameof(IPolishOrchestrator.PolishAllAsync)));
        Assert.NotNull(typeof(IPolishOrchestrator).GetMethod(nameof(IPolishOrchestrator.GetEligibleProviders)));
        Assert.NotNull(typeof(ITextDiffService).GetMethod(nameof(ITextDiffService.ComputeDiff)));
    }
}
