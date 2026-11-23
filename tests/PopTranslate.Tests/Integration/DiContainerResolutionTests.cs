using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PopTranslate.Core.Configuration;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.DependencyInjection;
using PopTranslate.Core.Diff;
using PopTranslate.Core.Language;
using PopTranslate.Core.Models;
using PopTranslate.Core.Networking;
using PopTranslate.Core.Ocr;
using PopTranslate.Core.Polish;
using PopTranslate.Core.Translation;
using PopTranslate.Win32.DependencyInjection;
using PopTranslate.Win32.Guards;
using PopTranslate.Win32.Hotkeys;
using PopTranslate.Win32.Ocr;
using PopTranslate.Win32.Screenshot;
using PopTranslate.Win32.Selection;
using PopTranslate.Win32.Startup;
using PopTranslate.Win32.Windowing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace PopTranslate.Tests.Integration;

public sealed class DiContainerResolutionTests
{
    [Fact]
    public void FullDiContainer_ResolvesEveryContractInPopTranslateCoreContracts()
    {
        var services = new ServiceCollection();
        services.AddPopTranslateCore();
        services.AddPopTranslateWin32();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        // Reflection check: confirm every single public interface in PopTranslate.Core.Contracts resolves via DI.
        var contractInterfaces = typeof(ITranslationOrchestrator).Assembly
            .GetTypes()
            .Where(t => t.IsInterface && t.IsPublic && t.Namespace == "PopTranslate.Core.Contracts")
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(21, contractInterfaces.Count);

        foreach (var contractType in contractInterfaces)
        {
            var resolved = provider.GetRequiredService(contractType);
            Assert.NotNull(resolved);
            Assert.IsAssignableFrom(contractType, resolved);
        }

        // Assert exact concrete wiring for every Core & Win32 contract (all 21 contracts).
        Assert.IsType<DpapiSecretProtector>(provider.GetRequiredService<ISecretProtector>());
        Assert.IsType<JsonSettingsStore>(provider.GetRequiredService<ISettingsStore>());
        Assert.IsType<ProxyHttpClientFactory>(provider.GetRequiredService<IProxyHttpClientFactory>());
        Assert.IsType<LocalLanguageDetector>(provider.GetRequiredService<ILanguageDetector>());
        Assert.IsType<DualTargetLanguageRouter>(provider.GetRequiredService<ILanguageRouter>());
        Assert.IsType<TextPreprocessor>(provider.GetRequiredService<ITextPreprocessor>());
        Assert.IsType<TranslationOrchestrator>(provider.GetRequiredService<ITranslationOrchestrator>());
        Assert.IsType<BackTranslationService>(provider.GetRequiredService<IBackTranslationService>());
        Assert.IsType<OpenAiCompatiblePolishProvider>(provider.GetRequiredService<IPolishProvider>());
        Assert.IsType<PolishOrchestrator>(provider.GetRequiredService<IPolishOrchestrator>());
        Assert.IsType<LocalTextDiffService>(provider.GetRequiredService<ITextDiffService>());
        Assert.IsType<OcrOrchestrator>(provider.GetRequiredService<IOcrOrchestrator>());

        Assert.IsType<ForegroundAppGuard>(provider.GetRequiredService<IForegroundAppGuard>());
        Assert.IsType<WindowHelperService>(provider.GetRequiredService<IWindowHelperService>());
        Assert.IsType<GlobalHotkeyService>(provider.GetRequiredService<IGlobalHotkeyService>());
        Assert.IsType<TextSelectionService>(provider.GetRequiredService<ITextSelectionService>());
        Assert.IsType<MouseSelectionHoverMonitor>(provider.GetRequiredService<ISelectionHoverMonitor>());
        Assert.IsType<ScreenshotCaptureService>(provider.GetRequiredService<IScreenshotCaptureService>());
        Assert.IsType<StartupPrivilegeService>(provider.GetRequiredService<IStartupPrivilegeService>());
    }

    [Fact]
    public void FullDiContainer_ResolvesAllBuiltInTranslationAndOcrProviders()
    {
        var services = new ServiceCollection();
        services.AddPopTranslateCore();
        services.AddPopTranslateWin32();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        var translationProviders = provider.GetServices<ITranslationProvider>().ToList();
        Assert.Equal(3, translationProviders.Count);
        Assert.Contains(translationProviders, p => p is OpenAiCompatibleTranslationProvider && p.Kind == ProviderKind.OpenAiCompatible && p.SupportsStreaming);
        Assert.Contains(translationProviders, p => p is FreeTraditionalTranslationProvider && p.Kind == ProviderKind.FreeTraditional && !p.SupportsStreaming);
        Assert.Contains(translationProviders, p => p is DeepLTranslationProvider && p.Kind == ProviderKind.DeepL && !p.SupportsStreaming);

        var polishProviders = provider.GetServices<IPolishProvider>().ToList();
        var polishProvider = Assert.Single(polishProviders);
        Assert.IsType<OpenAiCompatiblePolishProvider>(polishProvider);
        Assert.Equal(ProviderKind.OpenAiCompatible, polishProvider.Kind);
        Assert.True(polishProvider.SupportsStreaming);

        var ocrProviders = provider.GetServices<IOcrProvider>().ToList();
        Assert.Equal(2, ocrProviders.Count);
        Assert.Contains(ocrProviders, p => p is WindowsOcrProvider && p.Kind == OcrProviderKind.WindowsNative);
        Assert.Contains(ocrProviders, p => p is VisionLlmOcrProvider && p.Kind == OcrProviderKind.VisionLlm);
    }

    [Fact]
    public async Task OcrOrchestrator_WithRealWindowsOcrProvider_RecognizesRenderedTextAndMergesLineBreaks()
    {
        using var tempScope = new TempSettingsScope();
        var services = new ServiceCollection();
        services.AddPopTranslateCore();
        services.AddPopTranslateWin32();
        services.Replace(ServiceDescriptor.Singleton<ISettingsStore>(sp =>
            new JsonSettingsStore(sp.GetRequiredService<ISecretProtector>(), tempScope.SettingsPath)));

        using var provider = services.BuildServiceProvider();
        var settingsStore = provider.GetRequiredService<ISettingsStore>();
        var ocrOrchestrator = provider.GetRequiredService<IOcrOrchestrator>();

        // Verify Windows OCR packs are exposed through OcrOrchestrator -> WindowsOcrProvider.
        var installedPacks = await ocrOrchestrator.GetInstalledWindowsOcrLanguagesAsync();
        Assert.NotEmpty(installedPacks);

        var health = await ocrOrchestrator.TestActiveProviderAsync(new OcrConfig
        {
            ActiveProvider = OcrProviderKind.WindowsNative,
            PreferredWindowsOcrLanguageTag = "en-US",
        });
        Assert.True(health.IsSuccess, health.Message);

        // Render two lines of crisp black text on a white canvas using Win32 GDI -> ScreenCaptureGdi PNG encoder.
        var pngBytes = await RenderedTextImageHelper.RenderLinesToPngAsync("HELLO WINDOWS", "TRANSLATION TEST");
        Assert.True(pngBytes.Length > 8);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, pngBytes[..4]);

        var image = new OcrImage(
            ImageBytes: pngBytes,
            PixelWidth: RenderedTextImageHelper.DefaultWidth,
            PixelHeight: RenderedTextImageHelper.DefaultHeight,
            DpiScale: 1.0,
            PreferredLanguage: LanguageCode.English,
            MimeType: "image/png");

