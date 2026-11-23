using System.Runtime.CompilerServices;
using PopTranslate.Core.Configuration;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Language;
using PopTranslate.Core.Models;
using PopTranslate.Core.Translation;
using Xunit;

namespace PopTranslate.Tests.Core;

public sealed class TranslationOrchestratorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonSettingsStore _settingsStore;

    public TranslationOrchestratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PopTranslateOrchTests_" + Guid.NewGuid().ToString("N"));
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
            // Ignore temp cleanup errors.
        }
    }

    [Fact]
    public async Task TranslateParallelAsync_RunsProvidersConcurrently_FastProviderCompletesWhileSlowProviderStreams()
    {
        var slowGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastCompleted = false;

        var settings = AppSettings.CreateDefault();
        settings.Providers =
        [
            new ProviderInstanceConfig
            {
                Id = "slow-llm",
                DisplayName = "Slow LLM",
                Kind = ProviderKind.OpenAiCompatible,
                Enabled = true,
                SortOrder = 0,
            },
            new ProviderInstanceConfig
            {
                Id = "fast-free",
                DisplayName = "Fast Free",
                Kind = ProviderKind.FreeTraditional,
                Enabled = true,
                SortOrder = 1,
            },
        ];
        await _settingsStore.SaveAsync(settings);

        var slowProvider = new FakeTranslationProvider(
            ProviderKind.OpenAiCompatible,
            supportsStreaming: true,
            streamFactory: async (req, cfg, ct) =>
            {
                async IAsyncEnumerable<TranslationChunk> Stream([EnumeratorCancellation] CancellationToken token)
                {
                    yield return new TranslationChunk("Part1-", IsComplete: false);
                    await slowGate.Task.WaitAsync(token);
                    yield return new TranslationChunk("Part2", IsComplete: false);
                    yield return new TranslationChunk(string.Empty, IsComplete: true);
                }

                await Task.CompletedTask;
                return Stream(ct);
            });

        var fastProvider = new FakeTranslationProvider(
            ProviderKind.FreeTraditional,
            supportsStreaming: false,
            streamFactory: async (req, cfg, ct) =>
            {
                async IAsyncEnumerable<TranslationChunk> Stream([EnumeratorCancellation] CancellationToken token)
                {
                    await Task.Yield();
                    yield return new TranslationChunk("FastResult", IsComplete: false);
                    yield return new TranslationChunk(string.Empty, IsComplete: true);
                }

                await Task.CompletedTask;
                return Stream(ct);
            });

        var orchestrator = CreateOrchestrator(slowProvider, fastProvider);
        var updates = new List<ProviderStreamUpdate>();

        await foreach (var update in orchestrator.TranslateParallelAsync(
            new TranslationRequest("Hello world", LanguageCode.Auto, LanguageCode.Auto)))
        {
            updates.Add(update);
            if (update.ProviderInstanceId == "fast-free" && update.State == ProviderStreamState.Completed)
            {
                fastCompleted = true;
                // Only release slow provider AFTER fast provider has already completed!
                slowGate.TrySetResult();
            }
        }

        Assert.True(fastCompleted);

        var fastIndex = updates.FindIndex(u => u.ProviderInstanceId == "fast-free" && u.State == ProviderStreamState.Completed);
        var slowFinalIndex = updates.FindIndex(u => u.ProviderInstanceId == "slow-llm" && u.State == ProviderStreamState.Completed);
        Assert.True(fastIndex >= 0 && slowFinalIndex >= 0);
        Assert.True(fastIndex < slowFinalIndex);

        var slowFinal = updates[slowFinalIndex];
        Assert.Equal("Part1-Part2", slowFinal.AccumulatedText);
        Assert.Equal(LanguageCode.ChineseSimplified, slowFinal.TargetLanguage);
    }

    [Fact]
    public async Task TranslateParallelAsync_IsolatesFailingProvider_SiblingStillCompletes()
    {
        var settings = AppSettings.CreateDefault();
        settings.Providers =
        [
            new ProviderInstanceConfig { Id = "broken", DisplayName = "Broken", Kind = ProviderKind.DeepL, Enabled = true, SortOrder = 0 },
            new ProviderInstanceConfig { Id = "healthy", DisplayName = "Healthy", Kind = ProviderKind.FreeTraditional, Enabled = true, SortOrder = 1 },
        ];
        await _settingsStore.SaveAsync(settings);

        var brokenProvider = new FakeTranslationProvider(
            ProviderKind.DeepL,
            supportsStreaming: false,
            streamFactory: (_, _, _) => throw new InvalidOperationException("Simulated unhandled provider crash"));

        var healthyProvider = new FakeTranslationProvider(
            ProviderKind.FreeTraditional,
            supportsStreaming: false,
            streamFactory: (_, _, ct) =>
            {
                async IAsyncEnumerable<TranslationChunk> Stream([EnumeratorCancellation] CancellationToken token)
                {
                    await Task.Yield();
                    yield return new TranslationChunk("你好世界", IsComplete: false);
                    yield return new TranslationChunk(string.Empty, IsComplete: true);
                }

                return Task.FromResult(Stream(ct));
            });

        var orchestrator = CreateOrchestrator(brokenProvider, healthyProvider);
        var batch = await orchestrator.TranslateAllAsync(
            new TranslationRequest("Hello-\nworld", LanguageCode.Auto, LanguageCode.Auto, ApplyTextPreprocessing: true));

        Assert.Equal("Helloworld", batch.PreprocessedText);
        Assert.Equal(LanguageCode.English, batch.EffectiveSourceLanguage);
        Assert.Equal(LanguageCode.ChineseSimplified, batch.EffectiveTargetLanguage);
        Assert.Equal(2, batch.Results.Count);

        var brokenResult = batch.Results.Single(r => r.ProviderInstanceId == "broken");
        Assert.False(brokenResult.IsSuccess);
        Assert.Contains("Simulated unhandled provider crash", brokenResult.ErrorMessage, StringComparison.Ordinal);

        var healthyResult = batch.Results.Single(r => r.ProviderInstanceId == "healthy");
        Assert.True(healthyResult.IsSuccess);
        Assert.Equal("你好世界", healthyResult.TranslatedText);
    }

    [Fact]
    public async Task TranslateParallelAsync_CancellationToken_CancelsInFlightStreamsPromptly()
    {
        var providerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var providerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var settings = AppSettings.CreateDefault();
        settings.Providers =
        [
            new ProviderInstanceConfig { Id = "hang", DisplayName = "Hanging LLM", Kind = ProviderKind.OpenAiCompatible, Enabled = true, SortOrder = 0 },
        ];
        await _settingsStore.SaveAsync(settings);

        var hangingProvider = new FakeTranslationProvider(
            ProviderKind.OpenAiCompatible,
            supportsStreaming: true,
            streamFactory: (_, _, ct) =>
            {
                async IAsyncEnumerable<TranslationChunk> Stream([EnumeratorCancellation] CancellationToken token)
                {
                    providerStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                    catch (OperationCanceledException)
                    {
                        providerCancelled.TrySetResult();
                        throw;
                    }

                    yield break;
                }

                return Task.FromResult(Stream(ct));
            });

        var orchestrator = CreateOrchestrator(hangingProvider);
        using var cts = new CancellationTokenSource();

        var consumerTask = Task.Run(async () =>
        {
            await foreach (var _ in orchestrator.TranslateParallelAsync(
                new TranslationRequest("Test cancellation", LanguageCode.English, LanguageCode.ChineseSimplified),
                cts.Token))
            {
            }
        });

        await providerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await providerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumerTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task TestProviderAsync_DispatchesToMatchingProviderKind()
    {
        var fake = new FakeTranslationProvider(
            ProviderKind.DeepL,
            supportsStreaming: false,
            streamFactory: (_, _, _) => throw new NotImplementedException(),
            healthCheck: (_, _) => Task.FromResult(new ProviderHealthCheckResult(true, "DeepL OK", TimeSpan.FromMilliseconds(42))));

        var orchestrator = CreateOrchestrator(fake);
        var res = await orchestrator.TestProviderAsync(new ProviderInstanceConfig { Kind = ProviderKind.DeepL });

        Assert.True(res.IsSuccess);
        Assert.Equal("DeepL OK", res.Message);
    }

    private TranslationOrchestrator CreateOrchestrator(params ITranslationProvider[] providers)
    {
        var detector = new LocalLanguageDetector();
        var router = new DualTargetLanguageRouter(detector);
        var preprocessor = new TextPreprocessor();
        return new TranslationOrchestrator(_settingsStore, preprocessor, router, providers);
    }

    private sealed class FakeTranslationProvider(
        ProviderKind kind,
        bool supportsStreaming,
        Func<TranslationRequest, ProviderInstanceConfig, CancellationToken, Task<IAsyncEnumerable<TranslationChunk>>> streamFactory,
        Func<ProviderInstanceConfig, CancellationToken, Task<ProviderHealthCheckResult>>? healthCheck = null) : ITranslationProvider
    {
        public ProviderKind Kind => kind;

        public bool SupportsStreaming => supportsStreaming;

        public async IAsyncEnumerable<TranslationChunk> TranslateStreamAsync(
            TranslationRequest request,
            ProviderInstanceConfig config,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var stream = await streamFactory(request, config, cancellationToken);
            await foreach (var chunk in stream.WithCancellation(cancellationToken))
            {
                yield return chunk;
            }
        }

        public Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            ProviderInstanceConfig config,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<ProviderHealthCheckResult> TestConnectionAsync(
            ProviderInstanceConfig config,
            CancellationToken cancellationToken = default) =>
            healthCheck is not null
                ? healthCheck(config, cancellationToken)
                : Task.FromResult(new ProviderHealthCheckResult(true, "OK", TimeSpan.Zero));
    }
}
