using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
using PopTranslate.Win32.Ocr;
using PopTranslate.Win32.Screenshot;
using PopTranslate.Win32.Selection;
using PopTranslate.Win32.Tts;
using Xunit;

namespace PopTranslate.Tests.Integration;

public sealed class MilestoneUnifiedUpgradeTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;
    private readonly DpapiSecretProtector _protector = new();

    public MilestoneUnifiedUpgradeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PopTranslateMilestoneUnified_" + Guid.NewGuid().ToString("N"));
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
            // Ignore temp cleanup errors.
        }
    }

    [Fact]
    public async Task P0_1_LanguageDetectionAndAutoSourcePassThrough_DetectsKanjiJapaneseAndUrlHeavyChineseWhilePreservingAuto()
    {
        var detector = new LocalLanguageDetector();

        // Kanji-only Japanese (Shinjitai / Kokuji / Kanji compounds / Hiragana+CJK)
        Assert.Equal(LanguageCode.Japanese, detector.Detect("割引").Language);
        Assert.Equal(LanguageCode.Japanese, detector.Detect("株式会社").Language);
        Assert.Equal(LanguageCode.Japanese, detector.Detect("OpenAI APIの利用規約").Language);

        // Chinese prose containing long Latin URLs / file paths must still detect as ChineseSimplified
        var urlHeavyChinese = detector.Detect("报错：https://api.openai.com/v1/chat/completions 连接超时，请检查 C:\\Users\\Admin\\config.json");
        Assert.Equal(LanguageCode.ChineseSimplified, urlHeavyChinese.Language);
        Assert.True(urlHeavyChinese.IsReliable);

        // TranslationOrchestrator must pass SourceLanguage = Auto through to providers (no local override)
        string? capturedDeepLBody = null;
        Uri? capturedGoogleUri = null;

        var handler = new DelegateHttpHandler(async (req, ct) =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.StartsWith("https://api-free.deepl.com/v2/translate", StringComparison.Ordinal))
            {
                capturedDeepLBody = await req.Content!.ReadAsStringAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"translations":[{"detected_source_language":"JA","text":"折扣"}]}""",
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            if (url.Contains("google.com/translate_a/", StringComparison.Ordinal) ||
                url.Contains("googleapis.com/translate_a/", StringComparison.Ordinal))
            {
                capturedGoogleUri = req.RequestUri;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""[["折扣","ja"]]""", Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var store = new JsonSettingsStore(_protector, _settingsPath);
        var settings = AppSettings.CreateDefault();
        settings.PrimaryTargetLanguage = LanguageCode.ChineseSimplified;
        settings.SecondaryTargetLanguage = LanguageCode.English;
        settings.Providers =
        [
            new ProviderInstanceConfig
            {
                Id = "deepl-auto",
                DisplayName = "DeepL",
                Kind = ProviderKind.DeepL,
                Enabled = true,
                SortOrder = 0,
                EncryptedApiKey = _protector.Protect("deepl-test-key:fx"),
            },
            new ProviderInstanceConfig
            {
                Id = "google-auto",
                DisplayName = "Google Free",
                Kind = ProviderKind.FreeTraditional,
                FreeEngine = FreeTranslatorEngine.Google,
                Enabled = true,
                SortOrder = 1,
            },
        ];
        await store.SaveAsync(settings);

        var factory = new StubProxyHttpClientFactory(handler);
        var providers = new ITranslationProvider[]
        {
            new DeepLTranslationProvider(factory, _protector),
            new FreeTraditionalTranslationProvider(factory),
        };
        var orchestrator = new TranslationOrchestrator(
            store,
            new TextPreprocessor(),
            new DualTargetLanguageRouter(detector),
            providers);

        var batch = await orchestrator.TranslateAllAsync(new TranslationRequest(
            Text: "割引",
            SourceLanguage: LanguageCode.Auto,
            TargetLanguage: LanguageCode.Auto));

        Assert.Equal(LanguageCode.ChineseSimplified, batch.EffectiveTargetLanguage);
        Assert.All(batch.Results, r => Assert.True(r.IsSuccess, r.ErrorMessage));

        // DeepL omits source_lang when SourceLanguage.IsAuto is preserved; Google sends sl=auto.
        Assert.NotNull(capturedDeepLBody);
        using var deepLDoc = JsonDocument.Parse(capturedDeepLBody!);
        Assert.False(deepLDoc.RootElement.TryGetProperty("source_lang", out _));
        Assert.NotNull(capturedGoogleUri);
        Assert.Contains("sl=auto", capturedGoogleUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task P0_9_P2_1_P2_3_OpenAiTranslation_ReasoningThinkStrip_SourceTextGuard_GlossaryAndDictionaryPrompt()
    {
        var capturedBodies = new List<string>();
        var handler = new DelegateHttpHandler(async (req, ct) =>
        {
            var body = await req.Content!.ReadAsStringAsync(ct);
            lock (capturedBodies)
            {
                capturedBodies.Add(body);
            }

            var sse =
                "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"Analyzing context...\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"<think>internal CoT\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\" step</think>最终译文\"}}]}\n\n" +
                "data: [DONE]\n\n";

            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8),
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return resp;
        });

        var store = new JsonSettingsStore(_protector, _settingsPath);
        var settings = AppSettings.CreateDefault();
        settings.EnableSingleWordDictionary = true;
        settings.Glossary =
        [
            new GlossaryEntry { SourceTerm = "PopTranslate", TargetTerm = "泡泡翻译" },
            new GlossaryEntry { SourceTerm = "UnmatchedTerm", TargetTerm = "不应注入" },
        ];
        settings.Providers =
        [
            new ProviderInstanceConfig
            {
                Id = "r1-llm",
                DisplayName = "Reasoning LLM",
                Kind = ProviderKind.OpenAiCompatible,
                Enabled = true,
                SortOrder = 0,
                BaseUrl = "https://mock-llm.local/v1",
                Model = "o3-mini",
                EncryptedApiKey = _protector.Protect("sk-r1"),
                SystemPrompt = ProviderInstanceConfig.DefaultOpenAiSystemPrompt,
            },
        ];
        await store.SaveAsync(settings);

        var factory = new StubProxyHttpClientFactory(handler);
        var openAiProvider = new OpenAiCompatibleTranslationProvider(factory, _protector, store);
        var detector = new LocalLanguageDetector();
        var orchestrator = new TranslationOrchestrator(
            store,
            new TextPreprocessor(),
            new DualTargetLanguageRouter(detector),
            [openAiProvider]);

        // 1. Sentence with matched Glossary + Reasoning stream + <think> stripping + o3-mini temperature omission
        var updates = new List<ProviderStreamUpdate>();
        await foreach (var update in orchestrator.TranslateParallelAsync(new TranslationRequest(
            Text: "PopTranslate supports streaming.",
            SourceLanguage: LanguageCode.English,
            TargetLanguage: LanguageCode.ChineseSimplified)))
        {
            updates.Add(update);
        }

        Assert.Contains(updates, u => u.State == ProviderStreamState.Streaming && u.IsReasoning && string.IsNullOrEmpty(u.AccumulatedText));
        var completed = Assert.Single(updates, u => u.State == ProviderStreamState.Completed);
        Assert.Equal("最终译文", completed.AccumulatedText);
        Assert.DoesNotContain("<think>", completed.AccumulatedText, StringComparison.Ordinal);

        using (var sentenceDoc = JsonDocument.Parse(capturedBodies[0]))
        {
            Assert.False(sentenceDoc.RootElement.TryGetProperty("temperature", out _), "o3-mini must omit temperature.");
            var sys = sentenceDoc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString() ?? string.Empty;
            var usr = sentenceDoc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString() ?? string.Empty;
            Assert.Contains("<source_text>", sys, StringComparison.Ordinal);
            Assert.Contains("PopTranslate => 泡泡翻译", sys, StringComparison.Ordinal);
            Assert.DoesNotContain("UnmatchedTerm", sys, StringComparison.Ordinal);
            Assert.Equal("<source_text>\nPopTranslate supports streaming.\n</source_text>", usr);
        }

        // 2. Single-word dictionary lookup prompt activation
        var wordResult = await openAiProvider.TranslateAsync(
            new TranslationRequest("serendipity", LanguageCode.English, LanguageCode.ChineseSimplified),
            settings.Providers[0]);
        Assert.True(wordResult.IsSuccess, wordResult.ErrorMessage);
        Assert.Equal("最终译文", wordResult.TranslatedText);

        using (var dictDoc = JsonDocument.Parse(capturedBodies[1]))
        {
            var dictSys = dictDoc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString() ?? string.Empty;
            Assert.Contains("Phonetics", dictSys, StringComparison.Ordinal);
            Assert.Contains("POS & Meanings", dictSys, StringComparison.Ordinal);
            Assert.Contains("Inflections", dictSys, StringComparison.Ordinal);
            Assert.Contains("Examples", dictSys, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void P0_8_P2_2_TextPreprocessor_PreservesCodeFencesAndCompoundHyphensAndSplitsCodeIdentifiers()
    {
        var preprocessor = new TextPreprocessor();

        // Preserves fenced Markdown code blocks verbatim while merging prose & keeping state-of-the-art compound hyphen
        const string markdownWithFence =
            "This is a state-of-the-\nart pipeline:\n```csharp\nvar   x = 1;\n  return x;\n```\nauto-\nmatic wrap.";
        var processed = preprocessor.Preprocess(markdownWithFence, mergeLineBreaks: true);

        Assert.Contains("state-of-the-art pipeline:", processed, StringComparison.Ordinal);
        Assert.Contains("```csharp\nvar   x = 1;\n  return x;\n```", processed, StringComparison.Ordinal);
        Assert.Contains("automatic wrap.", processed, StringComparison.Ordinal);

        // Developer code identifier splitting (camelCase, PascalCase, acronym runs, snake_case, SCREAMING_SNAKE_CASE)
        Assert.Equal("Try Acquire Single Instance Mutex", preprocessor.Preprocess("TryAcquireSingleInstanceMutex", mergeLineBreaks: true));
        Assert.Equal("proxy http client", preprocessor.Preprocess("proxy_http_client", mergeLineBreaks: false));
        Assert.Equal("WM CLIPBOARDUPDATE", preprocessor.Preprocess("WM_CLIPBOARDUPDATE", mergeLineBreaks: false));
        Assert.Equal("XML Http Request", preprocessor.Preprocess("XMLHttpRequest", mergeLineBreaks: false));

        // Brands, compounds, and model names must NOT split
        Assert.Equal("iPhone", preprocessor.Preprocess("iPhone", mergeLineBreaks: false));
        Assert.Equal("state-of-the-art", preprocessor.Preprocess("state-of-the-art", mergeLineBreaks: false));
        Assert.Equal("gpt-4o-mini", preprocessor.Preprocess("gpt-4o-mini", mergeLineBreaks: false));

        // Markdown tables and $$...$$ display math blocks preserve line breaks even when mergeLineBreaks=true
        const string tableAndMath = "| Col A | Col B |\n| --- | --- |\n| 1 | 2 |\n\n$$\nx^2 + y^2 = z^2\n$$";
        Assert.Equal(tableAndMath, preprocessor.Preprocess(tableAndMath, mergeLineBreaks: true));
    }

    [Fact]
    public async Task P0_10_ProxyHostPortParsing_AndJsonSettingsStoreNullArrayElementSanitization()
    {
        var store = new JsonSettingsStore(_protector, _settingsPath);
        using var proxyFactory = new ProxyHttpClientFactory(store, _protector);

        // Host containing ":port" or "http://host:port" parses cleanly without UriFormatException
        using var inlinePortHandler = proxyFactory.CreateHandler(new ProxyConfig
        {
            Mode = ProxyMode.Http,
            Host = "127.0.0.1:7890",
            Port = 1080,
        });
        var webProxy1 = Assert.IsType<WebProxy>(inlinePortHandler.Proxy);
        Assert.Equal(new Uri("http://127.0.0.1:7890/"), webProxy1.Address);

        using var schemeHostPortHandler = proxyFactory.CreateHandler(new ProxyConfig
        {
            Mode = ProxyMode.Socks5,
            Host = "http://127.0.0.1:7891",
            Port = 1080,
        });
        var webProxy2 = Assert.IsType<WebProxy>(schemeHostPortHandler.Proxy);
        Assert.Equal(new Uri("socks5://127.0.0.1:7891/"), webProxy2.Address);

        // JsonSettingsStore recovers cleanly from null elements inside Providers, ProcessBlacklist, and Glossary
        const string dirtyJson = """
        {
          "SchemaVersion": 1,
          "Selection": {
            "ProcessBlacklist": [ null, "  ", "Code.exe" ]
          },
          "Providers": [
            null,
            {
              "Id": "valid-free",
              "DisplayName": "Free",
              "Kind": "FreeTraditional",
              "Enabled": true
            }
          ],
          "Glossary": [
            null,
            { "SourceTerm": "  ", "TargetTerm": "empty" },
            { "SourceTerm": "WinUI", "TargetTerm": "原生界面" }
          ]
        }
        """;
        await File.WriteAllTextAsync(_settingsPath, dirtyJson, Encoding.UTF8);

        var loaded = await store.LoadAsync();
        Assert.Equal(["Code.exe"], loaded.Selection.ProcessBlacklist);
        var singleProvider = Assert.Single(loaded.Providers);
        Assert.Equal("valid-free", singleProvider.Id);
        var singleGlossary = Assert.Single(loaded.Glossary);
        Assert.Equal("WinUI", singleGlossary.SourceTerm);
        Assert.Equal("原生界面", singleGlossary.TargetTerm);
    }

    [Fact]
    public void P2_5_LocalTextDiffService_PreservesContractionsAndCleansChaffIslands()
    {
        var diffService = new LocalTextDiffService();

        // Contractions tokenize as whole words ("don't", "can't") rather than splitting mid-apostrophe
        var tokens = LocalTextDiffService.Tokenize("I don't think we can’t ship.");
        Assert.Contains("don't", tokens);
        Assert.Contains("can’t", tokens);

        var contractionDiff = diffService.ComputeDiff("I don't agree.", "I do agree.");
        Assert.Contains(contractionDiff, s => s.Operation == DiffOperation.Delete && s.Text == "don't");
        Assert.Contains(contractionDiff, s => s.Operation == DiffOperation.Insert && s.Text == "do");

        // Post-Myers chaff cleanup merges isolated 1-char CJK / space islands between Delete+Insert pairs
        const string origCjk = "今天天气真的非常好呀";
        const string revCjk = "今天气候实在极其棒呀";
        // Without chaff cleanup, '真'/'实' + '的'(chaff?) or similar 1-char CJK islands fragment replacements.
        // Verify explicit 1-char CJK chaff island ("甲乙丙" -> "丁乙戊") merges into one clean replacement!
        var cjkChaffDiff = diffService.ComputeDiff("前缀甲乙丙后缀", "前缀丁乙戊后缀");
        Assert.Equal(
            [
                new DiffSegment(DiffOperation.Equal, "前缀"),
                new DiffSegment(DiffOperation.Delete, "甲乙丙"),
                new DiffSegment(DiffOperation.Insert, "丁乙戊"),
                new DiffSegment(DiffOperation.Equal, "后缀"),
            ],
            cjkChaffDiff);
        Assert.Equal(origCjk, string.Concat(diffService.ComputeDiff(origCjk, revCjk).Where(s => s.Operation != DiffOperation.Insert).Select(s => s.Text)));
    }

    [Fact]
    public async Task FullDiContainer_ResolvesWriteBackTtsAndModelDiscoveryServicesCleanly()
    {
        var handler = new DelegateHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":[{"id":"gpt-4o-mini"},{"id":"deepseek-reasoner"}]}""",
                    Encoding.UTF8,
                    "application/json"),
            }));

        var services = new ServiceCollection();
        services.AddPopTranslateCore();
        services.AddPopTranslateWin32();
        services.Replace(ServiceDescriptor.Singleton<IProxyHttpClientFactory>(new StubProxyHttpClientFactory(handler)));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        var writeBack = provider.GetRequiredService<ITextWriteBackService>();
        var tts = provider.GetRequiredService<ITtsService>();
        var modelDiscovery = provider.GetRequiredService<IModelDiscoveryService>();

        Assert.IsType<Win32TextWriteBackService>(writeBack);
        Assert.IsType<WindowsAndEdgeTtsService>(tts);
        Assert.IsType<OpenAiModelDiscoveryService>(modelDiscovery);
        Assert.False(tts.IsSpeaking);

        var models = await modelDiscovery.FetchModelsAsync("https://api.example.com/v1", "sk-test");
        Assert.Equal(["deepseek-reasoner", "gpt-4o-mini"], models);
    }

    [Fact]
    public async Task OpenAiStreamHelper_MultiLineDataSse_PrematureEof_AndSlidingIdleTimeout()
    {
        var config = new ProviderInstanceConfig
        {
            Id = "sse-edge",
            DisplayName = "SSE Edge LLM",
            Kind = ProviderKind.OpenAiCompatible,
            Enabled = true,
            BaseUrl = "https://mock-sse.local/v1",
            Model = "gpt-4o-mini",
            EncryptedApiKey = _protector.Protect("sk-sse"),
            TimeoutSeconds = 30,
        };

        // 1. Multi-line SSE `data:` frame split across multiple lines + blank-line dispatch
        var multiLineHandler = new DelegateHttpHandler((_, _) =>
        {
            const string multiLineSse =
                ": keepalive comment\n" +
                "data: {\"choices\":[{\"delta\":\n" +
                "data: {\"content\":\"Hello \"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":\n" +
                "data: {\"content\":\"Multi-line!\"}}]}\n\n" +
                "data: [DONE]\n\n";

            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(multiLineSse, Encoding.UTF8),
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(resp);
        });

        var multiLineProvider = new OpenAiCompatibleTranslationProvider(
            new StubProxyHttpClientFactory(multiLineHandler),
            _protector);
        var multiLineResult = await multiLineProvider.TranslateAsync(
            new TranslationRequest("Hi", LanguageCode.English, LanguageCode.ChineseSimplified),
            config);

        Assert.True(multiLineResult.IsSuccess, multiLineResult.ErrorMessage);
        Assert.Equal("Hello Multi-line!", multiLineResult.TranslatedText);

        // 2. Premature EOF (HTTP 200 SSE stream closes before any content, finish_reason, or [DONE])
        var prematureEofHandler = new DelegateHttpHandler((_, _) =>
        {
            const string truncatedSse = ": connected\n\n";
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(truncatedSse, Encoding.UTF8),
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(resp);
        });

        var prematureProvider = new OpenAiCompatibleTranslationProvider(
            new StubProxyHttpClientFactory(prematureEofHandler),
            _protector);
        var prematureResult = await prematureProvider.TranslateAsync(
            new TranslationRequest("Hi", LanguageCode.English, LanguageCode.ChineseSimplified),
            config);

        Assert.False(prematureResult.IsSuccess);
        Assert.Contains("prematurely", prematureResult.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        // 3a. Sliding idle-timeout stays alive when chunks arrive within each idle window, even if total > idleTimeout
        var slidingAliveHandler = new DelegateHttpHandler((_, ct) =>
        {
            var stream = new DelayedChunkStream(async writeAsync =>
            {
                for (var i = 1; i <= 4; i++)
                {
                    await Task.Delay(55, ct);
                    await writeAsync($"data: {{\"choices\":[{{\"delta\":{{\"content\":\"part{i} \"}}}}]}}\n\n", ct);
                }

                await Task.Delay(40, ct);
                await writeAsync("data: [DONE]\n\n", ct);
            });

            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(resp);
        });

        var aliveDeltaBuilder = new StringBuilder();
        string? aliveError = null;
        await foreach (var item in OpenAiStreamHelper.ExecuteStreamingChatAsync(
            new StubProxyHttpClientFactory(slidingAliveHandler),
            _protector,
            config,
            new Uri("https://mock-sse.local/v1/chat/completions"),
            "Translate",
            "Hi",
            LanguageCode.English,
            normalIdleTimeoutOverride: TimeSpan.FromMilliseconds(150),
            reasoningIdleTimeoutOverride: TimeSpan.FromMilliseconds(350)))
        {
            if (!string.IsNullOrEmpty(item.Chunk.DeltaText))
            {
                aliveDeltaBuilder.Append(item.Chunk.DeltaText);
            }

            if (!string.IsNullOrEmpty(item.Chunk.ErrorMessage))
            {
                aliveError = item.Chunk.ErrorMessage;
            }
        }

        Assert.Null(aliveError);
        Assert.Equal("part1 part2 part3 part4 ", aliveDeltaBuilder.ToString());

        // 3b. Sliding idle-timeout aborts cleanly when stream stalls beyond the idle window
        var stalledHandler = new DelegateHttpHandler((_, ct) =>
        {
            var stream = new DelayedChunkStream(async writeAsync =>
            {
                await writeAsync("data: {\"choices\":[{\"delta\":{\"content\":\"start\"}}]}\n\n", ct);
                await Task.Delay(500, ct);
            });

            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(resp);
        });

        string? stallError = null;
        await foreach (var item in OpenAiStreamHelper.ExecuteStreamingChatAsync(
            new StubProxyHttpClientFactory(stalledHandler),
            _protector,
            config,
            new Uri("https://mock-sse.local/v1/chat/completions"),
            "Translate",
            "Hi",
            LanguageCode.English,
            normalIdleTimeoutOverride: TimeSpan.FromMilliseconds(120),
            reasoningIdleTimeoutOverride: TimeSpan.FromMilliseconds(300)))
        {
            if (!string.IsNullOrEmpty(item.Chunk.ErrorMessage))
            {
                stallError = item.Chunk.ErrorMessage;
            }
        }

        Assert.NotNull(stallError);
        Assert.Contains("timed out", stallError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VisionLlmOcrProvider_AndOrchestrator_TableAndLatexModes_EmptySentinel_AndMultiBlockFences()
    {
        var capturedPrompts = new List<string>();
        var nextResponseContent = string.Empty;

        var handler = new DelegateHttpHandler(async (req, ct) =>
        {
            var body = await req.Content!.ReadAsStringAsync(ct);
            using (var doc = JsonDocument.Parse(body))
            {
                var promptText = doc.RootElement
                    .GetProperty("messages")[0]
                    .GetProperty("content")[0]
                    .GetProperty("text")
                    .GetString() ?? string.Empty;
                capturedPrompts.Add(promptText);
            }

            var json = JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new { message = new { content = nextResponseContent } },
                },
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

        var store = new JsonSettingsStore(_protector, _settingsPath);
        var visionProvider = new VisionLlmOcrProvider(new StubProxyHttpClientFactory(handler), _protector);
        var orchestrator = new OcrOrchestrator(store, new TextPreprocessor(), [visionProvider]);
        var tinyPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");
        var ocrImage = new OcrImage(tinyPng, 1, 1, 1.0, MimeType: "image/png");

        // 1. MarkdownTable mode + AutoMergeOcrLineBreaks=true must preserve multi-line table rows!
        var settings = AppSettings.CreateDefault();
        settings.Ocr.ActiveProvider = OcrProviderKind.VisionLlm;
        settings.Ocr.PromptMode = OcrPromptMode.MarkdownTable;
        settings.Ocr.AutoMergeOcrLineBreaks = true;
        settings.Ocr.VisionBaseUrl = "https://mock-vision.local/v1";
        settings.Ocr.VisionModel = "gpt-4o-mini";
        settings.Ocr.EncryptedVisionApiKey = _protector.Protect("sk-vision");
        await store.SaveAsync(settings);

        nextResponseContent = "```markdown\n| Metric | Value |\n| --- | --- |\n| Latency | 12ms |\n```";
        var tableResult = await orchestrator.RecognizeAsync(ocrImage);
        Assert.True(tableResult.IsSuccess, tableResult.ErrorMessage);
        Assert.Contains("GitHub-Flavored Markdown tables", capturedPrompts[0], StringComparison.Ordinal);
        Assert.Equal("| Metric | Value |\n| --- | --- |\n| Latency | 12ms |", tableResult.Text);

        // 2. LatexFormula mode + AutoMergeOcrLineBreaks=true must preserve multi-line $$...$$ display math!
        settings.Ocr.PromptMode = OcrPromptMode.LatexFormula;
        await store.SaveAsync(settings);

        nextResponseContent = "<think>detecting integral</think>\n$$\n\\int_0^\\infty e^{-x^2} dx = \\frac{\\sqrt{\\pi}}{2}\n$$";
        var latexResult = await orchestrator.RecognizeAsync(ocrImage);
        Assert.True(latexResult.IsSuccess, latexResult.ErrorMessage);
        Assert.Contains("LaTeX", capturedPrompts[1], StringComparison.Ordinal);
        Assert.Equal("$$\n\\int_0^\\infty e^{-x^2} dx = \\frac{\\sqrt{\\pi}}{2}\n$$", latexResult.Text);

        // 3. <EMPTY> and [EMPTY] sentinels normalize to empty text cleanly
        nextResponseContent = "```text\n<EMPTY>\n```";
        var emptyResult = await visionProvider.RecognizeAsync(ocrImage, settings.Ocr);
        Assert.True(emptyResult.IsSuccess);
        Assert.Equal(string.Empty, emptyResult.Text);
        Assert.Null(emptyResult.DetectedLanguage);

        // 4. Multi-block markdown fences must NOT be stripped/corrupted by StripOuterMarkdownFence
        const string multiBlock = "```python\nprint(1)\n```\n\nSome note\n\n```bash\necho 2\n```";
        Assert.Equal(multiBlock, VisionLlmOcrProvider.StripOuterMarkdownFence(multiBlock));

        const string singleWrapped = "```json\n{\"ok\":true}\n```";
        Assert.Equal("{\"ok\":true}", VisionLlmOcrProvider.StripOuterMarkdownFence(singleWrapped));
    }

    [Fact]
    public async Task PolishPromptTemplates_CodeNamingAndCustomLanguageGuard_AndDeepLUsageEndpointHealthCheck()
    {
        // 1. CodeNaming prompt includes all 4 conventions + note language guard, and omits Glossary injection
        var codeNamingZh = PolishPromptTemplates.ResolveSystemPrompt(
            PolishStyle.CodeNaming,
            customPrompt: null,
            preferredLanguage: LanguageCode.ChineseSimplified);
        Assert.Contains("camelCase", codeNamingZh, StringComparison.Ordinal);
        Assert.Contains("PascalCase", codeNamingZh, StringComparison.Ordinal);
        Assert.Contains("snake_case", codeNamingZh, StringComparison.Ordinal);
        Assert.Contains("SCREAMING_SNAKE_CASE", codeNamingZh, StringComparison.Ordinal);
        Assert.Contains(LanguageCode.ChineseSimplified.DisplayName, codeNamingZh, StringComparison.Ordinal);

        // 2. Custom prompt enforces strict output-language guard (both explicit language and fallback original-language guard)
        var customWithLang = PolishPromptTemplates.ResolveSystemPrompt(
            PolishStyle.Custom,
            customPrompt: "Rewrite with bullet points.",
            preferredLanguage: LanguageCode.English);
        Assert.Contains("Rewrite with bullet points.", customWithLang, StringComparison.Ordinal);
        Assert.Contains("Keep the output strictly in English", customWithLang, StringComparison.Ordinal);

        var customAutoLang = PolishPromptTemplates.ResolveSystemPrompt(
            PolishStyle.Custom,
            customPrompt: "Make tone warmer.",
            preferredLanguage: LanguageCode.Auto);
        Assert.Contains("Make tone warmer.", customAutoLang, StringComparison.Ordinal);
        Assert.Contains("original language", customAutoLang, StringComparison.OrdinalIgnoreCase);

        // 3. DeepL TestConnectionAsync hits GET /v2/usage (zero translation character burn) and formats quota
        HttpMethod? observedMethod = null;
        Uri? observedUri = null;
        string? observedAuth = null;

        var deepLHandler = new DelegateHttpHandler((req, _) =>
        {
            observedMethod = req.Method;
            observedUri = req.RequestUri;
            observedAuth = req.Headers.Authorization?.ToString();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"character_count":12500,"character_limit":500000}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        });

        var deepLProvider = new DeepLTranslationProvider(new StubProxyHttpClientFactory(deepLHandler), _protector);
        var freeConfig = new ProviderInstanceConfig
        {
            Kind = ProviderKind.DeepL,
            DeepLTier = DeepLEndpointTier.Auto,
            EncryptedApiKey = _protector.Protect("test-deepl-key:fx"),
        };

        var health = await deepLProvider.TestConnectionAsync(freeConfig);
        Assert.True(health.IsSuccess, health.Message);
        Assert.Equal(HttpMethod.Get, observedMethod);
        Assert.Equal("https://api-free.deepl.com/v2/usage", observedUri?.AbsoluteUri);
        Assert.Equal("DeepL-Auth-Key test-deepl-key:fx", observedAuth);
        Assert.Contains("12,500", health.Message, StringComparison.Ordinal);
        Assert.Contains("500,000", health.Message, StringComparison.Ordinal);

        // Custom gateway URL ending in /v2/translate maps cleanly to /v2/usage
        Assert.Equal(
            "https://deepl-proxy.example.com/v2/usage",
            DeepLTranslationProvider.ResolveUsageEndpointUrl(
                new ProviderInstanceConfig { BaseUrl = "https://deepl-proxy.example.com/v2/translate" },
                "pro-key"));
    }

    [Fact]
    public async Task WindowsOcrProvider_MultiSpaceAndCjkQuotesCleanup_AndRawBgraZeroCopyPath()
    {
        // Multi-space CJK & CJK quotation marks cleanup without harming English words
        Assert.Equal("你好", WindowsOcrProvider.CleanOcrLineText("你   好"));
        Assert.Equal("“你好”", WindowsOcrProvider.CleanOcrLineText("“ 你 好 ”"));
        Assert.Equal("（测试·文本）", WindowsOcrProvider.CleanOcrLineText("（ 测 试 · 文 本 ）"));
        Assert.Equal("Hello   World", WindowsOcrProvider.CleanOcrLineText("Hello   World"));

        // Zero-copy RawBgraBytes path: supply empty ImageBytes + valid RawBgraBytes (proving PNG decoder is bypassed)
        const int width = 96;
        const int height = 36;
        var rawBgra = new byte[width * height * 4];
        Array.Fill(rawBgra, (byte)0xFF);

        var zeroCopyImage = new OcrImage(
            ImageBytes: [],
            PixelWidth: width,
            PixelHeight: height,
            DpiScale: 1.25,
            MimeType: "image/png",
            RawBgraBytes: rawBgra);

        var windowsOcr = new WindowsOcrProvider();
        var ocrResult = await windowsOcr.RecognizeAsync(zeroCopyImage, new OcrConfig());
        Assert.True(ocrResult.IsSuccess, ocrResult.ErrorMessage);
        Assert.Equal(OcrProviderKind.WindowsNative, ocrResult.ProviderKind);

        // CapturedScreenshot.ToOcrImage() preserves RawBgraBytes for WindowsOcrProvider zero-copy
        var screenshot = new CapturedScreenshot(
            PngBytes: [1, 2, 3],
            CaptureBounds: new ScreenRect(0, 0, width, height),
            DpiScale: 1.5,
            PixelWidth: width,
            PixelHeight: height,
            RawBgraBytes: rawBgra,
            CopyOnly: true);
        var converted = screenshot.ToOcrImage();
        Assert.Same(rawBgra, converted.RawBgraBytes);
        Assert.True(screenshot.CopyOnly);
    }

    [Fact]
    public async Task ClipboardBackupService_FailedVsEmptySnapshotProtection_AndWordLookupClassifier()
    {
        var backupService = new ClipboardBackupService();

        // ClipboardSnapshot.Failed vs Empty invariants
        Assert.False(ClipboardSnapshot.Failed.CaptureSucceeded);
        Assert.True(ClipboardSnapshot.Failed.IsEmpty);

        Assert.True(ClipboardSnapshot.Empty.CaptureSucceeded);
        Assert.True(ClipboardSnapshot.Empty.IsEmpty);

        // Restoring a Failed snapshot MUST return false immediately and never touch/clear system clipboard!
        Assert.False(backupService.RestoreSnapshot(ClipboardSnapshot.Failed));
        Assert.False(await backupService.RestoreSnapshotAsync(ClipboardSnapshot.Failed));

        // WordLookupClassifier single words, short idioms, CJK words vs sentences & camelCase
        Assert.True(WordLookupClassifier.IsDictionaryLookupCandidate("ephemeral"));
        Assert.True(WordLookupClassifier.IsDictionaryLookupCandidate("piece of cake"));
        Assert.True(WordLookupClassifier.IsDictionaryLookupCandidate("鲁棒性"));
        Assert.False(WordLookupClassifier.IsDictionaryLookupCandidate("getWindowRect"));
        Assert.False(WordLookupClassifier.IsDictionaryLookupCandidate("This is a full sentence."));
    }

    [Fact]
    public async Task JsonSettingsStore_PortableModePath_AndCorruptJsonRecovery()
    {
        // 1. Portable marker file switches default settings path to <BaseDirectory>/data/settings.json
        var markerPath = Path.Combine(AppContext.BaseDirectory, "portable");
        var createdMarker = false;
        try
        {
            if (!File.Exists(markerPath))
            {
                await File.WriteAllTextAsync(markerPath, string.Empty);
                createdMarker = true;
            }

            Assert.True(JsonSettingsStore.IsPortableMode());
            var expectedPortablePath = Path.Combine(AppContext.BaseDirectory, "data", "settings.json");
            Assert.Equal(expectedPortablePath, JsonSettingsStore.GetDefaultSettingsPath());
        }
        finally
        {
            if (createdMarker && File.Exists(markerPath))
            {
                File.Delete(markerPath);
            }
        }

        // 2. Corrupt JSON recovery (truncated JSON + literal "null") backs up to .corrupt and restores defaults
        var corruptPath = Path.Combine(_tempDir, "corrupt-settings.json");
        await File.WriteAllTextAsync(corruptPath, "{\"SchemaVersion\": 1, \"Providers\": [broken!", Encoding.UTF8);

        var corruptStore = new JsonSettingsStore(_protector, corruptPath);
        var recovered = await corruptStore.LoadAsync();

        Assert.True(File.Exists(corruptPath + ".corrupt"));
        Assert.Equal(LanguageCode.ChineseSimplified, recovered.PrimaryTargetLanguage);
        Assert.NotEmpty(recovered.Providers);

        var nullJsonPath = Path.Combine(_tempDir, "null-root-settings.json");
        await File.WriteAllTextAsync(nullJsonPath, "null", Encoding.UTF8);
        var nullStore = new JsonSettingsStore(_protector, nullJsonPath);
        var recoveredFromNull = await nullStore.LoadAsync();

        Assert.True(File.Exists(nullJsonPath + ".corrupt"));
        Assert.NotEmpty(recoveredFromNull.Providers);
    }

    [Fact]
    public async Task DedicatedPolishProviderRole_TranslationSkipsPolishRole_WhilePolishPrefersDedicatedRoleAndSupportsExplicitIds()
    {
        var capturedRequests = new List<(string AuthToken, string SystemPrompt)>();
        var handler = new DelegateHttpHandler(async (req, ct) =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("google.com/translate_a/", StringComparison.Ordinal) ||
                url.Contains("googleapis.com/translate_a/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""[["你好世界","en"]]""", Encoding.UTF8, "application/json"),
                };
            }

            var body = await req.Content!.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var sysPrompt = doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString() ?? string.Empty;
            var token = req.Headers.Authorization?.Parameter ?? string.Empty;
            lock (capturedRequests)
            {
                capturedRequests.Add((token, sysPrompt));
            }

            var sse =
                $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"[{token}]\"}}}}]}}\n\n" +
                "data: [DONE]\n\n";
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8),
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return resp;
        });

        var store = new JsonSettingsStore(_protector, _settingsPath);
        var settings = AppSettings.CreateDefault();
        settings.Providers =
        [
            new ProviderInstanceConfig
            {
                Id = "free-translate",
                DisplayName = "Google Free",
                Kind = ProviderKind.FreeTraditional,
                Role = ProviderRole.Translate,
                FreeEngine = FreeTranslatorEngine.Google,
                Enabled = true,
                SortOrder = 0,
            },
            new ProviderInstanceConfig
            {
                Id = "llm-translate",
                DisplayName = "LLM Translator",
                Kind = ProviderKind.OpenAiCompatible,
                Role = ProviderRole.Translate,
                Enabled = true,
                SortOrder = 1,
                BaseUrl = "https://mock-llm.local/v1",
                Model = "gpt-4o-mini",
                EncryptedApiKey = _protector.Protect("sk-llm-translate"),
            },
            new ProviderInstanceConfig
            {
                Id = "llm-polish-dedicated",
                DisplayName = "AI 文本润色",
                Kind = ProviderKind.OpenAiCompatible,
                Role = ProviderRole.Polish,
                TriggerMode = PolishTriggerMode.OnDemand,
                DefaultPolishStyle = PolishStyle.Academic,
                Enabled = true,
                SortOrder = 2,
                BaseUrl = "https://mock-llm.local/v1",
                Model = "gpt-4o-mini",
                EncryptedApiKey = _protector.Protect("sk-llm-polish"),
                SystemPrompt = "Use crisp executive bullet phrasing.",
            },
            new ProviderInstanceConfig
            {
                Id = "llm-polish-disabled",
                DisplayName = "Disabled Polish",
                Kind = ProviderKind.OpenAiCompatible,
                Role = ProviderRole.Polish,
                TriggerMode = PolishTriggerMode.AutoParallel,
                Enabled = false,
                SortOrder = 3,
                BaseUrl = "https://mock-llm.local/v1",
                Model = "gpt-4o-mini",
                EncryptedApiKey = _protector.Protect("sk-llm-disabled"),
            },
        ];
        await store.SaveAsync(settings);

        // Verify JSON persistence and Clone() fidelity for Role, TriggerMode, and DefaultPolishStyle
        var reloaded = await store.LoadAsync();
        var dedicatedReloaded = reloaded.Providers.Single(p => p.Id == "llm-polish-dedicated");
        var dedicatedCloned = dedicatedReloaded.Clone();
        Assert.Equal(ProviderRole.Polish, dedicatedCloned.Role);
        Assert.Equal(PolishTriggerMode.OnDemand, dedicatedCloned.TriggerMode);
        Assert.Equal(PolishStyle.Academic, dedicatedCloned.DefaultPolishStyle);

        var factory = new StubProxyHttpClientFactory(handler);
        var detector = new LocalLanguageDetector();
        var preprocessor = new TextPreprocessor();

        var translationOrchestrator = new TranslationOrchestrator(
            store,
            preprocessor,
            new DualTargetLanguageRouter(detector),
            [
                new FreeTraditionalTranslationProvider(factory),
                new OpenAiCompatibleTranslationProvider(factory, _protector, store),
            ]);

        var polishOrchestrator = new PolishOrchestrator(
            store,
            preprocessor,
            detector,
            [new OpenAiCompatiblePolishProvider(factory, _protector)]);

        // 1. Default TranslationOrchestrator skips ProviderRole.Polish instances completely
        var translateBatch = await translationOrchestrator.TranslateAllAsync(new TranslationRequest(
            Text: "Hello world",
            SourceLanguage: LanguageCode.English,
            TargetLanguage: LanguageCode.ChineseSimplified));

        Assert.Equal(["free-translate", "llm-translate"], translateBatch.Results.Select(r => r.ProviderInstanceId).ToList());
        Assert.All(translateBatch.Results, r => Assert.True(r.IsSuccess, r.ErrorMessage));
        Assert.DoesNotContain(capturedRequests, r => r.AuthToken == "sk-llm-polish");

        // 2. Default PolishOrchestrator prefers dedicated ProviderRole.Polish instance (and applies instance Custom prompt fallback)
        Assert.Equal(["llm-polish-dedicated"], polishOrchestrator.GetEligibleProviders().Select(p => p.Id).ToList());

        capturedRequests.Clear();
        var polishResults = await polishOrchestrator.PolishAllAsync(new PolishRequest(
            Text: "Draft notes for review.",
            Style: PolishStyle.Custom,
            CustomPrompt: null));

        var singlePolish = Assert.Single(polishResults);
        Assert.Equal("llm-polish-dedicated", singlePolish.ProviderInstanceId);
        Assert.True(singlePolish.IsSuccess, singlePolish.ErrorMessage);
        Assert.Equal("[sk-llm-polish]", singlePolish.TranslatedText);
        var polishCall = Assert.Single(capturedRequests);
        Assert.Contains("Use crisp executive bullet phrasing.", polishCall.SystemPrompt, StringComparison.Ordinal);

        // 3. Explicit ProviderInstanceIds in PolishOrchestrator runs requested OpenAiCompatible IDs regardless of Role or Enabled
        var explicitPolish = await polishOrchestrator.PolishAllAsync(new PolishRequest(
            Text: "Explicit target polish.",
            Style: PolishStyle.Concise,
            ProviderInstanceIds: ["free-translate", "llm-translate", "llm-polish-disabled"]));

        Assert.Equal(
            ["llm-translate", "llm-polish-disabled"],
            explicitPolish.Select(r => r.ProviderInstanceId).ToList());
        Assert.All(explicitPolish, r => Assert.True(r.IsSuccess, r.ErrorMessage));

        // 4. Fallback: when 0 dedicated Polish providers are enabled, GetEligibleProviders / PolishAllAsync fall back to enabled OpenAI providers
        reloaded.Providers.Single(p => p.Id == "llm-polish-dedicated").Enabled = false;
        await store.SaveAsync(reloaded);

        Assert.Equal(["llm-translate"], polishOrchestrator.GetEligibleProviders().Select(p => p.Id).ToList());
        var fallbackPolish = await polishOrchestrator.PolishAllAsync(new PolishRequest("Fallback polish.", PolishStyle.General));
        Assert.Equal("llm-translate", Assert.Single(fallbackPolish).ProviderInstanceId);
    }

    [Fact]
    public async Task ProvidersAndOcrSettingsViewModels_AutoPersistApiKeysAndEndpointsWithoutManualSave_AndRuntimeSubdirPortablePath()
    {
        var store = new JsonSettingsStore(_protector, _settingsPath);
        var initial = AppSettings.CreateDefault();
        await store.SaveAsync(initial);

        var modelsHandler = new DelegateHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"id":"deepseek-chat"},{"id":"deepseek-reasoner"}]}""", Encoding.UTF8, "application/json"),
            }));
        var factory = new StubProxyHttpClientFactory(modelsHandler);
        var detector = new LocalLanguageDetector();
        var preprocessor = new TextPreprocessor();
        var translationOrchestrator = new TranslationOrchestrator(
            store,
            preprocessor,
            new DualTargetLanguageRouter(detector),
            [new OpenAiCompatibleTranslationProvider(factory, _protector, store)]);
        var modelDiscovery = new OpenAiModelDiscoveryService(factory);

        var providersVm = new PopTranslate.App.ViewModels.ProvidersSettingsViewModel(
            store,
            _protector,
            translationOrchestrator,
            modelDiscovery);

        providersVm.LoadFrom(store.Current);

        // Select the default disabled OpenAI provider and configure BaseUrl + ApiKey WITHOUT clicking Save
        var defaultLlm = providersVm.Providers.Single(p => p.Id == "openai-compatible-default");
        Assert.False(defaultLlm.Enabled);
        providersVm.SelectedProvider = defaultLlm;

        defaultLlm.BaseUrl = "https://api.deepseek.com/v1";
        defaultLlm.Model = "deepseek-chat";
        defaultLlm.ApiKeyPlaintext = "sk-live-deepseek-secret-key";

        // Wait for debounced auto-save to flush to disk
        await Task.Delay(500);

        var reloadedFromDisk = await store.LoadAsync();
        var savedLlm = reloadedFromDisk.Providers.Single(p => p.Id == "openai-compatible-default");
        Assert.Equal("https://api.deepseek.com/v1", savedLlm.BaseUrl);
        Assert.Equal("deepseek-chat", savedLlm.Model);
        Assert.True(savedLlm.Enabled);
        Assert.NotEmpty(savedLlm.EncryptedApiKey);
        Assert.True(_protector.TryUnprotect(savedLlm.EncryptedApiKey, out var decryptedKey));
        Assert.Equal("sk-live-deepseek-secret-key", decryptedKey);

        // FetchModelsCommand immediately persists freshly edited key/url before debounce expires
        defaultLlm.ApiKeyPlaintext = "sk-updated-before-fetch";
        await providersVm.FetchModelsCommand.ExecuteAsync(null);
        var afterFetch = await store.LoadAsync();
        var savedAfterFetch = afterFetch.Providers.Single(p => p.Id == "openai-compatible-default");
        Assert.True(_protector.TryUnprotect(savedAfterFetch.EncryptedApiKey, out var fetchDecrypted));
        Assert.Equal("sk-updated-before-fetch", fetchDecrypted);

        // Vision OCR ViewModel also auto-saves API key & BaseUrl without manual Save click
        var ocrOrchestrator = new OcrOrchestrator(store, preprocessor, [new WindowsOcrProvider()]);
        var ocrVm = new PopTranslate.App.ViewModels.OcrSettingsViewModel(store, _protector, ocrOrchestrator, modelDiscovery);
        await ocrVm.LoadFromAsync(store.Current);

        ocrVm.VisionBaseUrl = "https://api.siliconflow.cn/v1";
        ocrVm.VisionApiKeyPlaintext = "sk-vision-auto-saved";
        await Task.Delay(500);

        var afterOcr = await store.LoadAsync();
        Assert.Equal("https://api.siliconflow.cn/v1", afterOcr.Ocr.VisionBaseUrl);
        Assert.True(_protector.TryUnprotect(afterOcr.Ocr.EncryptedVisionApiKey, out var decryptedVisionKey));
        Assert.Equal("sk-vision-auto-saved", decryptedVisionKey);

        // Clean Release layout: runtime/ subdirectory detects parent portable marker
        var releaseRoot = Path.Combine(_tempDir, "PopTranslate-win-x64");
        var runtimeDir = Path.Combine(releaseRoot, "runtime");
        Directory.CreateDirectory(runtimeDir);
        await File.WriteAllTextAsync(Path.Combine(releaseRoot, "portable"), string.Empty);

        Assert.Equal(
            Path.Combine(releaseRoot, "data", "settings.json"),
            JsonSettingsStore.GetDefaultSettingsPath(runtimeDir));
    }

    [Fact]
    public async Task TrayAndHoverContextMenuCommands_ToggleHoverIconDeterministically_AndReplaceTranslateSequentialFailover()
    {
        var store = new JsonSettingsStore(_protector, _settingsPath);
        var initial = AppSettings.CreateDefault();
        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Alt, 0x52), initial.Hotkeys.ReplaceTranslate);
        Assert.Equal("Alt + R", initial.Hotkeys.ReplaceTranslate.DisplayText);
        await store.SaveAsync(initial);

        // 1. System Tray PopupMenu commands (H.NotifyIcon PopupMenu invokes ICommand, NOT XAML Click)
        using var tray = new PopTranslate.App.Services.TrayIconHost();
        tray.SyncMenuState(initial, isRunningAsAdministrator: false, isHoverMuted: false, isClipboardListenerEnabled: false);

        Assert.True(tray.IsHoverIconEnabled);
        Assert.Contains("Alt + R", tray.ReplaceTranslateLabel, StringComparison.Ordinal);

        var observedHoverToggles = new List<bool>();
        var observedMuteToggles = new List<bool>();
        var replaceRequestedCount = 0;
        tray.HoverIconToggled += (_, enabled) => observedHoverToggles.Add(enabled);
        tray.HoverMuteToggled += (_, muted) => observedMuteToggles.Add(muted);
        tray.ReplaceTranslateRequested += (_, _) => replaceRequestedCount++;

        tray.ToggleHoverIconCommand.Execute(null);
        Assert.Equal([false], observedHoverToggles);
        Assert.False(tray.IsHoverIconEnabled);

        tray.ToggleHoverIconCommand.Execute(null);
        Assert.Equal([false, true], observedHoverToggles);
        Assert.True(tray.IsHoverIconEnabled);

        tray.ToggleHoverMuteCommand.Execute(null);
        Assert.Equal([true], observedMuteToggles);

        tray.ReplaceTranslateCommand.Execute(null);
        Assert.Equal(1, replaceRequestedCount);

        // 2. Floating Hover Icon native Win32 context menu items (reliable on WS_EX_NOACTIVATE windows)
        var hoverMenuItems = PopTranslate.App.Windows.FloatingHoverIconWindow.BuildContextMenuItems("Code");
        Assert.Contains(hoverMenuItems, item => item.Text == "替换翻译选中文本" && item.IsEnabled);
        Assert.Contains(hoverMenuItems, item => item.Text == "屏蔽当前应用 (Code.exe)" && item.IsEnabled);
        Assert.Contains(hoverMenuItems, item => item.Text == "暂停划词 30 分钟" && item.IsEnabled);
        Assert.Contains(hoverMenuItems, item => item.Text == "关闭划词悬浮图标" && item.IsEnabled);
        Assert.Contains(hoverMenuItems, item => item.Text == "打开设置..." && item.IsEnabled);
        Assert.Contains(hoverMenuItems, item => item.Text == "退出程序" && item.IsEnabled);

        // 2b. Real Win32 tray message window + icon loader + synchronous UpdateState re-entrancy survival (regression for mute-30m killing right-click)
        var observedTrayTooltips = new List<string>();
        var deletedCount = 0;
        using (var realPumpProbe = new PopTranslate.Win32.Windowing.Win32TrayIconService())
        {
            var probeOk = realPumpProbe.CreateOrUpdate("Probe", null, static () => []);
            Assert.True(realPumpProbe.IsMessageWindowReady);
            Assert.True(realPumpProbe.HasLoadedIcon);
            if (PopTranslate.Win32.Windowing.Win32TrayIconService.IsShellTrayAvailable())
            {
                Assert.True(probeOk);
            }
        }

        using (var nativeTrayBackend = new PopTranslate.Win32.Windowing.Win32TrayIconService((msg, tip) =>
        {
            if (msg == 2 /* NIM_DELETE */)
            {
                deletedCount++;
            }
            else
            {
                observedTrayTooltips.Add($"{msg}:{tip}");
            }

            return true;
        }))
        using (var liveTray = new PopTranslate.App.Services.TrayIconHost(nativeTrayBackend))
        {
            var exitCount = 0;
            var muteEvents = new List<bool>();

            liveTray.HoverMuteToggled += (_, muted) =>
            {
                muteEvents.Add(muted);
                // Synchronously call UpdateState inside the click callback (exact sequence that previously broke H.NotifyIcon)
                liveTray.UpdateState(initial, isRunningAsAdministrator: false, isHoverMuted: muted, isClipboardListenerEnabled: false);
            };
            liveTray.ExitRequested += (_, _) => exitCount++;

            liveTray.Initialize(initial, isRunningAsAdministrator: false, uiDispatcher: null, isHoverMuted: false, isClipboardListenerEnabled: false);
            Assert.True(liveTray.IsTrayIconCreated);
            Assert.True(nativeTrayBackend.IsMessageWindowReady);
            Assert.True(nativeTrayBackend.HasLoadedIcon);

            var initialMenu = liveTray.BuildMenuItems();
            Assert.Contains(initialMenu, m => m.Id == PopTranslate.App.Services.TrayIconHost.CommandToggleHoverMute && !m.IsChecked);
            Assert.Contains(initialMenu, m => m.Id == PopTranslate.App.Services.TrayIconHost.CommandExit && m.IsEnabled);

            liveTray.DispatchMenuCommand(PopTranslate.App.Services.TrayIconHost.CommandToggleHoverMute);
            Assert.Equal([true], muteEvents);
            Assert.True(liveTray.IsTrayIconCreated);
            Assert.Contains(observedTrayTooltips, x => x.Contains("划词已暂停", StringComparison.Ordinal));

            var menuAfterMute = liveTray.BuildMenuItems();
            Assert.Contains(menuAfterMute, m => m.Id == PopTranslate.App.Services.TrayIconHost.CommandToggleHoverMute && m.IsChecked && m.Text.Contains("已暂停", StringComparison.Ordinal));
            Assert.Contains(menuAfterMute, m => m.Id == PopTranslate.App.Services.TrayIconHost.CommandExit && m.IsEnabled);

            liveTray.DispatchMenuCommand(PopTranslate.App.Services.TrayIconHost.CommandExit);
            Assert.Equal(1, exitCount);
        }

        Assert.Equal(1, deletedCount);

        // 2c. TranslationPopupWindow XAML must hide right-side scrollbar chrome while keeping vertical scroll enabled
        var repoRoot = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(repoRoot) && !File.Exists(Path.Combine(repoRoot, "PopTranslate.sln")))
        {
            repoRoot = Path.GetDirectoryName(repoRoot);
        }

        Assert.NotNull(repoRoot);
        var popupXamlPath = Path.Combine(repoRoot!, "src", "PopTranslate.App", "Windows", "TranslationPopupWindow.xaml");
        var popupXaml = await File.ReadAllTextAsync(popupXamlPath);
        Assert.DoesNotContain("VerticalScrollBarVisibility=\"Auto\"", popupXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("VerticalScrollBarVisibility=\"Visible\"", popupXaml, StringComparison.Ordinal);
        Assert.Contains("VerticalScrollBarVisibility=\"Hidden\"", popupXaml, StringComparison.Ordinal);

        // 2d. HotkeysSettingsPage must NOT contain redundant 输入翻译快捷键 row
        var hotkeysXamlPath = Path.Combine(repoRoot!, "src", "PopTranslate.App", "Pages", "HotkeysSettingsPage.xaml");
        var hotkeysXaml = await File.ReadAllTextAsync(hotkeysXamlPath);
        Assert.DoesNotContain("输入翻译快捷键", hotkeysXaml, StringComparison.Ordinal);

        // 2e. Best-practice user config persistence: Roaming %APPDATA%, legacy %LOCALAPPDATA% migration, .bak backup recovery, and non-destructive publish-release.ps1
        var roamingExpected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PopTranslate",
            "settings.json");
        var nonPortableDir = Path.Combine(_tempDir, "non-portable-bin");
        Directory.CreateDirectory(nonPortableDir);
        Assert.Equal(roamingExpected, JsonSettingsStore.GetDefaultSettingsPath(nonPortableDir));

        var legacyLocalFile = Path.Combine(_tempDir, "legacy-local", "settings.json");
        var newRoamingFile = Path.Combine(_tempDir, "roaming-appdata", "settings.json");
        var legacySeedStore = new JsonSettingsStore(_protector, legacyLocalFile);
        var seedSettings = AppSettings.CreateDefault();
        seedSettings.Providers.Add(new ProviderInstanceConfig
        {
            Id = "cpa-custom",
            DisplayName = "CPA",
            Kind = ProviderKind.OpenAiCompatible,
            Enabled = true,
            BaseUrl = "https://cpa.example.com/v1",
            Model = "gpt-4o",
            EncryptedApiKey = _protector.Protect("sk-cpa-secret-persistent"),
        });
        await legacySeedStore.SaveAsync(seedSettings);

        var migratedStore = new JsonSettingsStore(_protector, newRoamingFile, legacyFallbackFilePath: legacyLocalFile);
        var migratedLoaded = await migratedStore.LoadAsync();
        Assert.True(File.Exists(newRoamingFile));
        var migratedCpa = Assert.Single(migratedLoaded.Providers, p => p.Id == "cpa-custom");
        Assert.True(_protector.TryUnprotect(migratedCpa.EncryptedApiKey, out var migratedKey));
        Assert.Equal("sk-cpa-secret-persistent", migratedKey);

        // Second save writes rolling .bak backup, and corrupting primary settings.json recovers from .bak instead of wiping providers!
        migratedLoaded.Window.PopupWidth = 500;
        await migratedStore.SaveAsync(migratedLoaded);
        Assert.True(File.Exists(newRoamingFile + ".bak"));

        await File.WriteAllTextAsync(newRoamingFile, "{truncated-corrupt!", Encoding.UTF8);
        var recoveredFromBakStore = new JsonSettingsStore(_protector, newRoamingFile);
        var recoveredSettings = await recoveredFromBakStore.LoadAsync();
        Assert.Contains(recoveredSettings.Providers, p => p.Id == "cpa-custom");

        // publish-release.ps1 must only replace runtime/ and binaries, NEVER deleting release/PopTranslate-win-x64/data or portable marker
        var publishScript = await File.ReadAllTextAsync(Path.Combine(repoRoot!, "scripts", "publish-release.ps1"));
        Assert.DoesNotContain("Remove-Item -LiteralPath $distRoot", publishScript, StringComparison.Ordinal);

        // 3. HotkeysSettingsViewModel persists ReplaceTranslateGesture and syncs external EnableHoverIcon changes
        var hotkeysVm = new PopTranslate.App.ViewModels.HotkeysSettingsViewModel(store);
        hotkeysVm.LoadFrom(store.Current);
        Assert.Equal(HotkeyConfig.DefaultReplaceTranslate, hotkeysVm.ReplaceTranslateGesture);

        var customReplaceGesture = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x52);
        hotkeysVm.UpdateGesture(HotkeyAction.ReplaceTranslate, customReplaceGesture);
        await Task.Delay(150);

        var afterHotkeySave = await store.LoadAsync();
        Assert.Equal(customReplaceGesture, afterHotkeySave.Hotkeys.ReplaceTranslate);

        hotkeysVm.ResetDefaultHotkeysCommand.Execute(null);
        await Task.Delay(150);
        var afterReset = await store.LoadAsync();
        Assert.Equal(HotkeyConfig.DefaultReplaceTranslate, afterReset.Hotkeys.ReplaceTranslate);

        // 4. Replace-Translate sequential failover: skips Polish role, fails over from broken #1 to #2, and does NOT waste tokens on #3
        var invokedTokens = new List<string>();
        var handler = new DelegateHttpHandler((req, _) =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("google.com/translate_a/", StringComparison.Ordinal) ||
                url.Contains("googleapis.com/translate_a/", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""[["Replaced in place!","zh-CN"]]""", Encoding.UTF8, "application/json"),
                });
            }

            var token = req.Headers.Authorization?.Parameter ?? string.Empty;
            lock (invokedTokens)
            {
                invokedTokens.Add(token);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("""{"error":{"message":"Upstream gateway timeout"}}""", Encoding.UTF8, "application/json"),
            });
        });

        var failoverSettings = store.Current.Clone();
        failoverSettings.Providers =
        [
            new ProviderInstanceConfig
            {
                Id = "polish-only",
                DisplayName = "Polish Only",
                Kind = ProviderKind.OpenAiCompatible,
                Role = ProviderRole.Polish,
                Enabled = true,
                SortOrder = 0,
                BaseUrl = "https://mock-llm.local/v1",
                Model = "gpt-4o-mini",
                EncryptedApiKey = _protector.Protect("sk-polish-must-skip"),
            },
            new ProviderInstanceConfig
            {
                Id = "primary-broken-llm",
                DisplayName = "Primary Broken LLM",
                Kind = ProviderKind.OpenAiCompatible,
                Role = ProviderRole.Translate,
                Enabled = true,
                SortOrder = 1,
                BaseUrl = "https://mock-llm.local/v1",
                Model = "gpt-4o-mini",
                EncryptedApiKey = _protector.Protect("sk-primary-broken"),
            },
            new ProviderInstanceConfig
            {
                Id = "secondary-google-free",
                DisplayName = "Google Free Fallback",
                Kind = ProviderKind.FreeTraditional,
                Role = ProviderRole.Translate,
                FreeEngine = FreeTranslatorEngine.Google,
                Enabled = true,
                SortOrder = 2,
            },
            new ProviderInstanceConfig
            {
                Id = "tertiary-unused-llm",
                DisplayName = "Tertiary LLM",
                Kind = ProviderKind.OpenAiCompatible,
                Role = ProviderRole.Translate,
                Enabled = true,
                SortOrder = 3,
                BaseUrl = "https://mock-llm.local/v1",
                Model = "gpt-4o-mini",
                EncryptedApiKey = _protector.Protect("sk-tertiary-unused"),
            },
        ];
        await store.SaveAsync(failoverSettings);

        var factory = new StubProxyHttpClientFactory(handler);
        var detector = new LocalLanguageDetector();
        var orchestrator = new TranslationOrchestrator(
            store,
            new TextPreprocessor(),
            new DualTargetLanguageRouter(detector),
            [
                new FreeTraditionalTranslationProvider(factory),
                new OpenAiCompatibleTranslationProvider(factory, _protector, store),
            ]);

        var replaceResult = await orchestrator.TranslateFirstAvailableAsync(new TranslationRequest(
            Text: "原地替换测试",
            SourceLanguage: LanguageCode.Auto,
            TargetLanguage: LanguageCode.Auto));

        Assert.True(replaceResult.IsSuccess, replaceResult.ErrorMessage);
        Assert.Equal("secondary-google-free", replaceResult.ProviderInstanceId);
        Assert.Equal("Replaced in place!", replaceResult.TranslatedText);
        Assert.Equal(["sk-primary-broken"], invokedTokens);
    }

    private sealed class DelayedChunkStream : Stream
    {
        private readonly MemoryStream _buffer = new();
        private readonly Func<Func<string, CancellationToken, Task>, Task> _producer;
        private readonly SemaphoreSlim _signal = new(0);
        private readonly Lock _sync = new();
        private long _readPos;
        private bool _completed;
        private Exception? _error;
        private bool _started;

        public DelayedChunkStream(Func<Func<string, CancellationToken, Task>, Task> producer)
        {
            _producer = producer;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        private void EnsureStarted()
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    await _producer(async (chunk, ct) =>
                    {
                        ct.ThrowIfCancellationRequested();
                        var bytes = Encoding.UTF8.GetBytes(chunk);
                        lock (_sync)
                        {
                            _buffer.Position = _buffer.Length;
                            _buffer.Write(bytes, 0, bytes.Length);
                        }

                        _signal.Release();
                        await Task.Yield();
                    });
                }
                catch (Exception ex)
                {
                    lock (_sync)
                    {
                        _error = ex;
                    }
                }
                finally
                {
                    lock (_sync)
                    {
                        _completed = true;
                    }

                    _signal.Release();
                }
            });
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            EnsureStarted();

            while (true)
            {
                lock (_sync)
                {
                    if (_readPos < _buffer.Length)
                    {
                        _buffer.Position = _readPos;
                        var read = _buffer.Read(destination.Span);
                        _readPos += read;
                        return read;
                    }

                    if (_error is not null)
                    {
                        throw new IOException("Simulated stream error.", _error);
                    }

                    if (_completed)
                    {
                        return 0;
                    }
                }

                await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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

    private sealed class DelegateHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            responder(request, cancellationToken);
    }
}
