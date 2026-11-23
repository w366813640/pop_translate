using PopTranslate.Core.Configuration;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.DependencyInjection;
using PopTranslate.Core.Language;
using PopTranslate.Core.Models;
using PopTranslate.Core.Ocr;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace PopTranslate.Tests.Core;

public sealed class OcrOrchestratorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonSettingsStore _settingsStore;

    public OcrOrchestratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PopTranslateOcrOrchTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _settingsStore = new JsonSettingsStore(Path.Combine(_tempDir, "settings.json"));
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
            // Ignore cleanup errors.
        }
    }

    [Fact]
    public async Task RecognizeAsync_DispatchesToActiveProviderAndMergesOcrLineBreaks()
    {
        var settings = AppSettings.CreateDefault();
        settings.Ocr.ActiveProvider = OcrProviderKind.WindowsNative;
        settings.Ocr.AutoMergeOcrLineBreaks = true;
        await _settingsStore.SaveAsync(settings);

        var winProvider = new FakeOcrProvider(
            OcrProviderKind.WindowsNative,
            recognizeResult: new OcrResult(
                IsSuccess: true,
                Text: "你 好 世 界\n欢 迎 使 用",
                DetectedLanguage: LanguageCode.ChineseSimplified,
                ProviderKind: OcrProviderKind.WindowsNative,
                ErrorMessage: null,
                Elapsed: TimeSpan.FromMilliseconds(15)));

        var visionProvider = new FakeOcrProvider(
            OcrProviderKind.VisionLlm,
            recognizeResult: new OcrResult(
                IsSuccess: true,
                Text: "Vision text",
                DetectedLanguage: LanguageCode.English,
                ProviderKind: OcrProviderKind.VisionLlm,
                ErrorMessage: null,
                Elapsed: TimeSpan.FromMilliseconds(100)));

        var orchestrator = new OcrOrchestrator(_settingsStore, new TextPreprocessor(), [winProvider, visionProvider]);
        var result = await orchestrator.RecognizeAsync(new OcrImage([1, 2, 3], 100, 40));

        Assert.True(result.IsSuccess);
        Assert.Equal(OcrProviderKind.WindowsNative, result.ProviderKind);
        Assert.Equal("你好世界欢迎使用", result.Text);
        Assert.Equal(1, winProvider.RecognizeCalls);
        Assert.Equal(0, visionProvider.RecognizeCalls);
    }

    [Fact]
    public async Task RecognizeAsync_DispatchesToVisionLlmAndSkipsMergeWhenDisabled()
    {
        var settings = AppSettings.CreateDefault();
        settings.Ocr.ActiveProvider = OcrProviderKind.VisionLlm;
        settings.Ocr.AutoMergeOcrLineBreaks = false;
        await _settingsStore.SaveAsync(settings);

        var winProvider = new FakeOcrProvider(OcrProviderKind.WindowsNative);
        var visionProvider = new FakeOcrProvider(
            OcrProviderKind.VisionLlm,
            recognizeResult: new OcrResult(
                IsSuccess: true,
                Text: "Line 1\r\nLine 2",
                DetectedLanguage: LanguageCode.English,
                ProviderKind: OcrProviderKind.VisionLlm,
                ErrorMessage: null,
                Elapsed: TimeSpan.FromMilliseconds(50)));

        var orchestrator = new OcrOrchestrator(_settingsStore, new TextPreprocessor(), [winProvider, visionProvider]);
        var result = await orchestrator.RecognizeAsync(new OcrImage([1, 2, 3], 100, 40));

        Assert.True(result.IsSuccess);
        Assert.Equal(OcrProviderKind.VisionLlm, result.ProviderKind);
        Assert.Equal("Line 1\r\nLine 2", result.Text);
        Assert.Equal(0, winProvider.RecognizeCalls);
        Assert.Equal(1, visionProvider.RecognizeCalls);
    }

    [Fact]
    public async Task RecognizeAsync_MissingActiveProvider_ReturnsClearError()
    {
        var settings = AppSettings.CreateDefault();
        settings.Ocr.ActiveProvider = OcrProviderKind.WindowsNative;
        await _settingsStore.SaveAsync(settings);

        var visionOnly = new FakeOcrProvider(OcrProviderKind.VisionLlm);
        var orchestrator = new OcrOrchestrator(_settingsStore, new TextPreprocessor(), [visionOnly]);

        var result = await orchestrator.RecognizeAsync(new OcrImage([1, 2, 3], 100, 40));

        Assert.False(result.IsSuccess);
        Assert.Contains("WindowsNative", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetInstalledWindowsOcrLanguagesAsync_QueriesWindowsNativeProvider()
    {
        var packs = new[]
        {
            new OcrLanguagePackInfo("en-US", "English (United States)", LanguageCode.English),
            new OcrLanguagePackInfo("zh-Hans-CN", "Chinese (Simplified)", LanguageCode.ChineseSimplified),
        };
        var winProvider = new FakeOcrProvider(OcrProviderKind.WindowsNative, languages: packs);
        var orchestrator = new OcrOrchestrator(_settingsStore, new TextPreprocessor(), [winProvider]);

        var result = await orchestrator.GetInstalledWindowsOcrLanguagesAsync();

        Assert.Equal(2, result.Count);
        Assert.Contains(result, p => p.LanguageTag == "en-US");
        Assert.Contains(result, p => p.LanguageTag == "zh-Hans-CN");
    }

    [Fact]
    public async Task TestActiveProviderAsync_DispatchesToProviderMatchingSuppliedConfig()
    {
        var visionProvider = new FakeOcrProvider(
            OcrProviderKind.VisionLlm,
            healthCheck: new ProviderHealthCheckResult(true, "Vision OK", TimeSpan.FromMilliseconds(25)));

        var orchestrator = new OcrOrchestrator(_settingsStore, new TextPreprocessor(), [visionProvider]);
        var health = await orchestrator.TestActiveProviderAsync(new OcrConfig { ActiveProvider = OcrProviderKind.VisionLlm });

        Assert.True(health.IsSuccess);
        Assert.Equal("Vision OK", health.Message);
    }

    [Fact]
    public void AddPopTranslateCore_RegistersAndResolvesAllCoreServices()
    {
        var services = new ServiceCollection();
        services.AddPopTranslateCore();
        using var sp = services.BuildServiceProvider();

        Assert.IsType<DpapiSecretProtector>(sp.GetRequiredService<ISecretProtector>());
        Assert.IsType<JsonSettingsStore>(sp.GetRequiredService<ISettingsStore>());
        Assert.NotNull(sp.GetRequiredService<IProxyHttpClientFactory>());
        Assert.IsType<LocalLanguageDetector>(sp.GetRequiredService<ILanguageDetector>());
        Assert.IsType<DualTargetLanguageRouter>(sp.GetRequiredService<ILanguageRouter>());
        Assert.IsType<TextPreprocessor>(sp.GetRequiredService<ITextPreprocessor>());
        Assert.NotNull(sp.GetRequiredService<ITranslationOrchestrator>());
        Assert.IsType<OcrOrchestrator>(sp.GetRequiredService<IOcrOrchestrator>());

        var translationProviders = sp.GetServices<ITranslationProvider>().ToList();
        Assert.Equal(3, translationProviders.Count);
        Assert.Contains(translationProviders, p => p.Kind == ProviderKind.FreeTraditional);
        Assert.Contains(translationProviders, p => p.Kind == ProviderKind.OpenAiCompatible);
        Assert.Contains(translationProviders, p => p.Kind == ProviderKind.DeepL);

        var ocrProviders = sp.GetServices<IOcrProvider>().ToList();
        Assert.Contains(ocrProviders, p => p.Kind == OcrProviderKind.VisionLlm);
    }

    private sealed class FakeOcrProvider(
        OcrProviderKind kind,
        OcrResult? recognizeResult = null,
        IReadOnlyList<OcrLanguagePackInfo>? languages = null,
        ProviderHealthCheckResult? healthCheck = null) : IOcrProvider
    {
        public int RecognizeCalls { get; private set; }

        public OcrProviderKind Kind => kind;

        public string DisplayName => kind.ToString();

        public Task<OcrResult> RecognizeAsync(OcrImage image, OcrConfig config, CancellationToken cancellationToken = default)
        {
            RecognizeCalls++;
            return Task.FromResult(recognizeResult ?? new OcrResult(true, string.Empty, null, kind, null, TimeSpan.Zero));
        }

        public Task<IReadOnlyList<OcrLanguagePackInfo>> GetAvailableLanguagesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(languages ?? Array.Empty<OcrLanguagePackInfo>());

        public Task<ProviderHealthCheckResult> TestAsync(OcrConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult(healthCheck ?? new ProviderHealthCheckResult(true, "OK", TimeSpan.Zero));
    }
}
