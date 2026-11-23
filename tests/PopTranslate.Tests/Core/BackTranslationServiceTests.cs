using System.Runtime.CompilerServices;
using PopTranslate.Core.Configuration;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Language;
using PopTranslate.Core.Models;
using PopTranslate.Core.Translation;
using Xunit;

namespace PopTranslate.Tests.Core;

public sealed class BackTranslationServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonSettingsStore _settingsStore;

    public BackTranslationServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PopTranslateBackTransTests_" + Guid.NewGuid().ToString("N"));
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
            // Ignore temp directory cleanup errors.
        }
    }

    [Fact]
    public async Task BackTranslateStreamAsync_UsesExactMatchingProviderInstanceAndReversesDirection()
    {
        var settings = AppSettings.CreateDefault();
        settings.Providers =
        [
            new ProviderInstanceConfig
            {
                Id = "openai-a",
                DisplayName = "OpenAI Alpha",
                Kind = ProviderKind.OpenAiCompatible,
                Enabled = true,
                Model = "gpt-4o-mini",
                SortOrder = 0,
            },
            new ProviderInstanceConfig
            {
                Id = "openai-b",
                DisplayName = "OpenAI Beta",
                Kind = ProviderKind.OpenAiCompatible,
                Enabled = false, // Even if toggled off later, back-translate for that card still targets exact instance
                Model = "gpt-4.1",
                SortOrder = 1,
            },
        ];
        await _settingsStore.SaveAsync(settings);

        TranslationRequest? capturedRequest = null;
        ProviderInstanceConfig? capturedConfig = null;

        var provider = new FakeTranslationProvider(
            ProviderKind.OpenAiCompatible,
            (req, cfg, ct) =>
            {
                capturedRequest = req;
                capturedConfig = cfg;
                return StreamChunks(["Hello", ", ", "world"], ct);
            });

        var service = CreateService(provider);
        var updates = new List<ProviderStreamUpdate>();

        await foreach (var update in service.BackTranslateStreamAsync(new BackTranslationRequest(
            ProviderInstanceId: "openai-b",
            TranslatedText: "你好，世界",
            TranslatedLanguage: LanguageCode.ChineseSimplified,
            BackTargetLanguage: LanguageCode.English)))
        {
            updates.Add(update);
        }

        Assert.NotNull(capturedConfig);
        Assert.Equal("openai-b", capturedConfig!.Id);
        Assert.Equal("gpt-4.1", capturedConfig.Model);

        Assert.NotNull(capturedRequest);
        Assert.Equal("你好，世界", capturedRequest!.Text);
        Assert.Equal(LanguageCode.ChineseSimplified, capturedRequest.SourceLanguage);
        Assert.Equal(LanguageCode.English, capturedRequest.TargetLanguage);
        Assert.False(capturedRequest.ApplyTextPreprocessing);
        Assert.False(capturedRequest.IsFromOcr);
        Assert.Equal(["openai-b"], capturedRequest.ProviderInstanceIds);

        Assert.Equal(5, updates.Count); // Pending + 3 Streaming + Completed
        Assert.Equal(ProviderStreamState.Pending, updates[0].State);
        Assert.Equal(ProviderStreamState.Streaming, updates[1].State);
        Assert.Equal("Hello", updates[1].DeltaText);
        Assert.Equal("Hello", updates[1].AccumulatedText);
        Assert.Equal(ProviderStreamState.Streaming, updates[2].State);
        Assert.Equal("Hello, ", updates[2].AccumulatedText);
        Assert.Equal(ProviderStreamState.Streaming, updates[3].State);
        Assert.Equal("Hello, world", updates[3].AccumulatedText);
        Assert.Equal(ProviderStreamState.Completed, updates[4].State);
        Assert.Equal("Hello, world", updates[4].AccumulatedText);
        Assert.Equal(LanguageCode.English, updates[4].TargetLanguage);
        Assert.Equal("OpenAI Beta", updates[4].ProviderDisplayName);
        Assert.Null(updates[4].ErrorMessage);
    }

    [Fact]
    public async Task BackTranslateAsync_AutoBackTarget_ResolvesViaLanguageRouter()
    {
        var settings = AppSettings.CreateDefault();
        settings.PrimaryTargetLanguage = LanguageCode.ChineseSimplified;
        settings.SecondaryTargetLanguage = LanguageCode.English;
        settings.Providers =
        [
            new ProviderInstanceConfig
            {
                Id = "free-1",
                DisplayName = "Free Translator",
                Kind = ProviderKind.FreeTraditional,
                Enabled = true,
            },
        ];
        await _settingsStore.SaveAsync(settings);

        TranslationRequest? capturedRequest = null;
        var provider = new FakeTranslationProvider(
            ProviderKind.FreeTraditional,
            (req, _, ct) =>
            {
                capturedRequest = req;
                return StreamChunks(["Hello from back-translation"], ct);
            });

        var service = CreateService(provider);
        var result = await service.BackTranslateAsync(new BackTranslationRequest(
            ProviderInstanceId: "free-1",
            TranslatedText: "这是一段中文翻译结果",
            TranslatedLanguage: LanguageCode.ChineseSimplified,
            BackTargetLanguage: LanguageCode.Auto));

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("Hello from back-translation", result.TranslatedText);
        Assert.Equal(LanguageCode.English, result.TargetLanguage);
        Assert.NotNull(capturedRequest);
        Assert.Equal(LanguageCode.English, capturedRequest!.TargetLanguage);
    }

    [Fact]
    public async Task BackTranslateStreamAsync_MissingProviderInstance_EmitsFailedWithoutThrowing()
    {
        var settings = AppSettings.CreateDefault();
        settings.Providers = [];
        await _settingsStore.SaveAsync(settings);

        var service = CreateService();
        var updates = new List<ProviderStreamUpdate>();

        await foreach (var update in service.BackTranslateStreamAsync(new BackTranslationRequest(
            ProviderInstanceId: "non-existent-id",
            TranslatedText: "你好",
            TranslatedLanguage: LanguageCode.ChineseSimplified,
            BackTargetLanguage: LanguageCode.English)))
        {
            updates.Add(update);
        }

        var failed = Assert.Single(updates);
        Assert.Equal(ProviderStreamState.Failed, failed.State);
        Assert.Equal("non-existent-id", failed.ProviderInstanceId);
        Assert.Contains("non-existent-id", failed.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("not found", failed.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BackTranslateStreamAsync_UnregisteredProviderKind_EmitsFailedWithoutThrowing()
    {
        var settings = AppSettings.CreateDefault();
        settings.Providers =
        [
            new ProviderInstanceConfig
            {
                Id = "deepl-1",
                DisplayName = "DeepL",
                Kind = ProviderKind.DeepL,
                Enabled = true,
            },
        ];
        await _settingsStore.SaveAsync(settings);

        // Register no providers
        var service = CreateService();
        var updates = new List<ProviderStreamUpdate>();

        await foreach (var update in service.BackTranslateStreamAsync(new BackTranslationRequest(
            ProviderInstanceId: "deepl-1",
            TranslatedText: "你好",
            TranslatedLanguage: LanguageCode.ChineseSimplified,
            BackTargetLanguage: LanguageCode.English)))
        {
            updates.Add(update);
        }

        var failed = Assert.Single(updates);
        Assert.Equal(ProviderStreamState.Failed, failed.State);
        Assert.Equal(ProviderKind.DeepL, failed.ProviderKind);
        Assert.Contains("DeepL", failed.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackTranslateStreamAsync_EmptyText_CompletesImmediatelyWithoutInvokingProvider()
    {
        var settings = AppSettings.CreateDefault();
        settings.Providers =
        [
            new ProviderInstanceConfig
            {
                Id = "openai-1",
                DisplayName = "OpenAI",
                Kind = ProviderKind.OpenAiCompatible,
                Enabled = true,
            },
        ];
        await _settingsStore.SaveAsync(settings);

        var invoked = false;
        var provider = new FakeTranslationProvider(
            ProviderKind.OpenAiCompatible,
            (_, _, ct) =>
            {
                invoked = true;
                return StreamChunks(["unexpected"], ct);
            });

        var service = CreateService(provider);
        var result = await service.BackTranslateAsync(new BackTranslationRequest(
            ProviderInstanceId: "openai-1",
            TranslatedText: "   ",
            TranslatedLanguage: LanguageCode.ChineseSimplified,
            BackTargetLanguage: LanguageCode.English));

        Assert.False(invoked);
        Assert.True(result.IsSuccess);
        Assert.Equal(string.Empty, result.TranslatedText);
    }

    [Fact]
    public async Task BackTranslateStreamAsync_ProviderThrowsMidStream_EmitsFailedWithPartialAccumulatedText()
    {
        var settings = AppSettings.CreateDefault();
        settings.Providers =
        [
            new ProviderInstanceConfig
            {
                Id = "openai-err",
                DisplayName = "Flaky LLM",
                Kind = ProviderKind.OpenAiCompatible,
                Enabled = true,
            },
        ];
        await _settingsStore.SaveAsync(settings);

        var provider = new FakeTranslationProvider(
            ProviderKind.OpenAiCompatible,
            (_, _, ct) => StreamThenThrow("Partial ", new InvalidOperationException("Simulated stream drop"), ct));

        var service = CreateService(provider);
        var updates = new List<ProviderStreamUpdate>();

        await foreach (var update in service.BackTranslateStreamAsync(new BackTranslationRequest(
            ProviderInstanceId: "openai-err",
            TranslatedText: "你好世界",
            TranslatedLanguage: LanguageCode.ChineseSimplified,
            BackTargetLanguage: LanguageCode.English)))
        {
            updates.Add(update);
        }

        Assert.Equal(ProviderStreamState.Pending, updates[0].State);
        Assert.Equal(ProviderStreamState.Streaming, updates[1].State);
        Assert.Equal("Partial ", updates[1].AccumulatedText);

        var final = updates[^1];
        Assert.Equal(ProviderStreamState.Failed, final.State);
        Assert.Equal("Partial ", final.AccumulatedText);
        Assert.Contains("Simulated stream drop", final.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackTranslateStreamAsync_ProviderYieldsChunkError_EmitsFailedUpdate()
    {
        var settings = AppSettings.CreateDefault();
        settings.Providers =
        [
            new ProviderInstanceConfig
            {
                Id = "openai-chunk-err",
                DisplayName = "Error LLM",
                Kind = ProviderKind.OpenAiCompatible,
                Enabled = true,
            },
        ];
        await _settingsStore.SaveAsync(settings);

        var provider = new FakeTranslationProvider(
            ProviderKind.OpenAiCompatible,
            (_, _, ct) => StreamErrorChunk("Rate limit exceeded (HTTP 429)", ct));

        var service = CreateService(provider);
        var result = await service.BackTranslateAsync(new BackTranslationRequest(
            ProviderInstanceId: "openai-chunk-err",
            TranslatedText: "测试",
            TranslatedLanguage: LanguageCode.ChineseSimplified,
            BackTargetLanguage: LanguageCode.English));

        Assert.False(result.IsSuccess);
        Assert.Contains("429", result.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackTranslateStreamAsync_MidStreamCancellation_EmitsCancelledAndExitsPromptly()
    {
        var settings = AppSettings.CreateDefault();
        settings.Providers =
        [
            new ProviderInstanceConfig
            {
                Id = "openai-cancel",
                DisplayName = "Slow LLM",
                Kind = ProviderKind.OpenAiCompatible,
                Enabled = true,
            },
        ];
        await _settingsStore.SaveAsync(settings);

        var providerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeTranslationProvider(
            ProviderKind.OpenAiCompatible,
            (_, _, ct) => StreamThenHang("First", providerStarted, ct));

        var service = CreateService(provider);
        using var cts = new CancellationTokenSource();
        var updates = new List<ProviderStreamUpdate>();

        var streamTask = Task.Run(async () =>
        {
            await foreach (var update in service.BackTranslateStreamAsync(
                new BackTranslationRequest(
                    ProviderInstanceId: "openai-cancel",
                    TranslatedText: "测试取消",
                    TranslatedLanguage: LanguageCode.ChineseSimplified,
                    BackTargetLanguage: LanguageCode.English),
                cts.Token))
            {
                updates.Add(update);
                if (update.State == ProviderStreamState.Streaming)
                {
                    cts.Cancel();
                }
            }
        });

        await providerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await streamTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(updates, u => u.State == ProviderStreamState.Streaming && u.AccumulatedText == "First");
        Assert.Equal(ProviderStreamState.Cancelled, updates[^1].State);
        Assert.Equal("First", updates[^1].AccumulatedText);
    }

    private BackTranslationService CreateService(params ITranslationProvider[] providers)
    {
        var detector = new LocalLanguageDetector();
        var router = new DualTargetLanguageRouter(detector);
        return new BackTranslationService(_settingsStore, router, providers);
    }

    private static async IAsyncEnumerable<TranslationChunk> StreamChunks(
        string[] deltas,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var delta in deltas)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new TranslationChunk(delta, IsComplete: false);
        }

        yield return new TranslationChunk(string.Empty, IsComplete: true);
    }

    private static async IAsyncEnumerable<TranslationChunk> StreamThenThrow(
        string firstDelta,
        Exception ex,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return new TranslationChunk(firstDelta, IsComplete: false);
        await Task.Yield();
        throw ex;
    }

    private static async IAsyncEnumerable<TranslationChunk> StreamErrorChunk(
        string errorMessage,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return new TranslationChunk(string.Empty, IsComplete: true, ErrorMessage: errorMessage);
    }

    private static async IAsyncEnumerable<TranslationChunk> StreamThenHang(
        string firstDelta,
        TaskCompletionSource started,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        yield return new TranslationChunk(firstDelta, IsComplete: false);
        started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    private sealed class FakeTranslationProvider(
        ProviderKind kind,
        Func<TranslationRequest, ProviderInstanceConfig, CancellationToken, IAsyncEnumerable<TranslationChunk>> streamFactory) : ITranslationProvider
    {
        public ProviderKind Kind => kind;

        public bool SupportsStreaming => true;

        public IAsyncEnumerable<TranslationChunk> TranslateStreamAsync(
            TranslationRequest request,
            ProviderInstanceConfig config,
            CancellationToken cancellationToken = default) =>
            streamFactory(request, config, cancellationToken);

        public Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            ProviderInstanceConfig config,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<ProviderHealthCheckResult> TestConnectionAsync(
            ProviderInstanceConfig config,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderHealthCheckResult(true, "OK", TimeSpan.Zero));
    }
}
