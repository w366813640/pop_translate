using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PopTranslate.App.Services;
using PopTranslate.App.ViewModels;
using PopTranslate.Core.Configuration;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.DependencyInjection;
using PopTranslate.Core.Models;
using PopTranslate.Win32.DependencyInjection;
using Xunit;

namespace PopTranslate.Tests.Integration;

public sealed class StartupSettingsPreservationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public StartupSettingsPreservationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"PopTranslate_StartupPreserve_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
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
            // Ignore temp cleanup races.
        }
    }

    [Fact]
    public async Task ConstructingAllViewModelsBeforeLoadAsync_NeverOverwritesExistingSettingsOrApiKeysOnDisk()
    {
        const string openAiKey = "sk-cpa-real-user-key-99999";
        const string polishKey = "sk-polish-real-user-key-88888";
        const string visionKey = "sk-vision-real-user-key-77777";

        // 1. Seed a realistic user settings.json with custom providers & DPAPI-encrypted API keys on disk.
        var protector = new DpapiSecretProtector();
        var seedStore = new JsonSettingsStore(protector, _settingsPath);
        var userSettings = AppSettings.CreateDefault();
        userSettings.PrimaryTargetLanguage = LanguageCode.Japanese;
        userSettings.SecondaryTargetLanguage = LanguageCode.ChineseSimplified;
        userSettings.Ocr.ActiveProvider = OcrProviderKind.VisionLlm;
        userSettings.Ocr.VisionBaseUrl = "https://custom-vision.example.com/v1";
        userSettings.Ocr.VisionModel = "gpt-4.1-mini";
        userSettings.Ocr.EncryptedVisionApiKey = visionKey;
        userSettings.Providers =
        [
            new ProviderInstanceConfig
            {
                Id = "user-cpa",
                DisplayName = "CPA",
                Kind = ProviderKind.OpenAiCompatible,
                Role = ProviderRole.Translate,
                Enabled = true,
                SortOrder = 0,
                BaseUrl = "https://cpa.example.com/v1",
                Model = "claude-sonnet-4",
                EncryptedApiKey = openAiKey,
            },
            new ProviderInstanceConfig
            {
                Id = "user-polish",
                DisplayName = "AI 文本润色",
                Kind = ProviderKind.OpenAiCompatible,
                Role = ProviderRole.Polish,
                TriggerMode = PolishTriggerMode.AutoParallel,
                Enabled = true,
                SortOrder = 1,
                BaseUrl = "https://cpa.example.com/v1",
                Model = "claude-sonnet-4",
                EncryptedApiKey = polishKey,
            },
        ];
        await seedStore.SaveAsync(userSettings);

        var originalDiskJson = await File.ReadAllTextAsync(_settingsPath);

        // 2. Simulate cold app restart: construct fresh DI container and resolve ALL singleton ViewModels
        //    BEFORE LoadAsync() is invoked on the fresh ISettingsStore!
        var services = new ServiceCollection();
        services.AddPopTranslateCore();
        services.AddPopTranslateWin32();
        services.AddPopTranslateApp();
        services.Replace(ServiceDescriptor.Singleton<ISettingsStore>(sp =>
            new JsonSettingsStore(sp.GetRequiredService<ISecretProtector>(), _settingsPath)));
        services.Replace(ServiceDescriptor.Singleton<IStartupPrivilegeService, StubStartupPrivilegeService>());

        using var provider = services.BuildServiceProvider();
        var freshStore = provider.GetRequiredService<ISettingsStore>();

        var unexpectedSaveCount = 0;
        freshStore.SettingsChanged += (_, _) => Interlocked.Increment(ref unexpectedSaveCount);

        // Resolving every ViewModel must NEVER fire background SaveAsync calls!
        _ = provider.GetRequiredService<TranslationPopupViewModel>();
        _ = provider.GetRequiredService<GeneralSettingsViewModel>();
        _ = provider.GetRequiredService<ProvidersSettingsViewModel>();
        _ = provider.GetRequiredService<OcrSettingsViewModel>();
        _ = provider.GetRequiredService<HotkeysSettingsViewModel>();
        _ = provider.GetRequiredService<NetworkSettingsViewModel>();
        _ = provider.GetRequiredService<SettingsViewModel>();

        // Wait longer than all ViewModel debounce timers (250-300ms) so any rogue fire-and-forget save would surface.
        await Task.Delay(450);

        Assert.Equal(0, Volatile.Read(ref unexpectedSaveCount));

        // Disk file must be 100% untouched byte-for-byte!
        var afterConstructionJson = await File.ReadAllTextAsync(_settingsPath);
        Assert.Equal(originalDiskJson, afterConstructionJson);

        // Now LoadAsync() must recover every single custom provider and decrypted API key intact!
        var loaded = await freshStore.LoadAsync();
        Assert.Equal(LanguageCode.Japanese, loaded.PrimaryTargetLanguage);
        Assert.Equal(2, loaded.Providers.Count);

        var cpa = Assert.Single(loaded.Providers, p => p.Id == "user-cpa");
        Assert.Equal("CPA", cpa.DisplayName);
        Assert.Equal("https://cpa.example.com/v1", cpa.BaseUrl);
        Assert.Equal(openAiKey, protector.Unprotect(cpa.EncryptedApiKey));

        var polish = Assert.Single(loaded.Providers, p => p.Id == "user-polish");
        Assert.Equal("AI 文本润色", polish.DisplayName);
        Assert.Equal(polishKey, protector.Unprotect(polish.EncryptedApiKey));

        Assert.Equal(visionKey, protector.Unprotect(loaded.Ocr.EncryptedVisionApiKey));
    }

    [Fact]
    public async Task AppStartup_PreloadsSettingsStoreBeforeResolvingAppCoordinator()
    {
        var repoRoot = FindRepoRoot();
        var appCs = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "src", "PopTranslate.App", "App.xaml.cs"));

        var preloadIndex = appCs.IndexOf("GetRequiredService<ISettingsStore>().LoadAsync()", StringComparison.Ordinal);
        var coordinatorIndex = appCs.IndexOf("GetRequiredService<AppCoordinator>()", StringComparison.Ordinal);

        Assert.True(preloadIndex > 0, "App.OnLaunched should preload ISettingsStore.LoadAsync() before constructing AppCoordinator.");
        Assert.True(coordinatorIndex > preloadIndex, "ISettingsStore.LoadAsync() must run before GetRequiredService<AppCoordinator>().");
    }

    [Fact]
    public async Task JsonSettingsStore_IdenticalRepeatedSavesDoNotClobberDistinctBackupSnapshot()
    {
        var protector = new DpapiSecretProtector();
        var store = new JsonSettingsStore(protector, _settingsPath);

        // Save v1 (with custom provider + key)
        var v1 = AppSettings.CreateDefault();
        v1.Providers.Add(new ProviderInstanceConfig
        {
            Id = "custom-v1",
            DisplayName = "CPA",
            Kind = ProviderKind.OpenAiCompatible,
            Enabled = true,
            BaseUrl = "https://cpa.example.com/v1",
            Model = "gpt-4o",
            EncryptedApiKey = "sk-keep-me-safe-12345",
        });
        await store.SaveAsync(v1);

        // Save v2 (modified setting) -> .bak now holds v1
        var v2 = v1.Clone();
        v2.AutoBackTranslate = true;
        await store.SaveAsync(v2);

        var bakAfterV2 = await File.ReadAllTextAsync(_settingsPath + ".bak");
        Assert.Contains("custom-v1", bakAfterV2, StringComparison.Ordinal);
        Assert.Contains("\"AutoBackTranslate\": false", bakAfterV2, StringComparison.Ordinal);

        // Save v2 again with identical payload -> .bak MUST still hold v1 (not overwritten by identical v2)!
        await store.SaveAsync(v2);

        var bakAfterDuplicateSave = await File.ReadAllTextAsync(_settingsPath + ".bak");
        Assert.Equal(bakAfterV2, bakAfterDuplicateSave);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PopTranslate.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Unable to locate PopTranslate.sln.");
    }

    private sealed class StubStartupPrivilegeService : IStartupPrivilegeService
    {
        public bool IsRunningAsAdministrator() => false;
        public bool IsStandardStartupEnabled() => false;
        public void SetStandardStartup(bool enabled, string executablePath) { }
        public bool IsElevatedStartupTaskEnabled() => false;
        public Task<PrivilegeOperationResult> SetElevatedStartupTaskAsync(bool enabled, string executablePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PrivilegeOperationResult(true, "ok"));
        public bool RestartAsAdministrator(string executablePath, string? arguments = null) => false;
    }
}