        // 1. AutoMergeOcrLineBreaks = true merges the two rendered lines into one line.
        var mergedSettings = AppSettings.CreateDefault();
        mergedSettings.Ocr.ActiveProvider = OcrProviderKind.WindowsNative;
        mergedSettings.Ocr.PreferredWindowsOcrLanguageTag = "en-US";
        mergedSettings.Ocr.AutoMergeOcrLineBreaks = true;
        await settingsStore.SaveAsync(mergedSettings);

        var mergedResult = await ocrOrchestrator.RecognizeAsync(image);
        Assert.True(mergedResult.IsSuccess, mergedResult.ErrorMessage);
        Assert.Equal(OcrProviderKind.WindowsNative, mergedResult.ProviderKind);
        Assert.Contains("HELLO", mergedResult.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TRANSLATION", mergedResult.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\n', mergedResult.Text);

        // 2. AutoMergeOcrLineBreaks = false keeps the multi-line break from Windows OCR.
        var rawSettings = mergedSettings.Clone();
        rawSettings.Ocr.AutoMergeOcrLineBreaks = false;
        await settingsStore.SaveAsync(rawSettings);

        var unmergedResult = await ocrOrchestrator.RecognizeAsync(image);
        Assert.True(unmergedResult.IsSuccess, unmergedResult.ErrorMessage);
        Assert.Contains("HELLO", unmergedResult.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains('\n', unmergedResult.Text);
    }

    [Fact]
    public async Task SettingsStore_AndRealDpapiSecretProtector_RoundTripNeverPersistsPlaintextSecrets()
    {
        using var tempScope = new TempSettingsScope();
        var services = new ServiceCollection();
        services.AddPopTranslateCore();
        services.AddPopTranslateWin32();
        services.Replace(ServiceDescriptor.Singleton<ISettingsStore>(sp =>
            new JsonSettingsStore(sp.GetRequiredService<ISecretProtector>(), tempScope.SettingsPath)));

        const string openAiSecret = "sk-integration-openai-secret-98765";
        const string deepLSecret = "deepl-integration-key-12345:fx";
        const string visionSecret = "sk-integration-vision-secret-54321";
        const string proxySecret = "proxy-integration-pass-abcde";

        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<ISettingsStore>();
        var protector = provider.GetRequiredService<ISecretProtector>();
        var proxyFactory = Assert.IsType<ProxyHttpClientFactory>(provider.GetRequiredService<IProxyHttpClientFactory>());

        AppSettings? raisedSnapshot = null;
        store.SettingsChanged += (_, updated) => raisedSnapshot = updated;

        var settings = AppSettings.CreateDefault();
        settings.Providers.Add(new ProviderInstanceConfig
        {
            Id = "openai-int",
            DisplayName = "OpenAI Int",
            Kind = ProviderKind.OpenAiCompatible,
            Enabled = true,
            SortOrder = 1,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o-mini",
            EncryptedApiKey = openAiSecret, // Intentionally supplied plaintext; store must DPAPI-protect before writing!
        });
        settings.Providers.Add(new ProviderInstanceConfig
        {
            Id = "deepl-int",
            DisplayName = "DeepL Int",
            Kind = ProviderKind.DeepL,
            Enabled = true,
            SortOrder = 2,
            EncryptedApiKey = protector.Protect(deepLSecret),
        });
        settings.Ocr.ActiveProvider = OcrProviderKind.VisionLlm;
        settings.Ocr.EncryptedVisionApiKey = visionSecret;
        settings.Proxy.Mode = ProxyMode.Http;
        settings.Proxy.Host = "127.0.0.1";
        settings.Proxy.Port = 8899;
        settings.Proxy.Username = "poptranslate-user";
        settings.Proxy.EncryptedPassword = proxySecret;

        await store.SaveAsync(settings);

        Assert.NotNull(raisedSnapshot);
        Assert.True(File.Exists(tempScope.SettingsPath));

        // Verify raw JSON on disk contains ZERO plaintext secrets and uses dpapi: prefix everywhere.
        var rawJson = await File.ReadAllTextAsync(tempScope.SettingsPath);
        Assert.DoesNotContain(openAiSecret, rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain(deepLSecret, rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain(visionSecret, rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain(proxySecret, rawJson, StringComparison.Ordinal);
        Assert.Contains(DpapiSecretProtector.Prefix, rawJson, StringComparison.Ordinal);

        // Reload using a brand new store + real DPAPI protector and verify exact decryption.
        var reloadedStore = new JsonSettingsStore(new DpapiSecretProtector(), tempScope.SettingsPath);
        var reloaded = await reloadedStore.LoadAsync();

        var reloadedOpenAi = reloaded.Providers.Single(p => p.Id == "openai-int");
        var reloadedDeepL = reloaded.Providers.Single(p => p.Id == "deepl-int");

        Assert.StartsWith(DpapiSecretProtector.Prefix, reloadedOpenAi.EncryptedApiKey, StringComparison.Ordinal);
        Assert.StartsWith(DpapiSecretProtector.Prefix, reloadedDeepL.EncryptedApiKey, StringComparison.Ordinal);
        Assert.StartsWith(DpapiSecretProtector.Prefix, reloaded.Ocr.EncryptedVisionApiKey, StringComparison.Ordinal);
        Assert.StartsWith(DpapiSecretProtector.Prefix, reloaded.Proxy.EncryptedPassword, StringComparison.Ordinal);

        Assert.Equal(openAiSecret, protector.Unprotect(reloadedOpenAi.EncryptedApiKey));
        Assert.Equal(deepLSecret, protector.Unprotect(reloadedDeepL.EncryptedApiKey));
        Assert.Equal(visionSecret, protector.Unprotect(reloaded.Ocr.EncryptedVisionApiKey));
        Assert.Equal(proxySecret, protector.Unprotect(reloaded.Proxy.EncryptedPassword));

        // Verify ProxyHttpClientFactory decrypts the persisted DPAPI proxy password properly.
        using var handler = proxyFactory.CreateHandler(reloaded.Proxy);
        var webProxy = Assert.IsType<WebProxy>(handler.Proxy);
        var cred = Assert.IsType<NetworkCredential>(webProxy.Credentials);
        Assert.Equal("poptranslate-user", cred.UserName);
        Assert.Equal(proxySecret, cred.Password);
    }

    [Fact]
    public async Task TranslationOrchestrator_EndToEndDiPipeline_RunsAllProvidersConcurrentlyAndHandlesCancellation()
    {
        using var tempScope = new TempSettingsScope();
        var releaseSlowOpenAiSecondChunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowOpenAiStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? observedOpenAiAuth = null;
        string? observedDeepLAuth = null;

        var handler = new StubHttpMessageHandler(async (req, ct) =>
        {
            var uri = req.RequestUri!.AbsoluteUri;

            if (uri.StartsWith("https://mock-openai.local/v1/chat/completions", StringComparison.Ordinal))
            {
                observedOpenAiAuth = req.Headers.Authorization?.ToString();
                var sseStream = new AsyncChunkedSseStream(async writer =>
                {
                    await writer("data: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}\n\n", ct);
                    slowOpenAiStarted.TrySetResult();
                    await releaseSlowOpenAiSecondChunk.Task.WaitAsync(ct);
                    await writer("data: {\"choices\":[{\"delta\":{\"content\":\"世界\"}}]}\n\n", ct);
                    await writer("data: [DONE]\n\n", ct);
                });

                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(sseStream),
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                return response;
            }

            if (uri.StartsWith("https://api-free.deepl.com/v2/translate", StringComparison.Ordinal))
            {
                observedDeepLAuth = req.Headers.Authorization?.ToString();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new
                        {
                            translations = new[]
                            {
                                new { detected_source_language = "EN", text = "你好，世界" },
                            },
                        }),
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            if (uri.StartsWith("https://translate.googleapis.com/translate_a/single", StringComparison.Ordinal))
            {
                // Simulate Google 429 to exercise FreeTraditional fallback to Bing inside the full DI graph.
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            }

            if (uri.StartsWith("https://edge.microsoft.com/translate/auth", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("mock-bing-jwt-token", Encoding.UTF8, "text/plain"),
                };
            }

            if (uri.StartsWith("https://api-edge.cognitive.microsofttranslator.com/translate", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "[{\"detectedLanguage\":{\"language\":\"en\"},\"translations\":[{\"text\":\"哈喽世界\",\"to\":\"zh-Hans\"}]}]",
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var services = new ServiceCollection();
        services.AddPopTranslateCore();
        services.AddPopTranslateWin32();
        services.Replace(ServiceDescriptor.Singleton<ISettingsStore>(sp =>
            new JsonSettingsStore(sp.GetRequiredService<ISecretProtector>(), tempScope.SettingsPath)));
        services.Replace(ServiceDescriptor.Singleton<IProxyHttpClientFactory>(new StubProxyHttpClientFactory(handler)));

        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<ISettingsStore>();
        var protector = provider.GetRequiredService<ISecretProtector>();
        var orchestrator = provider.GetRequiredService<ITranslationOrchestrator>();

        var settings = AppSettings.CreateDefault();
        settings.MergePdfLineBreaks = true;
        settings.PrimaryTargetLanguage = LanguageCode.ChineseSimplified;
        settings.SecondaryTargetLanguage = LanguageCode.English;
        settings.Providers =
        [
            new ProviderInstanceConfig
            {
                Id = "openai-slow",
                DisplayName = "OpenAI Streaming",
                Kind = ProviderKind.OpenAiCompatible,
                Enabled = true,
                SortOrder = 0,
                BaseUrl = "https://mock-openai.local/v1",
                Model = "gpt-4o-mini",
                EncryptedApiKey = protector.Protect("sk-e2e-openai-key"),
            },
            new ProviderInstanceConfig
            {
                Id = "deepl-fast",
                DisplayName = "DeepL Fast",
                Kind = ProviderKind.DeepL,
                Enabled = true,
                SortOrder = 1,
                DeepLTier = DeepLEndpointTier.Auto,
                EncryptedApiKey = protector.Protect("deepl-e2e-key:fx"),
            },
            new ProviderInstanceConfig
            {
                Id = "free-fallback",
                DisplayName = "Free Google/Bing",
                Kind = ProviderKind.FreeTraditional,
                Enabled = true,
                SortOrder = 2,
                FreeEngine = FreeTranslatorEngine.Auto,
            },
        ];
        await store.SaveAsync(settings);

        // Part A: Concurrent execution across all 3 real providers.
        var updates = new List<ProviderStreamUpdate>();
        bool deepLCompletedWhileOpenAiStillStreaming = false;
        bool openAiReceivedFirstDelta = false;

        await foreach (var update in orchestrator.TranslateParallelAsync(
            new TranslationRequest(
                Text: "Hello auto-\nmatic world",
                SourceLanguage: LanguageCode.Auto,
                TargetLanguage: LanguageCode.Auto,
                ApplyTextPreprocessing: true)))
        {
            updates.Add(update);

            if (update.ProviderInstanceId == "openai-slow" &&
                update.State == ProviderStreamState.Streaming &&
                update.AccumulatedText == "你好")
            {
                openAiReceivedFirstDelta = true;
            }

            if (update.ProviderInstanceId == "deepl-fast" &&
                update.State == ProviderStreamState.Completed)
            {
                if (!releaseSlowOpenAiSecondChunk.Task.IsCompleted)
                {
                    deepLCompletedWhileOpenAiStillStreaming = true;
                    releaseSlowOpenAiSecondChunk.TrySetResult();
                }
            }
        }

        Assert.True(openAiReceivedFirstDelta);
        Assert.True(deepLCompletedWhileOpenAiStillStreaming, "DeepL must complete concurrently while slow OpenAI SSE is still streaming.");
        Assert.Equal("Bearer sk-e2e-openai-key", observedOpenAiAuth);
        Assert.Equal("DeepL-Auth-Key deepl-e2e-key:fx", observedDeepLAuth);

        var finalByProvider = updates
            .GroupBy(u => u.ProviderInstanceId)
            .ToDictionary(g => g.Key, g => g.Last());

        Assert.Equal(ProviderStreamState.Completed, finalByProvider["openai-slow"].State);
        Assert.Equal("你好世界", finalByProvider["openai-slow"].AccumulatedText);
        Assert.Equal(LanguageCode.ChineseSimplified, finalByProvider["openai-slow"].TargetLanguage);

        Assert.Equal(ProviderStreamState.Completed, finalByProvider["deepl-fast"].State);
        Assert.Equal("你好，世界", finalByProvider["deepl-fast"].AccumulatedText);

        Assert.Equal(ProviderStreamState.Completed, finalByProvider["free-fallback"].State);
        Assert.Equal("哈喽世界", finalByProvider["free-fallback"].AccumulatedText);

        // Part B: Prompt cancellation of an active streaming translation.
        releaseSlowOpenAiSecondChunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        slowOpenAiStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var cancelCts = new CancellationTokenSource();
        var observedUpdatesBeforeCancel = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var update in orchestrator.TranslateParallelAsync(
                new TranslationRequest(
                    Text: "Cancel this stream",
                    SourceLanguage: LanguageCode.English,
                    TargetLanguage: LanguageCode.ChineseSimplified,
                    ProviderInstanceIds: ["openai-slow"]),
                cancelCts.Token))
            {
                observedUpdatesBeforeCancel++;
                if (update.State == ProviderStreamState.Streaming)
                {
                    cancelCts.Cancel();
                }
            }
        });

        Assert.True(observedUpdatesBeforeCancel >= 2);
    }

    [Fact]
    public async Task TranslationOrchestrator_EndToEndDiPipeline_IsolatesFailingProviderAndRoutesChineseToEnglish()
    {
        using var tempScope = new TempSettingsScope();
        string? deepLRequestBody = null;

        var handler = new StubHttpMessageHandler(async (req, ct) =>
        {
            var uri = req.RequestUri!.AbsoluteUri;
            if (uri.StartsWith("https://mock-failing.local/v1/chat/completions", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("{\"error\":{\"message\":\"Upstream overloaded\"}}", Encoding.UTF8, "application/json"),
                };
            }

            if (uri.StartsWith("https://api-free.deepl.com/v2/translate", StringComparison.Ordinal))
            {
                deepLRequestBody = await req.Content!.ReadAsStringAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new
                        {
                            translations = new[]
                            {
                                new { detected_source_language = "ZH", text = "Hello Windows translation" },
                            },
                        }),
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var services = new ServiceCollection();
        services.AddPopTranslateCore();
        services.AddPopTranslateWin32();
        services.Replace(ServiceDescriptor.Singleton<ISettingsStore>(sp =>
            new JsonSettingsStore(sp.GetRequiredService<ISecretProtector>(), tempScope.SettingsPath)));
        services.Replace(ServiceDescriptor.Singleton<IProxyHttpClientFactory>(new StubProxyHttpClientFactory(handler)));

        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<ISettingsStore>();
        var protector = provider.GetRequiredService<ISecretProtector>();
        var orchestrator = provider.GetRequiredService<ITranslationOrchestrator>();

        var settings = AppSettings.CreateDefault();
        settings.PrimaryTargetLanguage = LanguageCode.ChineseSimplified;
        settings.SecondaryTargetLanguage = LanguageCode.English;
        settings.Providers =
        [
            new ProviderInstanceConfig
            {
                Id = "failing-llm",
                DisplayName = "Failing LLM",
                Kind = ProviderKind.OpenAiCompatible,
                Enabled = true,
                SortOrder = 0,
                BaseUrl = "https://mock-failing.local/v1",
                Model = "gpt-4o-mini",
                EncryptedApiKey = protector.Protect("sk-fail-key"),
            },
            new ProviderInstanceConfig
            {
                Id = "healthy-deepl",
                DisplayName = "Healthy DeepL",
                Kind = ProviderKind.DeepL,
                Enabled = true,
                SortOrder = 1,
                EncryptedApiKey = protector.Protect("deepl-healthy:fx"),
            },
        ];
        await store.SaveAsync(settings);

        var batch = await orchestrator.TranslateAllAsync(new TranslationRequest(
            Text: "你好，Windows 11 桌面翻译测试",
            SourceLanguage: LanguageCode.Auto,
            TargetLanguage: LanguageCode.Auto));

        Assert.Equal(LanguageCode.English, batch.EffectiveTargetLanguage);
        Assert.NotNull(deepLRequestBody);
        Assert.Contains("\"EN-US\"", deepLRequestBody, StringComparison.Ordinal);

        var failedCard = Assert.Single(batch.Results, r => r.ProviderInstanceId == "failing-llm");
        Assert.False(failedCard.IsSuccess);
        Assert.Contains("500", failedCard.ErrorMessage, StringComparison.Ordinal);

        var healthyCard = Assert.Single(batch.Results, r => r.ProviderInstanceId == "healthy-deepl");
        Assert.True(healthyCard.IsSuccess, healthyCard.ErrorMessage);
        Assert.Equal("Hello Windows translation", healthyCard.TranslatedText);
    }

    [Fact]
    public async Task OcrOrchestrator_WithVisionLlmOcrProvider_DecryptsApiKeyAndMergesOcrLines()
    {
        using var tempScope = new TempSettingsScope();
        string? observedAuth = null;
        string? observedBody = null;

        var handler = new StubHttpMessageHandler(async (req, ct) =>
        {
            observedAuth = req.Headers.Authorization?.ToString();
            observedBody = await req.Content!.ReadAsStringAsync(ct);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        choices = new[]
                        {
                            new
                            {
                                message = new
                                {
                                    content = "```text\nThis is an auto-\nmatic Vision OCR test.\n```",
                                },
                            },
                        },
                    }),
                    Encoding.UTF8,
                    "application/json"),
            };
        });

        var services = new ServiceCollection();
        services.AddPopTranslateCore();
        services.AddPopTranslateWin32();
        services.Replace(ServiceDescriptor.Singleton<ISettingsStore>(sp =>
            new JsonSettingsStore(sp.GetRequiredService<ISecretProtector>(), tempScope.SettingsPath)));
        services.Replace(ServiceDescriptor.Singleton<IProxyHttpClientFactory>(new StubProxyHttpClientFactory(handler)));

        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<ISettingsStore>();
        var protector = provider.GetRequiredService<ISecretProtector>();
        var ocrOrchestrator = provider.GetRequiredService<IOcrOrchestrator>();

        var settings = AppSettings.CreateDefault();
        settings.Ocr.ActiveProvider = OcrProviderKind.VisionLlm;
        settings.Ocr.VisionBaseUrl = "https://mock-vision.local/v1";
        settings.Ocr.VisionModel = "gpt-4o-mini";
        settings.Ocr.EncryptedVisionApiKey = protector.Protect("sk-vision-e2e-key");
        settings.Ocr.AutoMergeOcrLineBreaks = true;
        await store.SaveAsync(settings);

        var tinyPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");
        var result = await ocrOrchestrator.RecognizeAsync(new OcrImage(tinyPng, 1, 1, 1.0, MimeType: "image/png"));

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(OcrProviderKind.VisionLlm, result.ProviderKind);
        Assert.Equal("Bearer sk-vision-e2e-key", observedAuth);
        Assert.NotNull(observedBody);
        Assert.Contains("data:image/png;base64,", observedBody, StringComparison.Ordinal);
        Assert.Equal("This is an automatic Vision OCR test.", result.Text);
    }

    [Fact]
    public async Task BackTranslationService_EndToEndDiPipeline_ReverseStreamsOpenAiAndDeepLWithDpapiKeys()
    {
        using var tempScope = new TempSettingsScope();
        var openAiAuthHeaders = new List<string?>();
        var openAiRequestBodies = new List<string>();
        var deepLAuthHeaders = new List<string?>();
        var deepLRequestBodies = new List<string>();
        var openAiCallCount = 0;
        var deepLCallCount = 0;

        var handler = new StubHttpMessageHandler(async (req, ct) =>
        {
            var uri = req.RequestUri!.AbsoluteUri;
            var body = req.Content is not null ? await req.Content.ReadAsStringAsync(ct) : string.Empty;

            if (uri.StartsWith("https://mock-bt-openai.local/v1/chat/completions", StringComparison.Ordinal))
            {
                var callIndex = Interlocked.Increment(ref openAiCallCount);
                lock (openAiAuthHeaders)
                {
                    openAiAuthHeaders.Add(req.Headers.Authorization?.ToString());
                    openAiRequestBodies.Add(body);
                }

                var isReverse = callIndex >= 2;
                var sseStream = new AsyncChunkedSseStream(async writer =>
                {
                    if (!isReverse)
                    {
                        await writer("data: {\"choices\":[{\"delta\":{\"content\":\"早上好，\"}}]}\n\n", ct);
                        await writer("data: {\"choices\":[{\"delta\":{\"content\":\"团队！\"}}]}\n\n", ct);
                    }
                    else
                    {
                        await writer("data: {\"choices\":[{\"delta\":{\"content\":\"Good morning, \"}}]}\n\n", ct);
                        await writer("data: {\"choices\":[{\"delta\":{\"content\":\"team!\"}}]}\n\n", ct);
                    }

                    await writer("data: [DONE]\n\n", ct);
                });

                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(sseStream),
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                return response;
            }

            if (uri.StartsWith("https://api-free.deepl.com/v2/translate", StringComparison.Ordinal))
            {
                var callIndex = Interlocked.Increment(ref deepLCallCount);
                lock (deepLAuthHeaders)
                {
                    deepLAuthHeaders.Add(req.Headers.Authorization?.ToString());
                    deepLRequestBodies.Add(body);
                }

                var isReverse = callIndex >= 2;
                var json = isReverse
                    ? JsonSerializer.Serialize(new
                    {
                        translations = new[] { new { detected_source_language = "ZH", text = "Good morning team" } },
                    })
                    : JsonSerializer.Serialize(new
                    {
                        translations = new[] { new { detected_source_language = "EN", text = "早安团队" } },
                    });

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var services = new ServiceCollection();
        services.AddPopTranslateCore();
        services.AddPopTranslateWin32();
        services.Replace(ServiceDescriptor.Singleton<ISettingsStore>(sp =>
            new JsonSettingsStore(sp.GetRequiredService<ISecretProtector>(), tempScope.SettingsPath)));
        services.Replace(ServiceDescriptor.Singleton<IProxyHttpClientFactory>(new StubProxyHttpClientFactory(handler)));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        var store = provider.GetRequiredService<ISettingsStore>();
        var protector = provider.GetRequiredService<ISecretProtector>();
        var translator = provider.GetRequiredService<ITranslationOrchestrator>();
        var backTranslator = provider.GetRequiredService<IBackTranslationService>();

        var settings = AppSettings.CreateDefault();
        settings.PrimaryTargetLanguage = LanguageCode.ChineseSimplified;
        settings.SecondaryTargetLanguage = LanguageCode.English;
        settings.Providers =
        [
            new ProviderInstanceConfig
            {
                Id = "openai-bt",
                DisplayName = "OpenAI BT",
                Kind = ProviderKind.OpenAiCompatible,
                Enabled = true,
                SortOrder = 0,
                BaseUrl = "https://mock-bt-openai.local/v1",
                Model = "gpt-4o-mini",
                EncryptedApiKey = protector.Protect("sk-bt-openai-secret"),
            },
            new ProviderInstanceConfig
            {
                Id = "deepl-bt",
                DisplayName = "DeepL BT",
                Kind = ProviderKind.DeepL,
                Enabled = true,
                SortOrder = 1,
                DeepLTier = DeepLEndpointTier.Auto,
                EncryptedApiKey = protector.Protect("deepl-bt-secret:fx"),
            },
        ];
        await store.SaveAsync(settings);

        // 1. Forward translate EN -> ZH through full DI pipeline
        var forwardBatch = await translator.TranslateAllAsync(new TranslationRequest(
            Text: "Good morning, team!",
            SourceLanguage: LanguageCode.English,
            TargetLanguage: LanguageCode.ChineseSimplified));

        var openAiForward = Assert.Single(forwardBatch.Results, r => r.ProviderInstanceId == "openai-bt");
        var deepLForward = Assert.Single(forwardBatch.Results, r => r.ProviderInstanceId == "deepl-bt");
        Assert.True(openAiForward.IsSuccess, openAiForward.ErrorMessage);
        Assert.Equal("早上好，团队！", openAiForward.TranslatedText);
        Assert.True(deepLForward.IsSuccess, deepLForward.ErrorMessage);
        Assert.Equal("早安团队", deepLForward.TranslatedText);

        // 2. Reverse streaming back-translation ZH -> EN on OpenAI instance
        var openAiBackUpdates = new List<ProviderStreamUpdate>();
        await foreach (var update in backTranslator.BackTranslateStreamAsync(new BackTranslationRequest(
            ProviderInstanceId: "openai-bt",
            TranslatedText: openAiForward.TranslatedText,
            TranslatedLanguage: LanguageCode.ChineseSimplified,
            BackTargetLanguage: LanguageCode.English)))
        {
            openAiBackUpdates.Add(update);
        }

        Assert.Equal(ProviderStreamState.Pending, openAiBackUpdates[0].State);
        Assert.Equal(2, openAiBackUpdates.Count(u => u.State == ProviderStreamState.Streaming));
        Assert.Equal(ProviderStreamState.Completed, openAiBackUpdates[^1].State);
        Assert.Equal("Good morning, team!", openAiBackUpdates[^1].AccumulatedText);
        Assert.Equal(LanguageCode.English, openAiBackUpdates[^1].TargetLanguage);
        Assert.All(openAiBackUpdates, u => Assert.Equal("openai-bt", u.ProviderInstanceId));

        // 3. Reverse back-translation ZH -> Auto (router resolves EN) on DeepL instance
        var deepLBackUpdates = new List<ProviderStreamUpdate>();
        await foreach (var update in backTranslator.BackTranslateStreamAsync(new BackTranslationRequest(
            ProviderInstanceId: "deepl-bt",
            TranslatedText: deepLForward.TranslatedText,
            TranslatedLanguage: LanguageCode.ChineseSimplified,
            BackTargetLanguage: LanguageCode.Auto)))
        {
            deepLBackUpdates.Add(update);
        }

        Assert.Equal(ProviderStreamState.Pending, deepLBackUpdates[0].State);
        Assert.Equal(ProviderStreamState.Completed, deepLBackUpdates[^1].State);
        Assert.Equal("Good morning team", deepLBackUpdates[^1].AccumulatedText);
        Assert.Equal(LanguageCode.English, deepLBackUpdates[^1].TargetLanguage);

        Assert.Equal(2, openAiAuthHeaders.Count);
        Assert.All(openAiAuthHeaders, h => Assert.Equal("Bearer sk-bt-openai-secret", h));
        using (var openAiReverseDoc = JsonDocument.Parse(openAiRequestBodies[1]))
        {
            var reverseSystemPrompt = openAiReverseDoc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
            var reverseUserContent = openAiReverseDoc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
            Assert.Contains("English", reverseSystemPrompt ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("早上好，团队！", reverseUserContent);
        }

        Assert.Equal(2, deepLAuthHeaders.Count);
        Assert.All(deepLAuthHeaders, h => Assert.Equal("DeepL-Auth-Key deepl-bt-secret:fx", h));
        using (var deepLReverseDoc = JsonDocument.Parse(deepLRequestBodies[1]))
        {
            Assert.Equal("EN-US", deepLReverseDoc.RootElement.GetProperty("target_lang").GetString());
            Assert.Equal("早安团队", deepLReverseDoc.RootElement.GetProperty("text")[0].GetString());
        }
    }

    [Fact]
    public async Task PolishOrchestrator_EndToEndDiPipeline_FiltersEligibleLlmsStreamsConcurrentlyIsolatesFailureAndPropagatesCustomPrompt()
    {
        using var tempScope = new TempSettingsScope();
        var releaseSlowLlmSecondChunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failSecondLlm = false;
        var forbiddenNonLlmCalled = false;
        var observedSystemPrompts = new List<(string Endpoint, string SystemPrompt, string? Auth)>();

        var handler = new StubHttpMessageHandler(async (req, ct) =>
        {
            var uri = req.RequestUri!.AbsoluteUri;

            if (uri.Contains("deepl.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Contains("googleapis.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Contains("microsoft.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Contains("microsofttranslator.com", StringComparison.OrdinalIgnoreCase))
            {
                forbiddenNonLlmCalled = true;
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            var body = req.Content is not null ? await req.Content.ReadAsStringAsync(ct) : string.Empty;
            using var doc = JsonDocument.Parse(body);
            var systemPrompt = doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString() ?? string.Empty;
            lock (observedSystemPrompts)
            {
                observedSystemPrompts.Add((uri, systemPrompt, req.Headers.Authorization?.ToString()));
            }

            if (uri.StartsWith("https://mock-llm-slow.local/v1/chat/completions", StringComparison.Ordinal))
            {
                var sseStream = new AsyncChunkedSseStream(async writer =>
                {
                    await writer("data: {\"choices\":[{\"delta\":{\"content\":\"Polished \"}}]}\n\n", ct);
                    await releaseSlowLlmSecondChunk.Task.WaitAsync(ct);
                    await writer("data: {\"choices\":[{\"delta\":{\"content\":\"slow output.\"}}]}\n\n", ct);
                    await writer("data: [DONE]\n\n", ct);
                });

                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(sseStream),
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                return response;
            }

            if (uri.StartsWith("https://mock-llm-fast.local/v1/chat/completions", StringComparison.Ordinal))
            {
                if (failSecondLlm)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent("{\"error\":{\"message\":\"Secondary LLM overloaded\"}}", Encoding.UTF8, "application/json"),
                    };
                }

                var sseStream = new AsyncChunkedSseStream(async writer =>
                {
                    await writer("data: {\"choices\":[{\"delta\":{\"content\":\"Fast polished text.\"}}]}\n\n", ct);
                    await writer("data: [DONE]\n\n", ct);
                });

                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(sseStream),
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var services = new ServiceCollection();
        services.AddPopTranslateCore();
        services.AddPopTranslateWin32();
        services.Replace(ServiceDescriptor.Singleton<ISettingsStore>(sp =>
            new JsonSettingsStore(sp.GetRequiredService<ISecretProtector>(), tempScope.SettingsPath)));
        services.Replace(ServiceDescriptor.Singleton<IProxyHttpClientFactory>(new StubProxyHttpClientFactory(handler)));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        var store = provider.GetRequiredService<ISettingsStore>();
        var protector = provider.GetRequiredService<ISecretProtector>();
        var polishOrchestrator = provider.GetRequiredService<IPolishOrchestrator>();

        const string persistedCustomPrompt = "Polish with crisp executive clarity and preserve every technical term.";
        var settings = AppSettings.CreateDefault();
        settings.Polish.CustomPrompt = persistedCustomPrompt;
        settings.Providers =
        [
            new ProviderInstanceConfig
            {
                Id = "llm-slow",
                DisplayName = "Primary LLM",
                Kind = ProviderKind.OpenAiCompatible,
                Enabled = true,
                SortOrder = 0,
                BaseUrl = "https://mock-llm-slow.local/v1",
                Model = "gpt-4o",
                EncryptedApiKey = protector.Protect("sk-polish-slow-key"),
            },
            new ProviderInstanceConfig
            {
                Id = "free-skip",
                DisplayName = "Free Traditional",
                Kind = ProviderKind.FreeTraditional,
                Enabled = true,
                SortOrder = 1,
            },
            new ProviderInstanceConfig
            {
                Id = "deepl-skip",
                DisplayName = "DeepL",
                Kind = ProviderKind.DeepL,
                Enabled = true,
                SortOrder = 2,
                EncryptedApiKey = protector.Protect("deepl-skip:fx"),
            },
            new ProviderInstanceConfig
            {
                Id = "llm-fast",
                DisplayName = "Secondary LLM",
                Kind = ProviderKind.OpenAiCompatible,
                Enabled = true,
                SortOrder = 3,
                BaseUrl = "https://mock-llm-fast.local/v1",
                Model = "deepseek-chat",
                EncryptedApiKey = protector.Protect("sk-polish-fast-key"),
            },
        ];
        await store.SaveAsync(settings);

        // 1. Eligible provider filtering: only enabled OpenAiCompatible providers in SortOrder
        var eligible = polishOrchestrator.GetEligibleProviders();
        Assert.Equal(["llm-slow", "llm-fast"], eligible.Select(p => p.Id));

        // 2. Concurrent SSE streaming (fast completes before slow is released)
        var updates = new List<ProviderStreamUpdate>();
        var fastCompletedWhileSlowStreaming = false;
        var slowFirstChunkSeen = false;

        await foreach (var update in polishOrchestrator.PolishParallelAsync(new PolishRequest(
            Text: "This draft is very good.",
            Style: PolishStyle.Academic,
            PreferredLanguage: LanguageCode.English)))
        {
            updates.Add(update);

            if (update.ProviderInstanceId == "llm-slow" &&
                update.State == ProviderStreamState.Streaming &&
                update.AccumulatedText == "Polished ")
            {
                slowFirstChunkSeen = true;
            }

            if (update.ProviderInstanceId == "llm-fast" &&
                update.State == ProviderStreamState.Completed &&
                !releaseSlowLlmSecondChunk.Task.IsCompleted)
            {
                fastCompletedWhileSlowStreaming = true;
                releaseSlowLlmSecondChunk.TrySetResult();
            }
        }

        Assert.False(forbiddenNonLlmCalled, "PolishOrchestrator must never call FreeTraditional or DeepL endpoints.");
        Assert.True(slowFirstChunkSeen);
        Assert.True(fastCompletedWhileSlowStreaming, "Fast LLM must complete concurrently while slow LLM is mid-stream.");
        Assert.DoesNotContain(updates, u => u.ProviderInstanceId is "free-skip" or "deepl-skip");

        var finalByProvider = updates.GroupBy(u => u.ProviderInstanceId).ToDictionary(g => g.Key, g => g.Last());
        Assert.Equal("Polished slow output.", finalByProvider["llm-slow"].AccumulatedText);
        Assert.Equal("Fast polished text.", finalByProvider["llm-fast"].AccumulatedText);

        Assert.Contains(observedSystemPrompts, x => x.Endpoint.Contains("mock-llm-slow", StringComparison.Ordinal) && x.Auth == "Bearer sk-polish-slow-key" && x.SystemPrompt.Contains("academic", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(observedSystemPrompts, x => x.Endpoint.Contains("mock-llm-fast", StringComparison.Ordinal) && x.Auth == "Bearer sk-polish-fast-key" && x.SystemPrompt.Contains("academic", StringComparison.OrdinalIgnoreCase));

        // 3. Custom prompt propagation from settings + single-provider failure isolation
        observedSystemPrompts.Clear();
        failSecondLlm = true;
        releaseSlowLlmSecondChunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        releaseSlowLlmSecondChunk.TrySetResult();

        var customResults = await polishOrchestrator.PolishAllAsync(new PolishRequest(
            Text: "Need better wording.",
            Style: PolishStyle.Custom,
            CustomPrompt: null));

        Assert.Equal(2, customResults.Count);
        Assert.Equal("llm-slow", customResults[0].ProviderInstanceId);
        Assert.True(customResults[0].IsSuccess, customResults[0].ErrorMessage);
        Assert.Equal("Polished slow output.", customResults[0].TranslatedText);

        Assert.Equal("llm-fast", customResults[1].ProviderInstanceId);
        Assert.False(customResults[1].IsSuccess);
        Assert.Contains("500", customResults[1].ErrorMessage, StringComparison.Ordinal);

        Assert.Equal(2, observedSystemPrompts.Count);
        Assert.All(observedSystemPrompts, x => Assert.Equal(persistedCustomPrompt, x.SystemPrompt));
    }

    [Fact]
    public void TextDiffService_EndToEndDiContainer_ReconstructsMixedCjkEnglishAndSurrogatePairsWithValidUtf16Offsets()
    {
        var services = new ServiceCollection();
        services.AddPopTranslateCore();
        services.AddPopTranslateWin32();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        var diffService = provider.GetRequiredService<ITextDiffService>();
        Assert.IsType<LocalTextDiffService>(diffService);

        const string original = "请使用 PopTranslate 工具来 fast translate 文档 🚀\r\nThis draft is very good ✨\n谢谢大家！";
        const string revised = "推荐使用 PopTranslate 工具来 quickly polish 文档 🎯\r\nThis draft is excellent ✨\n非常感谢！";

        var segments = diffService.ComputeDiff(original, revised);
        Assert.NotEmpty(segments);

        // Exact verbatim round-trip reconstruction invariants
        var reconstructedOriginal = string.Concat(
            segments.Where(s => s.Operation is DiffOperation.Equal or DiffOperation.Delete).Select(s => s.Text));
        var reconstructedRevised = string.Concat(
            segments.Where(s => s.Operation is DiffOperation.Equal or DiffOperation.Insert).Select(s => s.Text));

        Assert.Equal(original, reconstructedOriginal);
        Assert.Equal(revised, reconstructedRevised);

        // Surrogate-pair integrity & CRLF-normalized UTF-16 offset verification (matching RichTextBlock TextHighlighter contract)
        var renderedText = new StringBuilder();
        var charOffset = 0;
        var highlightedRanges = new List<(DiffOperation Op, int StartIndex, int Length, string Text)>();

        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            Assert.False(string.IsNullOrEmpty(seg.Text));
            Assert.False(char.IsLowSurrogate(seg.Text[0]), "Diff segment must not start with an orphaned low surrogate.");
            Assert.False(char.IsHighSurrogate(seg.Text[^1]), "Diff segment must not end with an orphaned high surrogate.");
            if (i > 0)
            {
                Assert.NotEqual(segments[i - 1].Operation, seg.Operation);
            }

            var normalized = seg.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            if (seg.Operation is DiffOperation.Delete or DiffOperation.Insert)
            {
                highlightedRanges.Add((seg.Operation, charOffset, normalized.Length, normalized));
            }

            renderedText.Append(normalized);
            charOffset += normalized.Length;
        }

        var flatRendered = renderedText.ToString();
        Assert.NotEmpty(highlightedRanges);
        foreach (var (_, startIndex, length, expectedSlice) in highlightedRanges)
        {
            Assert.Equal(expectedSlice, flatRendered.Substring(startIndex, length));
        }

        Assert.Contains(segments, s => s.Operation == DiffOperation.Delete && s.Text.Contains("🚀", StringComparison.Ordinal));
        Assert.Contains(segments, s => s.Operation == DiffOperation.Insert && s.Text.Contains("🎯", StringComparison.Ordinal));
        Assert.Contains(segments, s => s.Operation == DiffOperation.Equal && s.Text.Contains("✨", StringComparison.Ordinal));
    }

    [Fact]
    public void GlobalHotkeyService_EndToEndDiContainer_RegistersAllFourActionsIncludingSelectionPolishAndReportsConflicts()
    {
        // Use distinct F13-F16 keys (0x7C-0x7F) so this test never collides with parallel unit tests using F9-F12.
        const uint vkF13 = 0x7C;
        const uint vkF14 = 0x7D;
        const uint vkF15 = 0x7E;
        const uint vkF16 = 0x7F;
        const HotkeyModifiers safeModifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift;

        var services = new ServiceCollection();
        services.AddPopTranslateCore();
        services.AddPopTranslateWin32();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        var hotkeyService = provider.GetRequiredService<IGlobalHotkeyService>();
        Assert.IsType<GlobalHotkeyService>(hotkeyService);

        var config = new HotkeyConfig
        {
            SelectionTranslate = new HotkeyGesture(safeModifiers, vkF13),
            InputTranslate = new HotkeyGesture(safeModifiers, vkF14),
            ScreenshotTranslate = new HotkeyGesture(safeModifiers, vkF15),
            SelectionPolish = new HotkeyGesture(safeModifiers, vkF16),
        };

        var results = hotkeyService.ApplyHotkeys(config);

        Assert.Equal(4, results.Count);
        Assert.True(results[HotkeyAction.SelectionTranslate].Succeeded, results[HotkeyAction.SelectionTranslate].ErrorMessage);
        Assert.True(results[HotkeyAction.InputTranslate].Succeeded, results[HotkeyAction.InputTranslate].ErrorMessage);
        Assert.True(results[HotkeyAction.ScreenshotTranslate].Succeeded, results[HotkeyAction.ScreenshotTranslate].ErrorMessage);
        Assert.True(results[HotkeyAction.SelectionPolish].Succeeded, results[HotkeyAction.SelectionPolish].ErrorMessage);

        // Duplicate SelectionPolish gesture must report internal conflict against SelectionTranslate
        var conflictConfig = config.Clone();
        conflictConfig.SelectionPolish = config.SelectionTranslate;
        var conflictResults = hotkeyService.ApplyHotkeys(conflictConfig);

        Assert.Equal(4, conflictResults.Count);
        Assert.True(conflictResults[HotkeyAction.SelectionTranslate].Succeeded, conflictResults[HotkeyAction.SelectionTranslate].ErrorMessage);
        Assert.False(conflictResults[HotkeyAction.SelectionPolish].Succeeded);
        Assert.Contains("SelectionTranslate", conflictResults[HotkeyAction.SelectionPolish].ErrorMessage ?? string.Empty, StringComparison.Ordinal);

        hotkeyService.UnregisterAll();
    }

    [Fact]
    public async Task JsonSettingsStore_EndToEndDiContainer_LoadsV1JsonWithSafeV11DefaultsAndRoundTripsDpapiSecrets()
    {
        using var tempScope = new TempSettingsScope();
        const string legacyPlaintextKey = "sk-v1-migration-secret-55555";

        // Write a V1 settings.json payload intentionally lacking AutoBackTranslate, Polish, and Hotkeys.SelectionPolish.
        const string v1Json = """
            {
              "SchemaVersion": 1,
              "PrimaryTargetLanguage": "zh-Hans",
              "SecondaryTargetLanguage": "en",
              "MergePdfLineBreaks": true,
              "LaunchAtStartup": false,
              "Hotkeys": {
                "SelectionTranslate": { "Modifiers": "Alt", "VirtualKey": 68 },
                "InputTranslate": { "Modifiers": "Alt", "VirtualKey": 65 },
                "ScreenshotTranslate": { "Modifiers": "Alt", "VirtualKey": 83 }
              },
              "Providers": [
                {
                  "Id": "legacy-openai",
                  "DisplayName": "Legacy OpenAI",
                  "Kind": "OpenAiCompatible",
                  "Enabled": true,
                  "SortOrder": 0,
                  "BaseUrl": "https://api.openai.com/v1",
                  "Model": "gpt-4o-mini",
                  "EncryptedApiKey": "sk-v1-migration-secret-55555"
                }
              ]
            }
            """;
        await File.WriteAllTextAsync(tempScope.SettingsPath, v1Json, Encoding.UTF8);

        var services = new ServiceCollection();
        services.AddPopTranslateCore();
        services.AddPopTranslateWin32();
        services.Replace(ServiceDescriptor.Singleton<ISettingsStore>(sp =>
            new JsonSettingsStore(sp.GetRequiredService<ISecretProtector>(), tempScope.SettingsPath)));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        var store = provider.GetRequiredService<ISettingsStore>();
        var protector = provider.GetRequiredService<ISecretProtector>();

        var loaded = await store.LoadAsync();

        // Assert V1.1 backward-compatible defaults
        Assert.False(loaded.AutoBackTranslate);
        Assert.NotNull(loaded.Polish);
        Assert.Equal(PolishStyle.General, loaded.Polish.DefaultStyle);
        Assert.False(loaded.Polish.ShowDiffByDefault);
        Assert.Equal(PolishConfig.DefaultCustomPrompt, loaded.Polish.CustomPrompt);
        Assert.Equal(HotkeyConfig.DefaultSelectionPolish, loaded.Hotkeys.SelectionPolish);
        Assert.Equal("Alt + P", loaded.Hotkeys.SelectionPolish.DisplayText);

        // Assert V1 fields preserved and legacy plaintext secret sanitized into DPAPI
        var legacyProvider = Assert.Single(loaded.Providers, p => p.Id == "legacy-openai");
        Assert.StartsWith(DpapiSecretProtector.Prefix, legacyProvider.EncryptedApiKey, StringComparison.Ordinal);
        Assert.Equal(legacyPlaintextKey, protector.Unprotect(legacyProvider.EncryptedApiKey));

        // Update V1.1 settings, persist, verify zero plaintext secret on disk, and reload via fresh DI container
        loaded.AutoBackTranslate = true;
        loaded.Polish.DefaultStyle = PolishStyle.Concise;
        loaded.Polish.ShowDiffByDefault = true;
        loaded.Polish.CustomPrompt = "Keep every sentence tight and direct.";
        loaded.Hotkeys.SelectionPolish = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x50);
        await store.SaveAsync(loaded);

        var persistedJson = await File.ReadAllTextAsync(tempScope.SettingsPath, Encoding.UTF8);
        Assert.DoesNotContain(legacyPlaintextKey, persistedJson, StringComparison.Ordinal);
        Assert.Contains(DpapiSecretProtector.Prefix, persistedJson, StringComparison.Ordinal);

        var freshStore = new JsonSettingsStore(new DpapiSecretProtector(), tempScope.SettingsPath);
        var roundTripped = await freshStore.LoadAsync();

        Assert.True(roundTripped.AutoBackTranslate);
        Assert.Equal(PolishStyle.Concise, roundTripped.Polish.DefaultStyle);
        Assert.True(roundTripped.Polish.ShowDiffByDefault);
        Assert.Equal("Keep every sentence tight and direct.", roundTripped.Polish.CustomPrompt);
        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x50), roundTripped.Hotkeys.SelectionPolish);

        var roundTrippedProvider = Assert.Single(roundTripped.Providers, p => p.Id == "legacy-openai");
        Assert.StartsWith(DpapiSecretProtector.Prefix, roundTrippedProvider.EncryptedApiKey, StringComparison.Ordinal);
        Assert.Equal(legacyPlaintextKey, protector.Unprotect(roundTrippedProvider.EncryptedApiKey));
    }

    private sealed class TempSettingsScope : IDisposable
    {
        private readonly string _dir;

        public TempSettingsScope()
        {
            _dir = Path.Combine(Path.GetTempPath(), "PopTranslateIntegrationTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            SettingsPath = Path.Combine(_dir, "settings.json");
        }

        public string SettingsPath { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_dir))
                {
                    Directory.Delete(_dir, recursive: true);
                }
            }
            catch
            {
                // Ignore temp cleanup errors.
            }
        }
    }

    private sealed class StubProxyHttpClientFactory(HttpMessageHandler handler) : IProxyHttpClientFactory
    {
        public HttpClient CreateClient(ProxyConfig? overrideConfig = null, TimeSpan? timeout = null) =>
            new(handler, disposeHandler: false)
            {
                Timeout = timeout ?? TimeSpan.FromSeconds(15),
            };

        public void InvalidateProxyCache()
        {
        }
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            responder(request, cancellationToken);
    }

    /// <summary>
    /// Pipe-backed stream that allows deterministic mid-stream pacing for SSE concurrency and cancellation tests.
    /// </summary>
    private sealed class AsyncChunkedSseStream : Stream
    {
        private readonly System.Threading.Channels.Channel<byte[]> _chunks =
            System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        private byte[]? _current;
        private int _offset;

        public AsyncChunkedSseStream(Func<Func<string, CancellationToken, ValueTask>, Task> producer)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await producer(WriteChunkAsync).ConfigureAwait(false);
                    _chunks.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    _chunks.Writer.TryComplete(ex);
                }
            });
        }

        private ValueTask WriteChunkAsync(string text, CancellationToken ct) =>
            _chunks.Writer.WriteAsync(Encoding.UTF8.GetBytes(text), ct);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_current is null || _offset >= _current.Length)
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return 0;
                }

                if (_chunks.Reader.TryRead(out var next))
                {
                    _current = next;
                    _offset = 0;
                }
            }

            var toCopy = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, toCopy).CopyTo(buffer);
            _offset += toCopy;
            return toCopy;
        }
    }

    /// <summary>
    /// Renders crisp Windows Segoe UI text onto a top-down BGRA32 DIBSection and encodes it to PNG via ScreenCaptureGdi.
    /// </summary>
    private static class RenderedTextImageHelper
    {
        public const int DefaultWidth = 640;
        public const int DefaultHeight = 200;

        public static async Task<byte[]> RenderLinesToPngAsync(string line1, string line2)
        {
            var pixels = RenderBgra32(line1, line2, DefaultWidth, DefaultHeight);
            return await ScreenCaptureGdi.EncodeBgra32ToPngAsync(pixels, DefaultWidth, DefaultHeight);
        }

        private static byte[] RenderBgra32(string line1, string line2, int width, int height)
        {
            var screenDc = GetDC(nint.Zero);
            var memDc = CreateCompatibleDC(screenDc);
            nint hBitmap = nint.Zero;
            nint oldBitmap = nint.Zero;
            nint hFont = nint.Zero;
            nint oldFont = nint.Zero;
            nint whiteBrush = nint.Zero;

            try
            {
                var bmi = new BitmapInfoHeader
                {
                    biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    biWidth = width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,
                };

                hBitmap = CreateDIBSection(memDc, ref bmi, 0, out var bitsPtr, nint.Zero, 0);
                if (hBitmap == nint.Zero || bitsPtr == nint.Zero)
                {
                    throw new InvalidOperationException("Failed to allocate GDI DIBSection for OCR test image.");
                }

                oldBitmap = SelectObject(memDc, hBitmap);
                whiteBrush = CreateSolidBrush(0x00FFFFFFu);
                var fillRect = new Rect { Left = 0, Top = 0, Right = width, Bottom = height };
                FillRect(memDc, ref fillRect, whiteBrush);

                hFont = CreateFontW(
                    -42, 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI");
                oldFont = SelectObject(memDc, hFont);
                SetBkMode(memDc, 1); // TRANSPARENT
                SetTextColor(memDc, 0x00000000u); // Black

                var r1 = new Rect { Left = 28, Top = 28, Right = width - 28, Bottom = 100 };
                var r2 = new Rect { Left = 28, Top = 108, Right = width - 28, Bottom = 180 };
                DrawTextW(memDc, line1, line1.Length, ref r1, 0x00000020u); // DT_SINGLELINE
                DrawTextW(memDc, line2, line2.Length, ref r2, 0x00000020u);

                var bytes = new byte[width * height * 4];
                Marshal.Copy(bitsPtr, bytes, 0, bytes.Length);
                for (var i = 3; i < bytes.Length; i += 4)
                {
                    bytes[i] = 0xFF;
                }

                return bytes;
            }
            finally
            {
                if (oldFont != nint.Zero) SelectObject(memDc, oldFont);
                if (hFont != nint.Zero) DeleteObject(hFont);
                if (whiteBrush != nint.Zero) DeleteObject(whiteBrush);
                if (oldBitmap != nint.Zero) SelectObject(memDc, oldBitmap);
                if (hBitmap != nint.Zero) DeleteObject(hBitmap);
                if (memDc != nint.Zero) DeleteDC(memDc);
                if (screenDc != nint.Zero) ReleaseDC(nint.Zero, screenDc);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern nint GetDC(nint hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(nint hWnd, nint hDC);

        [DllImport("gdi32.dll")]
        private static extern nint CreateCompatibleDC(nint hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(nint hdc);

        [DllImport("gdi32.dll")]
        private static extern nint CreateDIBSection(
            nint hdc,
            ref BitmapInfoHeader pbmi,
            uint usage,
            out nint ppvBits,
            nint hSection,
            uint offset);

        [DllImport("gdi32.dll")]
        private static extern nint SelectObject(nint hdc, nint h);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(nint ho);

        [DllImport("gdi32.dll")]
        private static extern nint CreateSolidBrush(uint color);

        [DllImport("user32.dll")]
        private static extern int FillRect(nint hDC, ref Rect lprc, nint hbr);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern nint CreateFontW(
            int cHeight,
            int cWidth,
            int cEscapement,
            int cOrientation,
            int cWeight,
            uint bItalic,
            uint bUnderline,
            uint bStrikeOut,
            uint iCharSet,
            uint iOutPrecision,
            uint iClipPrecision,
            uint iQuality,
            uint iPitchAndFamily,
            string pszFaceName);

        [DllImport("gdi32.dll")]
        private static extern int SetBkMode(nint hdc, int mode);

        [DllImport("gdi32.dll")]
        private static extern uint SetTextColor(nint hdc, uint color);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int DrawTextW(nint hdc, string lpchText, int cchText, ref Rect lprc, uint format);
    }
}
