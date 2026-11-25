using System.Runtime.CompilerServices;
using PopTranslate.App.ViewModels;
using PopTranslate.Core.Configuration;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Diff;
using PopTranslate.Core.Language;
using PopTranslate.Core.Models;
using Xunit;

namespace PopTranslate.Tests.Integration;

public sealed class PolishLayoutDeferredCollapseAndHistoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public PolishLayoutDeferredCollapseAndHistoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"PopTranslate_UxPolish_{Guid.NewGuid():N}");
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
    public async Task TranslationResultCardControl_PolishHeaderUsesResponsiveGridAndHidesDisabledDiffWhileLoading()
    {
        var repoRoot = FindRepoRoot();
        var xaml = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "src", "PopTranslate.App", "Controls", "TranslationResultCardControl.xaml"));

        // Polish header left must use a responsive Grid (with Width="*" on DisplayName) instead of an overflowing horizontal StackPanel.
        Assert.Contains("x:Name=\"PolishHeaderLeftGrid\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TranslateHeaderLeftGrid\"", xaml, StringComparison.Ordinal);

        // Diff button should only occupy header width once polish text exists (CanToggleDiff), never crowding "润色中...".
        Assert.Contains("Visibility=\"{x:Bind ViewModel.CanToggleDiff, Mode=OneWay, Converter={StaticResource BoolToVisibilityConverter}}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CollapsedByDefaultProviders_DoNotTriggerRequestsUntilUserExpandsCard()
    {
        var settings = AppSettings.CreateDefault();
        settings.Providers =
        [
            new ProviderInstanceConfig
            {
                Id = "expanded-llm",
                DisplayName = "CPA",
                Kind = ProviderKind.OpenAiCompatible,
                Role = ProviderRole.Translate,
                Enabled = true,
                SortOrder = 0,
                IsCollapsedByDefault = false,
                BaseUrl = "https://example.invalid/v1",
                Model = "gpt-4o-mini",
            },
            new ProviderInstanceConfig
            {
                Id = "collapsed-free",
                DisplayName = "Free Translate",
                Kind = ProviderKind.FreeTraditional,
                Role = ProviderRole.Translate,
                Enabled = true,
                SortOrder = 1,
                IsCollapsedByDefault = true,
            },
            new ProviderInstanceConfig
            {
                Id = "collapsed-polish",
                DisplayName = "AI 文本润色",
                Kind = ProviderKind.OpenAiCompatible,
                Role = ProviderRole.Polish,
                TriggerMode = PolishTriggerMode.AutoParallel,
                Enabled = true,
                SortOrder = 2,
                IsCollapsedByDefault = true,
                BaseUrl = "https://example.invalid/v1",
                Model = "gpt-4o-mini",
            },
        ];

        var store = new JsonSettingsStore(new DpapiSecretProtector(), _settingsPath);
        await store.SaveAsync(settings);

        var translateOrchestrator = new RecordingTranslationOrchestrator();
        var polishOrchestrator = new RecordingPolishOrchestrator();
        var vm = CreateViewModel(store, translateOrchestrator, polishOrchestrator);

        await vm.TranslateTextAsync("Hello deferred world");

        // Only the non-collapsed provider should have executed!
        Assert.Equal(["expanded-llm"], translateOrchestrator.RequestedProviderIds);
        Assert.Empty(polishOrchestrator.RequestedProviderIds);

        var expandedCard = Assert.Single(vm.ServiceCards, c => c.ProviderInstanceId == "expanded-llm");
        var collapsedTranslateCard = Assert.Single(vm.ServiceCards, c => c.ProviderInstanceId == "collapsed-free");
        var collapsedPolishCard = Assert.Single(vm.ServiceCards, c => c.ProviderInstanceId == "collapsed-polish");

        Assert.Equal(ProviderStreamState.Completed, expandedCard.State);
        Assert.False(expandedCard.IsCollapsed);
        Assert.False(expandedCard.IsDeferredUntilExpanded);

        Assert.True(collapsedTranslateCard.IsCollapsed);
        Assert.True(collapsedTranslateCard.IsDeferredUntilExpanded);
        Assert.False(collapsedTranslateCard.IsLoading);
        Assert.Equal(string.Empty, collapsedTranslateCard.TranslatedText);

        Assert.True(collapsedPolishCard.IsCollapsed);
        Assert.True(collapsedPolishCard.IsDeferredUntilExpanded);
        Assert.False(collapsedPolishCard.IsLoading);
        Assert.Equal(string.Empty, collapsedPolishCard.TranslatedText);

        // Expanding the collapsed translate card triggers ONLY that provider now!
        collapsedTranslateCard.ToggleCollapseCommand.Execute(null);
        await WaitUntilAsync(() => collapsedTranslateCard.State == ProviderStreamState.Completed);

        Assert.False(collapsedTranslateCard.IsCollapsed);
        Assert.False(collapsedTranslateCard.IsDeferredUntilExpanded);
        Assert.Equal(["expanded-llm", "collapsed-free"], translateOrchestrator.RequestedProviderIds);
        Assert.Contains("collapsed-free:", collapsedTranslateCard.TranslatedText, StringComparison.Ordinal);

        // Collapsing and re-expanding again must NOT re-trigger the API.
        collapsedTranslateCard.ToggleCollapseCommand.Execute(null);
        collapsedTranslateCard.ToggleCollapseCommand.Execute(null);
        await Task.Delay(50);
        Assert.Equal(["expanded-llm", "collapsed-free"], translateOrchestrator.RequestedProviderIds);

        // Expanding the collapsed polish card triggers ONLY that polish provider now!
        collapsedPolishCard.ToggleCollapseCommand.Execute(null);
        await WaitUntilAsync(() => collapsedPolishCard.State == ProviderStreamState.Completed);

        Assert.False(collapsedPolishCard.IsCollapsed);
        Assert.False(collapsedPolishCard.IsDeferredUntilExpanded);
        Assert.Equal(["collapsed-polish"], polishOrchestrator.RequestedProviderIds);
        Assert.Contains("polished:", collapsedPolishCard.TranslatedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HistoryCache_ReusesPreviousResultWithoutReuploadingAfterLosingFocus_AndSupportsHistoryRestore()
    {
        var settings = AppSettings.CreateDefault();
        settings.Providers =
        [
            new ProviderInstanceConfig
            {
                Id = "llm-primary",
                DisplayName = "CPA",
                Kind = ProviderKind.OpenAiCompatible,
                Role = ProviderRole.Translate,
                Enabled = true,
                SortOrder = 0,
                IsCollapsedByDefault = false,
                BaseUrl = "https://example.invalid/v1",
                Model = "gpt-4o-mini",
            },
        ];

        var store = new JsonSettingsStore(new DpapiSecretProtector(), _settingsPath);
        await store.SaveAsync(settings);

        var translateOrchestrator = new RecordingTranslationOrchestrator();
        var polishOrchestrator = new RecordingPolishOrchestrator();
        var vm = CreateViewModel(store, translateOrchestrator, polishOrchestrator);

        // 1. First translation calls the API once and records history.
        await vm.TranslateTextAsync("Generate HTML from bookmarks dictionary");
        Assert.Equal(1, translateOrchestrator.CallCount);
        Assert.True(vm.HasHistory);
        Assert.Single(vm.HistoryEntries);

        var firstResult = Assert.Single(vm.ServiceCards).TranslatedText;

        // 2. Simulate accidental focus loss (popup hides) followed by translating the EXACT same text again:
        //    must serve directly from cache with ZERO new API calls!
        vm.SetPopupVisible(false);
        vm.SetPopupVisible(true);
        await vm.TranslateTextAsync("Generate HTML from bookmarks dictionary");

        Assert.Equal(1, translateOrchestrator.CallCount);
        var cachedCard = Assert.Single(vm.ServiceCards);
        Assert.Equal(ProviderStreamState.Completed, cachedCard.State);
        Assert.Equal(firstResult, cachedCard.TranslatedText);

        // 3. Translating a second distinct sentence calls API once and adds a 2nd history entry (newest first).
        await vm.TranslateTextAsync("Second distinct sentence");
        Assert.Equal(2, translateOrchestrator.CallCount);
        Assert.Equal(2, vm.HistoryEntries.Count);

        // 4. Restoring the first sentence from history returns cached result with ZERO new API calls!
        var firstHistoryId = vm.HistoryEntries[1].Id;
        vm.RestoreHistoryEntry(firstHistoryId);

        Assert.Equal(2, translateOrchestrator.CallCount);
        Assert.Equal("Generate HTML from bookmarks dictionary", vm.SourceText);
        Assert.Equal(firstResult, Assert.Single(vm.ServiceCards).TranslatedText);

        // 5. Explicit Retry on the card DOES bypass cache and calls the API again.
        Assert.Single(vm.ServiceCards).RetryCommand.Execute(null);
        await WaitUntilAsync(() => translateOrchestrator.CallCount == 3);
        Assert.Equal(3, translateOrchestrator.CallCount);

        // 6. Verify TranslationPopupWindow preserves in-flight translation on click-outside (cancelTranslations: false).
        var repoRoot = FindRepoRoot();
        var popupCs = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "src", "PopTranslate.App", "Windows", "TranslationPopupWindow.xaml.cs"));
        Assert.Contains("HidePopup(cancelTranslations: false);", popupCs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActiveInFlightTranslation_ReattachesWithoutRestartingWhenSameTextRetriggeredAfterAccidentalHide()
    {
        var settings = AppSettings.CreateDefault();
        settings.Providers =
        [
            new ProviderInstanceConfig
            {
                Id = "slow-llm",
                DisplayName = "CPA",
                Kind = ProviderKind.OpenAiCompatible,
                Role = ProviderRole.Translate,
                Enabled = true,
                SortOrder = 0,
                IsCollapsedByDefault = false,
                BaseUrl = "https://example.invalid/v1",
                Model = "gpt-4o-mini",
            },
        ];

        var store = new JsonSettingsStore(new DpapiSecretProtector(), _settingsPath);
        await store.SaveAsync(settings);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var translateOrchestrator = new RecordingTranslationOrchestrator(gate.Task);
        var polishOrchestrator = new RecordingPolishOrchestrator();
        var vm = CreateViewModel(store, translateOrchestrator, polishOrchestrator);

        // Start a slow translation, then simulate accidental click-outside (hide without cancel)
        var firstRun = vm.TranslateTextAsync("Slow streaming sentence");
        await WaitUntilAsync(() => translateOrchestrator.CallCount == 1);

        vm.SetPopupVisible(false);

        // User re-triggers translation for the exact same text while the first stream is still running!
        vm.SetPopupVisible(true);
        var secondRun = vm.TranslateTextAsync("Slow streaming sentence");

        // Must NOT have started a second API request!
        Assert.Equal(1, translateOrchestrator.CallCount);

        // Release stream and verify both complete cleanly with exact text.
        gate.SetResult();
        await Task.WhenAll(firstRun, secondRun);

        Assert.Equal(1, translateOrchestrator.CallCount);
        var card = Assert.Single(vm.ServiceCards);
        Assert.Equal(ProviderStreamState.Completed, card.State);
        Assert.Contains("Slow streaming sentence", card.TranslatedText, StringComparison.Ordinal);
    }

    private static TranslationPopupViewModel CreateViewModel(
        ISettingsStore store,
        ITranslationOrchestrator translationOrchestrator,
        IPolishOrchestrator polishOrchestrator)
    {
        var detector = new LocalLanguageDetector();
        var vm = new TranslationPopupViewModel(
            store,
            new TextPreprocessor(),
            detector,
            new DualTargetLanguageRouter(detector),
            translationOrchestrator,
            new NoOpBackTranslationService(),
            polishOrchestrator,
            new LocalTextDiffService(),
            new NoOpOcrOrchestrator(),
            new NoOpTextWriteBackService(),
            new NoOpTtsService());
        vm.ApplySettings(store.Current, applySessionDefaults: true);
        return vm;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not satisfied before timeout.");
            }

            await Task.Delay(15);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "PopTranslate.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return dir!;
    }

    private sealed class RecordingTranslationOrchestrator(Task? holdGate = null) : ITranslationOrchestrator
    {
        public int CallCount { get; private set; }
        public List<string> RequestedProviderIds { get; } = [];

        public async IAsyncEnumerable<ProviderStreamUpdate> TranslateParallelAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            var ids = request.ProviderInstanceIds ?? [];
            RequestedProviderIds.AddRange(ids);

            foreach (var id in ids)
            {
                yield return new ProviderStreamUpdate(
                    id, id, ProviderKind.OpenAiCompatible, ProviderStreamState.Pending,
                    string.Empty, string.Empty, LanguageCode.English, request.TargetLanguage, null, TimeSpan.Zero);
            }

            if (holdGate is not null)
            {
                await holdGate.WaitAsync(cancellationToken);
            }

            foreach (var id in ids)
            {
                var translated = $"{id}: {request.Text}";
                yield return new ProviderStreamUpdate(
                    id, id, ProviderKind.OpenAiCompatible, ProviderStreamState.Completed,
                    translated, translated, LanguageCode.English, request.TargetLanguage, null, TimeSpan.FromMilliseconds(42));
            }
        }

        public Task<TranslationBatchResult> TranslateAllAsync(
            TranslationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationBatchResult(
                request.Text, LanguageCode.English, LanguageCode.ChineseSimplified, []));

        public Task<TranslationResult> TranslateFirstAvailableAsync(
            TranslationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationResult(
                "mock", "mock", ProviderKind.OpenAiCompatible, request.Text,
                LanguageCode.English, request.TargetLanguage, true, null, TimeSpan.Zero));

        public Task<ProviderHealthCheckResult> TestProviderAsync(
            ProviderInstanceConfig config,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderHealthCheckResult(true, "ok", TimeSpan.Zero));
    }

    private sealed class RecordingPolishOrchestrator : IPolishOrchestrator
    {
        public int CallCount { get; private set; }
        public List<string> RequestedProviderIds { get; } = [];

        public IReadOnlyList<ProviderInstanceConfig> GetEligibleProviders() => [];

        public async IAsyncEnumerable<ProviderStreamUpdate> PolishParallelAsync(
            PolishRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            var ids = request.ProviderInstanceIds ?? [];
            RequestedProviderIds.AddRange(ids);

            foreach (var id in ids)
            {
                var polished = $"polished: {request.Text}";
                yield return new ProviderStreamUpdate(
                    id, id, ProviderKind.OpenAiCompatible, ProviderStreamState.Completed,
                    polished, polished, LanguageCode.English, LanguageCode.English, null, TimeSpan.FromMilliseconds(55));
            }

            await Task.CompletedTask;
        }

        public Task<IReadOnlyList<TranslationResult>> PolishAllAsync(
            PolishRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TranslationResult>>([]);
    }

    private sealed class NoOpBackTranslationService : IBackTranslationService
    {
        public async IAsyncEnumerable<ProviderStreamUpdate> BackTranslateStreamAsync(
            BackTranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<TranslationResult> BackTranslateAsync(
            BackTranslationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationResult(
                request.ProviderInstanceId, request.ProviderInstanceId, ProviderKind.OpenAiCompatible,
                request.TranslatedText, request.TranslatedLanguage, request.BackTargetLanguage, true, null, TimeSpan.Zero));
    }

    private sealed class NoOpOcrOrchestrator : IOcrOrchestrator
    {
        public Task<IReadOnlyList<OcrLanguagePackInfo>> GetInstalledWindowsOcrLanguagesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OcrLanguagePackInfo>>([]);
        public Task<OcrResult> RecognizeAsync(OcrImage image, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OcrResult(false, string.Empty, null, OcrProviderKind.WindowsNative, "noop", TimeSpan.Zero));
        public Task<ProviderHealthCheckResult> TestActiveProviderAsync(OcrConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderHealthCheckResult(true, "ok", TimeSpan.Zero));
    }

    private sealed class NoOpTextWriteBackService : ITextWriteBackService
    {
        public Task<bool> ReplaceTextInWindowAsync(nint targetWindowHandle, string replacementText, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class NoOpTtsService : ITtsService
    {
        public bool IsSpeaking => false;
        public Task SpeakAsync(string text, LanguageCode? language = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public void Stop() { }
        public void Dispose() { }
    }
}
