using System.Runtime.CompilerServices;
using PopTranslate.App.ViewModels;
using PopTranslate.Core.Configuration;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Diff;
using PopTranslate.Core.Language;
using PopTranslate.Core.Models;
using Xunit;

namespace PopTranslate.Tests.Integration;

public sealed class SmartGuardsAndResumeTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public SmartGuardsAndResumeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"PopTranslate_SmartGuards_{Guid.NewGuid():N}");
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

    // -----------------------------------------------------------------------
    // Item 5: SmartInputClassifier unit tests (positive & negative precision)
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("https://github.com/microsoft/microsoft-ui-xaml/issues/1234")]
    [InlineData("http://localhost:8080/api/v1/health")]
    [InlineData("www.example.com/docs/getting-started")]
    [InlineData("mailto:alice@example.com")]
    [InlineData("alice.bob+tag@company.co.uk")]
    public void SmartInputClassifier_DetectsUrlsAndEmails(string input)
    {
        var classifier = new SmartInputClassifier();
        var result = classifier.Classify(input);
        Assert.Equal(SmartInputKind.UrlOrEmail, result.Kind);
    }

    [Theory]
    [InlineData(@"C:\Users\WangTx\Documents\report.pdf")]
    [InlineData(@"D:/vibe_project/bob_win/src/PopTranslate.App/App.xaml.cs")]
    [InlineData(@"\\fileserver\shared\builds\PopTranslate.exe")]
    [InlineData("/usr/local/bin/dotnet")]
    [InlineData("~/config/poptranslate/settings.json")]
    public void SmartInputClassifier_DetectsFilePaths(string input)
    {
        var classifier = new SmartInputClassifier();
        var result = classifier.Classify(input);
        Assert.Equal(SmartInputKind.FilePath, result.Kind);
    }

    [Theory]
    [InlineData("{\"model\": \"gpt-4o-mini\", \"temperature\": 0.2, \"stream\": true}")]
    [InlineData("[{\"id\": 1, \"name\": \"alpha\"}, {\"id\": 2, \"name\": \"beta\"}]")]
    [InlineData("<Grid ColumnSpacing=\"8\"><TextBlock Text=\"Hello\" /></Grid>")]
    public void SmartInputClassifier_DetectsJsonAndMarkup(string input)
    {
        var classifier = new SmartInputClassifier();
        var result = classifier.Classify(input);
        Assert.Equal(SmartInputKind.JsonOrMarkup, result.Kind);
    }

    [Theory]
    [InlineData("System.NullReferenceException: Object reference not set to an instance of an object.\n   at PopTranslate.App.ViewModels.TranslationPopupViewModel.ExecuteUnifiedAsync() in F:\\vibe_project\\bob_win\\src\\TranslationPopupViewModel.cs:line 124")]
    [InlineData("Traceback (most recent call last):\n  File \"main.py\", line 42, in <module>\n    run_pipeline()\nValueError: invalid literal")]
    [InlineData("TypeError: Cannot read properties of undefined (reading 'map')\n    at renderList (app.tsx:88:14)\n    at App (app.tsx:21:5)")]
    public void SmartInputClassifier_DetectsStackTraces(string input)
    {
        var classifier = new SmartInputClassifier();
        var result = classifier.Classify(input);
        Assert.Equal(SmartInputKind.StackTrace, result.Kind);
    }

    [Theory]
    [InlineData("public async Task<string> FetchAsync(CancellationToken ct) {\n    return await _client.GetStringAsync(url, ct);\n}")]
    [InlineData("const items = users.filter(u => u.active && u.score >= 10);")]
    [InlineData("SELECT id, name FROM users WHERE active = 1 ORDER BY created_at DESC;")]
    [InlineData("dotnet build PopTranslate.sln -c Debug -p:Platform=x64")]
    [InlineData("git commit -m \"feat: add smart input guard\"")]
    public void SmartInputClassifier_DetectsCodeSnippetsAndCliCommands(string input)
    {
        var classifier = new SmartInputClassifier();
        var result = classifier.Classify(input);
        Assert.Equal(SmartInputKind.CodeSnippet, result.Kind);
    }

    [Theory]
    [InlineData("Generate HTML: Transforms the bookmarks dictionary into a structured page.")]
    [InlineData("getUserProfile")]
    [InlineData("snake_case_identifier")]
    [InlineData("NullReferenceException")]
    [InlineData("Visit https://example.com for the complete documentation.")]
    [InlineData("Please return the signed form before Friday.")]
    [InlineData("Select the preferred language from the dropdown menu.")]
    [InlineData("Read/write permissions are required for this folder.")]
    [InlineData("The latency must be <= 100ms in production.")]
    [InlineData("你好，请问这个功能如何开启？")]
    public void SmartInputClassifier_DoesNotMisclassifyProseOrSingleIdentifiers(string input)
    {
        var classifier = new SmartInputClassifier();
        var result = classifier.Classify(input);
        Assert.Equal(SmartInputKind.Prose, result.Kind);
    }

    // -----------------------------------------------------------------------
    // Item 1: Resume last session on tray / no-selection trigger (no new hotkey)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task TryResumeLastSession_RestoresPreviousCompletedOrInFlightSessionWithoutApiCalls_AndAddsNoNewHotkey()
    {
        // Confirm NO new global hotkey action was added for item 1.
        Assert.Equal(5, Enum.GetValues<HotkeyAction>().Length);

        var settings = CreateSettingsWithLlm();
        var store = new JsonSettingsStore(new DpapiSecretProtector(), _settingsPath);
        await store.SaveAsync(settings);

        var translateOrchestrator = new RecordingTranslationOrchestrator();
        var polishOrchestrator = new RecordingPolishOrchestrator();
        var vm = CreateViewModel(store, translateOrchestrator, polishOrchestrator);

        // Fresh state with no history -> cannot resume; caller should open empty input box.
        Assert.False(vm.TryResumeLastSession(prioritizePolish: false));

        // Translate once, then simulate losing focus (hide popup without cancel).
        await vm.TranslateTextAsync("Remember this translation");
        Assert.Equal(1, translateOrchestrator.CallCount);
        vm.SetPopupVisible(false);

        // User clicks tray icon or presses Alt+E without selecting text -> resumes instantly with 0 new API calls!
        vm.SetPopupVisible(true);
        Assert.True(vm.TryResumeLastSession(prioritizePolish: false));
        Assert.Equal(1, translateOrchestrator.CallCount);
        Assert.Equal("Remember this translation", vm.SourceText);
        Assert.Single(vm.ServiceCards);
        Assert.Contains("Remember this translation", vm.ServiceCards[0].TranslatedText, StringComparison.Ordinal);

        // After user explicitly clicks ClearSource, reopening should NOT force-restore cleared session
        // (empty input box still offers the manual "恢复上次" pill).
        vm.ClearSourceCommand.Execute(null);
        Assert.False(vm.TryResumeLastSession(prioritizePolish: false));
        Assert.True(vm.HasHistory);
        Assert.True(vm.ShowRestoreLastHistoryHint);

        // Verify AppCoordinator uses TryResumeLastSession in both tray/input open and empty selection hotkey paths.
        var repoRoot = FindRepoRoot();
        var coordinatorCs = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "src", "PopTranslate.App", "Services", "AppCoordinator.cs"));
        Assert.Contains("TryResumeLastSession(", coordinatorCs, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Item 2: Long-Text Token Guard
    // -----------------------------------------------------------------------

    [Fact]
    public async Task LongTextGuard_PausesOversizedTextUntilConfirmed_AndSkipsGuardWhenAlreadyCached()
    {
        var settings = CreateSettingsWithLlm();
        settings.EnableLongTextGuard = true;
        settings.LongTextGuardThreshold = 120;

        var store = new JsonSettingsStore(new DpapiSecretProtector(), _settingsPath);
        await store.SaveAsync(settings);

        var translateOrchestrator = new RecordingTranslationOrchestrator();
        var polishOrchestrator = new RecordingPolishOrchestrator();
        var vm = CreateViewModel(store, translateOrchestrator, polishOrchestrator);

        var longProse = string.Join(' ', Enumerable.Repeat("Natural language sentence for testing.", 6));
        Assert.True(longProse.Length > settings.LongTextGuardThreshold);

        // First trigger: guarded -> ZERO API calls!
        await vm.TranslateTextAsync(longProse);
        Assert.Equal(0, translateOrchestrator.CallCount);
        Assert.Equal(0, polishOrchestrator.CallCount);
        Assert.True(vm.HasPendingInputGuard);
        Assert.Equal(InputGuardReason.LongText, vm.PendingGuardReason);
        Assert.Empty(vm.ServiceCards);
        Assert.Equal(longProse, vm.SourceText);

        // User explicitly confirms -> executes immediately and clears guard!
        await vm.ConfirmPendingGuardCommand.ExecuteAsync(null);
        Assert.Equal(1, translateOrchestrator.CallCount);
        Assert.False(vm.HasPendingInputGuard);
        Assert.Equal(InputGuardReason.None, vm.PendingGuardReason);
        Assert.Single(vm.ServiceCards);

        // Re-triggering the EXACT same long text later hits cache with ZERO API calls and ZERO guard nag!
        await vm.TranslateTextAsync("Short normal text");
        Assert.Equal(2, translateOrchestrator.CallCount);

        await vm.TranslateTextAsync(longProse);
        Assert.Equal(2, translateOrchestrator.CallCount);
        Assert.False(vm.HasPendingInputGuard);
        Assert.Single(vm.ServiceCards);
    }

    // -----------------------------------------------------------------------
    // Item 5: Smart Non-Prose Guard & AI Explain integration in ViewModel
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SmartInputGuard_PausesUrlAndCode_AllowsForcedTranslateAndAiExplain_AndRespectsSettingToggle()
    {
        var settings = CreateSettingsWithLlm();
        settings.EnableSmartInputGuard = true;

        var store = new JsonSettingsStore(new DpapiSecretProtector(), _settingsPath);
        await store.SaveAsync(settings);

        var translateOrchestrator = new RecordingTranslationOrchestrator();
        var polishOrchestrator = new RecordingPolishOrchestrator();
        var vm = CreateViewModel(store, translateOrchestrator, polishOrchestrator);

        // 1. URL triggers guard with 0 API calls and hides AI Explain action.
        await vm.TranslateTextAsync("https://learn.microsoft.com/en-us/windows/apps/winui/");
        Assert.Equal(0, translateOrchestrator.CallCount);
        Assert.True(vm.HasPendingInputGuard);
        Assert.Equal(InputGuardReason.UrlOrEmail, vm.PendingGuardReason);
        Assert.False(vm.ShowGuardExplainAction);

        // Forced translation still works on click!
        await vm.ConfirmPendingGuardCommand.ExecuteAsync(null);
        Assert.Equal(1, translateOrchestrator.CallCount);
        Assert.False(vm.HasPendingInputGuard);

        // 2. Code snippet triggers guard with 0 API calls AND offers AI Explain!
        var snippet = "public async Task RunAsync(CancellationToken ct) {\n    await Task.Delay(100, ct);\n}";
        await vm.TranslateTextAsync(snippet);
        Assert.Equal(1, translateOrchestrator.CallCount);
        Assert.Equal(0, polishOrchestrator.CallCount);
        Assert.True(vm.HasPendingInputGuard);
        Assert.Equal(InputGuardReason.CodeSnippet, vm.PendingGuardReason);
        Assert.True(vm.ShowGuardExplainAction);
        // Preserves original newlines instead of collapsing code into one mangled line.
        Assert.Contains('\n', vm.SourceText);

        // Clicking "AI 解释" streams technical explanation via OpenAI-compatible provider with custom prompt!
        await vm.ExplainPendingGuardWithAiCommand.ExecuteAsync(null);
        Assert.False(vm.HasPendingInputGuard);
        Assert.Equal(1, translateOrchestrator.CallCount);
        Assert.Equal(1, polishOrchestrator.CallCount);
        Assert.NotNull(polishOrchestrator.LastRequest);
        Assert.Equal(PolishStyle.Custom, polishOrchestrator.LastRequest!.Style);
        Assert.False(string.IsNullOrWhiteSpace(polishOrchestrator.LastRequest.CustomPrompt));
        Assert.Single(vm.ServiceCards);
        Assert.Contains("explained:", vm.ServiceCards[0].TranslatedText, StringComparison.Ordinal);

        // 3. Disabling EnableSmartInputGuard allows URLs to translate immediately without pausing.
        var disabled = settings.Clone();
        disabled.EnableSmartInputGuard = false;
        await store.SaveAsync(disabled);
        vm.ApplySettings(store.Current);

        await vm.TranslateTextAsync("https://example.org/direct-translate");
        Assert.False(vm.HasPendingInputGuard);
        Assert.Equal(2, translateOrchestrator.CallCount);
    }

    [Fact]
    public async Task AppSettings_PersistsLongTextAndSmartInputGuardSettings_AndPopupHasGuardUi()
    {
        var store = new JsonSettingsStore(new DpapiSecretProtector(), _settingsPath);
        var defaults = AppSettings.CreateDefault();
        Assert.True(defaults.EnableLongTextGuard);
        Assert.Equal(1500, defaults.LongTextGuardThreshold);
        Assert.True(defaults.EnableSmartInputGuard);

        defaults.EnableLongTextGuard = false;
        defaults.LongTextGuardThreshold = 2400;
        defaults.EnableSmartInputGuard = false;
        await store.SaveAsync(defaults);

        var reloaded = await store.LoadAsync();
        Assert.False(reloaded.EnableLongTextGuard);
        Assert.Equal(2400, reloaded.LongTextGuardThreshold);
        Assert.False(reloaded.EnableSmartInputGuard);

        var repoRoot = FindRepoRoot();
        var popupXaml = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "src", "PopTranslate.App", "Windows", "TranslationPopupWindow.xaml"));
        Assert.Contains("x:Name=\"InputGuardCardBorder\"", popupXaml, StringComparison.Ordinal);
        Assert.Contains("ConfirmPendingGuardCommand", popupXaml, StringComparison.Ordinal);
        Assert.Contains("ExplainPendingGuardWithAiCommand", popupXaml, StringComparison.Ordinal);
    }

    private static AppSettings CreateSettingsWithLlm()
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
        return settings;
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

    private sealed class RecordingTranslationOrchestrator : ITranslationOrchestrator
    {
        public int CallCount { get; private set; }

        public async IAsyncEnumerable<ProviderStreamUpdate> TranslateParallelAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            await Task.Yield();
            var ids = request.ProviderInstanceIds is { Count: > 0 } ? request.ProviderInstanceIds : ["llm-primary"];
            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ProviderStreamUpdate(
                    id,
                    id,
                    ProviderKind.OpenAiCompatible,
                    ProviderStreamState.Completed,
                    $"translated:{request.Text}",
                    $"translated:{request.Text}",
                    LanguageCode.English,
                    request.TargetLanguage,
                    null,
                    TimeSpan.FromMilliseconds(35));
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
                "llm-primary", "CPA", ProviderKind.OpenAiCompatible, $"translated:{request.Text}",
                LanguageCode.English, request.TargetLanguage, true, null, TimeSpan.FromMilliseconds(10)));

        public Task<ProviderHealthCheckResult> TestProviderAsync(
            ProviderInstanceConfig providerConfig,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderHealthCheckResult(true, "OK", TimeSpan.FromMilliseconds(10)));
    }

    private sealed class RecordingPolishOrchestrator : IPolishOrchestrator
    {
        public int CallCount { get; private set; }
        public PolishRequest? LastRequest { get; private set; }

        public IReadOnlyList<ProviderInstanceConfig> GetEligibleProviders() => [];

        public async IAsyncEnumerable<ProviderStreamUpdate> PolishParallelAsync(
            PolishRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            await Task.Yield();
            var ids = request.ProviderInstanceIds is { Count: > 0 } ? request.ProviderInstanceIds : ["llm-primary"];
            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ProviderStreamUpdate(
                    id,
                    id,
                    ProviderKind.OpenAiCompatible,
                    ProviderStreamState.Completed,
                    $"explained:{request.Text}",
                    $"explained:{request.Text}",
                    LanguageCode.English,
                    LanguageCode.ChineseSimplified,
                    null,
                    TimeSpan.FromMilliseconds(45));
            }
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
            Task.FromResult(new ProviderHealthCheckResult(true, "OK", TimeSpan.Zero));
    }

    private sealed class NoOpTextWriteBackService : ITextWriteBackService
    {
        public Task<bool> ReplaceTextInWindowAsync(nint targetWindowHandle, string replacementText, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class NoOpTtsService : ITtsService
    {
        public bool IsSpeaking => false;
        public Task SpeakAsync(string text, LanguageCode? language = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Stop() { }
        public void Dispose() { }
    }
}
