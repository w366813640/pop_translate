using System.Runtime.CompilerServices;
using PopTranslate.Core.Configuration;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Language;
using PopTranslate.Core.Models;
using PopTranslate.Core.Polish;
using Xunit;

namespace PopTranslate.Tests.Core;

public sealed class PolishOrchestratorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonSettingsStore _settingsStore;

    public PolishOrchestratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PopTranslatePolishOrchTests_" + Guid.NewGuid().ToString("N"));
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
    public async Task PolishParallelAsync_SkipsNonLlmProvidersAndRunsEligibleLlmsConcurrently()
    {
        var slowGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastCompletedBeforeSlowReleased = false;
        var invokedInstanceIds = new List<string>();
        var gateLock = new object();

        var settings = AppSettings.CreateDefault();
        settings.Providers =
        [
            new ProviderInstanceConfig { Id = "free-1", DisplayName = "Free", Kind = ProviderKind.FreeTraditional, Enabled = true, SortOrder = 0 },
            new ProviderInstanceConfig { Id = "deepl-1", DisplayName = "DeepL", Kind = ProviderKind.DeepL, Enabled = true, SortOrder = 1 },
            new ProviderInstanceConfig { Id = "llm-slow", DisplayName = "LLM Slow", Kind = ProviderKind.OpenAiCompatible, Enabled = true, SortOrder = 2 },
            new ProviderInstanceConfig { Id = "llm-disabled", DisplayName = "LLM Off", Kind = ProviderKind.OpenAiCompatible, Enabled = false, SortOrder = 3 },
            new ProviderInstanceConfig { Id = "llm-fast", DisplayName = "LLM Fast", Kind = ProviderKind.OpenAiCompatible, Enabled = true, SortOrder = 4 },
        ];
        await _settingsStore.SaveAsync(settings);

        var llmProvider = new FakePolishProvider(
            ProviderKind.OpenAiCompatible,
            (req, cfg, ct) =>
            {
                lock (gateLock) invokedInstanceIds.Add(cfg.Id);
                if (cfg.Id == "llm-slow")
                {
                    return StreamGated(["Polished-", "Slow"], slowGate, ct);
                }

                return StreamImmediate(["Polished-Fast"], ct);
            });

        var orchestrator = CreateOrchestrator(llmProvider);

        var eligible = orchestrator.GetEligibleProviders();
        Assert.Equal(["llm-slow", "llm-fast"], eligible.Select(p => p.Id).ToList());

        var updates = new List<ProviderStreamUpdate>();
        await foreach (var update in orchestrator.PolishParallelAsync(new PolishRequest("Hello world", PolishStyle.Business)))
        {
            updates.Add(update);
            if (update.ProviderInstanceId == "llm-fast" && update.State == ProviderStreamState.Completed)
            {
                if (!slowGate.Task.IsCompleted)
                {
                    fastCompletedBeforeSlowReleased = true;
                    slowGate.TrySetResult();
                }
            }
        }

        Assert.True(fastCompletedBeforeSlowReleased, "Fast LLM must complete concurrently while slow LLM is blocked.");
        Assert.DoesNotContain("free-1", invokedInstanceIds);
        Assert.DoesNotContain("deepl-1", invokedInstanceIds);
        Assert.DoesNotContain("llm-disabled", invokedInstanceIds);

        var fastCompleteIdx = updates.FindIndex(u => u.ProviderInstanceId == "llm-fast" && u.State == ProviderStreamState.Completed);
        var slowCompleteIdx = updates.FindIndex(u => u.ProviderInstanceId == "llm-slow" && u.State == ProviderStreamState.Completed);
        Assert.True(fastCompleteIdx >= 0 && slowCompleteIdx >= 0 && fastCompleteIdx < slowCompleteIdx);
        Assert.Equal("Polished-Slow", updates[slowCompleteIdx].AccumulatedText);
        Assert.Equal(LanguageCode.English, updates[slowCompleteIdx].DetectedSourceLanguage);
        Assert.Equal(LanguageCode.English, updates[slowCompleteIdx].TargetLanguage);
    }

    [Fact]
    public async Task PolishParallelAsync_WhenNoEligibleLlmProvidersEnabled_ReturnsEmptyImmediately()
    {
        var settings = AppSettings.CreateDefault();
        settings.Providers =
        [
            new ProviderInstanceConfig { Id = "free-1", DisplayName = "Free", Kind = ProviderKind.FreeTraditional, Enabled = true, SortOrder = 0 },
            new ProviderInstanceConfig { Id = "deepl-1", DisplayName = "DeepL", Kind = ProviderKind.DeepL, Enabled = true, SortOrder = 1 },
            new ProviderInstanceConfig { Id = "llm-off", DisplayName = "LLM Off", Kind = ProviderKind.OpenAiCompatible, Enabled = false, SortOrder = 2 },
        ];
        await _settingsStore.SaveAsync(settings);

        var orchestrator = CreateOrchestrator(new FakePolishProvider(ProviderKind.OpenAiCompatible, (_, _, _) => throw new InvalidOperationException("Should not run")));

        Assert.Empty(orchestrator.GetEligibleProviders());

        var updates = new List<ProviderStreamUpdate>();
        await foreach (var update in orchestrator.PolishParallelAsync(new PolishRequest("Test")))
        {
            updates.Add(update);
        }

        Assert.Empty(updates);

        var results = await orchestrator.PolishAllAsync(new PolishRequest("Test"));
        Assert.Empty(results);
    }

    [Fact]
    public async Task PolishAllAsync_IsolatesFailingLlmProvider_AndAppliesTextPreprocessing()
    {
        var settings = AppSettings.CreateDefault();
        settings.MergePdfLineBreaks = true;
        settings.Providers =
        [
            new ProviderInstanceConfig { Id = "llm-broken", DisplayName = "Broken LLM", Kind = ProviderKind.OpenAiCompatible, Enabled = true, SortOrder = 0 },
            new ProviderInstanceConfig { Id = "llm-ok", DisplayName = "Healthy LLM", Kind = ProviderKind.OpenAiCompatible, Enabled = true, SortOrder = 1 },
        ];
        await _settingsStore.SaveAsync(settings);

        string? receivedText = null;
        var llmProvider = new FakePolishProvider(
            ProviderKind.OpenAiCompatible,
            (req, cfg, ct) =>
            {
                if (cfg.Id == "llm-broken")
                {
                    throw new InvalidOperationException("Simulated LLM crash");
                }

                receivedText = req.Text;
                return StreamImmediate(["Clean polished sentence."], ct);
            });

        var orchestrator = CreateOrchestrator(llmProvider);
        var results = await orchestrator.PolishAllAsync(new PolishRequest(
            Text: "Auto-\nmatic polish",
            Style: PolishStyle.General,
            ApplyTextPreprocessing: true));

        Assert.Equal("Automatic polish", receivedText);
        Assert.Equal(2, results.Count);
        Assert.Equal("llm-broken", results[0].ProviderInstanceId);
        Assert.False(results[0].IsSuccess);
        Assert.Contains("Simulated LLM crash", results[0].ErrorMessage ?? string.Empty, StringComparison.Ordinal);

        Assert.Equal("llm-ok", results[1].ProviderInstanceId);
        Assert.True(results[1].IsSuccess, results[1].ErrorMessage);
        Assert.Equal("Clean polished sentence.", results[1].TranslatedText);
    }

    [Fact]
    public async Task PolishAllAsync_CustomStyle_FallsBackToSettingsCustomPromptWhenRequestPromptIsNull()
    {
        var settings = AppSettings.CreateDefault();
        settings.Polish.CustomPrompt = "Polish according to settings custom instruction.";
        settings.Providers =
        [
            new ProviderInstanceConfig { Id = "llm-1", DisplayName = "LLM 1", Kind = ProviderKind.OpenAiCompatible, Enabled = true, SortOrder = 0 },
        ];
        await _settingsStore.SaveAsync(settings);

        PolishRequest? capturedFromFallback = null;
        PolishRequest? capturedExplicit = null;
        var callCount = 0;

        var llmProvider = new FakePolishProvider(
            ProviderKind.OpenAiCompatible,
            (req, _, ct) =>
            {
                if (++callCount == 1) capturedFromFallback = req;
                else capturedExplicit = req;
                return StreamImmediate(["Done"], ct);
            });

        var orchestrator = CreateOrchestrator(llmProvider);

        await orchestrator.PolishAllAsync(new PolishRequest("Hello", PolishStyle.Custom, CustomPrompt: null));
        await orchestrator.PolishAllAsync(new PolishRequest("Hello", PolishStyle.Custom, CustomPrompt: "Explicit override prompt."));

        Assert.NotNull(capturedFromFallback);
        Assert.Equal("Polish according to settings custom instruction.", capturedFromFallback!.CustomPrompt);

        Assert.NotNull(capturedExplicit);
        Assert.Equal("Explicit override prompt.", capturedExplicit!.CustomPrompt);
    }

    [Fact]
    public async Task PolishParallelAsync_SingleProviderRetryAndCancellation_BehavesCleanly()
    {
        var providerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var providerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var settings = AppSettings.CreateDefault();
        settings.Providers =
        [
            new ProviderInstanceConfig { Id = "free-1", DisplayName = "Free", Kind = ProviderKind.FreeTraditional, Enabled = true, SortOrder = 0 },
            new ProviderInstanceConfig { Id = "llm-1", DisplayName = "LLM 1", Kind = ProviderKind.OpenAiCompatible, Enabled = true, SortOrder = 1 },
            new ProviderInstanceConfig { Id = "llm-2", DisplayName = "LLM 2", Kind = ProviderKind.OpenAiCompatible, Enabled = true, SortOrder = 2 },
        ];
        await _settingsStore.SaveAsync(settings);

        var executedIds = new List<string>();
        var llmProvider = new FakePolishProvider(
            ProviderKind.OpenAiCompatible,
            (req, cfg, ct) =>
            {
                executedIds.Add(cfg.Id);
                if (req.Text == "hang")
                {
                    return StreamHang(providerStarted, providerCancelled, ct);
                }

                return StreamImmediate([$"Polished by {cfg.Id}"], ct);
            });

        var orchestrator = CreateOrchestrator(llmProvider);

        // Single-provider retry targeting llm-2 + ineligible free-1 -> ONLY llm-2 runs!
        var retryResults = await orchestrator.PolishAllAsync(new PolishRequest(
            Text: "Retry target",
            Style: PolishStyle.Academic,
            ProviderInstanceIds: ["free-1", "llm-2"]));

        var single = Assert.Single(retryResults);
        Assert.Equal("llm-2", single.ProviderInstanceId);
        Assert.Equal("Polished by llm-2", single.TranslatedText);
        Assert.Equal(["llm-2"], executedIds);

        // Cancellation propagates to active provider immediately
        using var cts = new CancellationTokenSource();
        var consumerTask = Task.Run(async () =>
        {
            await foreach (var _ in orchestrator.PolishParallelAsync(
                new PolishRequest("hang", PolishStyle.General, ProviderInstanceIds: ["llm-1"]),
                cts.Token))
            {
            }
        });

        await providerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await providerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumerTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private PolishOrchestrator CreateOrchestrator(params IPolishProvider[] providers)
    {
        var detector = new LocalLanguageDetector();
        var preprocessor = new TextPreprocessor();
        return new PolishOrchestrator(_settingsStore, preprocessor, detector, providers);
    }

    private static async IAsyncEnumerable<TranslationChunk> StreamImmediate(
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

    private static async IAsyncEnumerable<TranslationChunk> StreamGated(
        string[] deltas,
        TaskCompletionSource gate,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        yield return new TranslationChunk(deltas[0], IsComplete: false);
        await gate.Task.WaitAsync(ct);
        for (var i = 1; i < deltas.Length; i++)
        {
            yield return new TranslationChunk(deltas[i], IsComplete: false);
        }

        yield return new TranslationChunk(string.Empty, IsComplete: true);
    }

    private static async IAsyncEnumerable<TranslationChunk> StreamHang(
        TaskCompletionSource started,
        TaskCompletionSource cancelled,
        [EnumeratorCancellation] CancellationToken ct)
    {
        started.TrySetResult();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        catch (OperationCanceledException)
        {
            cancelled.TrySetResult();
            throw;
        }

        yield break;
    }

    private sealed class FakePolishProvider(
        ProviderKind kind,
        Func<PolishRequest, ProviderInstanceConfig, CancellationToken, IAsyncEnumerable<TranslationChunk>> streamFactory) : IPolishProvider
    {
        public ProviderKind Kind => kind;

        public bool SupportsStreaming => true;

        public IAsyncEnumerable<TranslationChunk> PolishStreamAsync(
            PolishRequest request,
            ProviderInstanceConfig config,
            CancellationToken cancellationToken = default) =>
            streamFactory(request, config, cancellationToken);

        public Task<TranslationResult> PolishAsync(
            PolishRequest request,
            ProviderInstanceConfig config,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
