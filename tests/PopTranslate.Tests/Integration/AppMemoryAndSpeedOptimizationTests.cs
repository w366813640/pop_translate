using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using PopTranslate.App.Services;
using PopTranslate.App.ViewModels;
using PopTranslate.App.Windows;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.DependencyInjection;
using PopTranslate.Core.Diff;
using PopTranslate.Core.Language;
using PopTranslate.Core.Models;
using PopTranslate.Core.Translation;
using PopTranslate.Win32.DependencyInjection;
using Xunit;

namespace PopTranslate.Tests.Integration;

public sealed class AppMemoryAndSpeedOptimizationTests
{
    [Fact]
    public void PopTranslateAppCsproj_ConfiguresLowFootprintDesktopGarbageCollection()
    {
        var repoRoot = FindRepoRoot();
        var csprojPath = Path.Combine(repoRoot, "src", "PopTranslate.App", "PopTranslate.App.csproj");
        var doc = XDocument.Load(csprojPath);
        var mainPropertyGroup = doc.Root?.Elements("PropertyGroup").FirstOrDefault();

        Assert.NotNull(mainPropertyGroup);
        Assert.Equal("false", mainPropertyGroup!.Element("ServerGarbageCollection")?.Value?.Trim());

        var conserveValue = mainPropertyGroup.Element("GCConserveMemory")?.Value?.Trim();
        Assert.NotNull(conserveValue);
        Assert.True(int.TryParse(conserveValue, out var level) && level is >= 1 and <= 9,
            $"Expected GCConserveMemory to be an integer in 1..9, got '{conserveValue}'.");

        var conserveHostOption = doc.Root?
            .Elements("ItemGroup")
            .Elements("RuntimeHostConfigurationOption")
            .FirstOrDefault(e => string.Equals(e.Attribute("Include")?.Value, "System.GC.ConserveMemory", StringComparison.Ordinal));
        Assert.NotNull(conserveHostOption);
        var hostOptionValue = conserveHostOption!.Attribute("Value")?.Value?.Trim();
        Assert.True(
            string.Equals(hostOptionValue, "$(GCConserveMemory)", StringComparison.Ordinal) ||
            string.Equals(hostOptionValue, conserveValue, StringComparison.Ordinal),
            $"Expected RuntimeHostConfigurationOption Value to reference '$(GCConserveMemory)' or '{conserveValue}', got '{hostOptionValue}'.");
    }

    [Fact]
    public void AppServiceCollectionExtensions_RegistersSettingsWindowTransientAndPopupSingleton()
    {
        var services = new ServiceCollection();
        services.AddPopTranslateCore();
        services.AddPopTranslateWin32();
        services.AddPopTranslateApp();

        var settingsDescriptor = Assert.Single(services, d => d.ServiceType == typeof(SettingsWindow));
        Assert.Equal(ServiceLifetime.Transient, settingsDescriptor.Lifetime);

        var popupDescriptor = Assert.Single(services, d => d.ServiceType == typeof(TranslationPopupWindow));
        Assert.Equal(ServiceLifetime.Singleton, popupDescriptor.Lifetime);

        var hoverDescriptor = Assert.Single(services, d => d.ServiceType == typeof(FloatingHoverIconWindow));
        Assert.Equal(ServiceLifetime.Singleton, hoverDescriptor.Lifetime);
    }

    [Fact]
    public async Task TranslationPopupViewModel_ReleaseOcrRawPixelsAndReleaseIdleMemory_FreesBuffersCleanly()
    {
        var settings = CreateSettingsWithProviders(
            new ProviderInstanceConfig
            {
                Id = "p-translate",
                DisplayName = "Test Translate",
                Kind = ProviderKind.OpenAiCompatible,
                Role = ProviderRole.Translate,
                Enabled = true,
                SortOrder = 0,
            });
        var ocrStub = new CapturingOcrOrchestrator();
        var vm = CreateViewModel(
            settings,
            translationOrchestrator: new BurstTranslationOrchestrator(_ => []),
            polishOrchestrator: new BurstPolishOrchestrator(_ => []),
            ocrOrchestrator: ocrStub);

        var pngBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");
        var rawBgraBytes = new byte[128];
        Array.Fill(rawBgraBytes, (byte)0xAB);

        var screenshot = new CapturedScreenshot(
            PngBytes: pngBytes,
            CaptureBounds: new ScreenRect(10, 20, 100, 60),
            DpiScale: 1.0,
            PixelWidth: 100,
            PixelHeight: 60,
            RawBgraBytes: rawBgraBytes);

        vm.BeginOcrCapture(screenshot);
        Assert.True(vm.HasOcrContext);

        // 1. ReleaseOcrRawPixels clears RawBgraBytes while preserving PngBytes & HasOcrContext for re-OCR.
        vm.ReleaseOcrRawPixels();
        Assert.True(vm.HasOcrContext);

        await vm.RerunOcrAsync();
        Assert.Equal(1, ocrStub.CallCount);
        Assert.NotNull(ocrStub.LastImage);
        Assert.Null(ocrStub.LastImage!.RawBgraBytes);
        Assert.Same(pngBytes, ocrStub.LastImage.ImageBytes);

        // 2. ReleaseIdleMemory clears OCR screenshot reference, preview state, and HasOcrContext.
        vm.IsOcrPreviewExpanded = true;
        vm.ReleaseIdleMemory();

        Assert.False(vm.HasOcrContext);
        Assert.False(vm.IsOcrPreviewExpanded);
        Assert.Null(vm.OcrPreviewImage);

        // Re-OCR is a no-op after idle memory release because _lastOcrScreenshot was cleared.
        await vm.RerunOcrAsync();
        Assert.Equal(1, ocrStub.CallCount);
    }

    [Fact]
    public void StreamingUpdateCoalescer_CoalescesRapidStreamingUpdatesAndNeverDropsTransitionsOrCompletion()
    {
        var now = TimeSpan.Zero;
        var coalescer = new StreamingUpdateCoalescer(TimeSpan.FromMilliseconds(25), () => now);

        static ProviderStreamUpdate MakeUpdate(
            string providerId,
            ProviderStreamState state,
            string delta,
            string accumulated,
            bool isReasoning = false,
            LanguageCode? detected = null) =>
            new(
                ProviderInstanceId: providerId,
                ProviderDisplayName: providerId,
                ProviderKind: ProviderKind.OpenAiCompatible,
                State: state,
                DeltaText: delta,
                AccumulatedText: accumulated,
                DetectedSourceLanguage: detected,
                TargetLanguage: LanguageCode.ChineseSimplified,
                ErrorMessage: null,
                Elapsed: TimeSpan.FromMilliseconds(120),
                IsReasoning: isReasoning);

        // 1. Pending always flushes immediately.
        var pendingDecision = coalescer.Push(MakeUpdate("p1", ProviderStreamState.Pending, "", ""));
        Assert.True(pendingDecision.ShouldDrainNow);
        Assert.Single(coalescer.DrainReady(), u => u.State == ProviderStreamState.Pending);

        // 2. Reasoning transition flushes immediately; duplicate reasoning heartbeat within 25ms coalesces.
        now = TimeSpan.FromMilliseconds(1);
        Assert.True(coalescer.Push(MakeUpdate("p1", ProviderStreamState.Streaming, "", "", isReasoning: true)).ShouldDrainNow);
        Assert.Single(coalescer.DrainReady(), u => u.IsReasoning);

        now = TimeSpan.FromMilliseconds(5);
        var dupReasoning = coalescer.Push(MakeUpdate("p1", ProviderStreamState.Streaming, "", "", isReasoning: true));
        Assert.False(dupReasoning.ShouldDrainNow);
        Assert.True(dupReasoning.ShouldScheduleDelayedFlush);

        // 3. First non-empty streaming chunk + reasoning exit flushes immediately.
        now = TimeSpan.FromMilliseconds(8);
        Assert.True(coalescer.Push(MakeUpdate("p1", ProviderStreamState.Streaming, "Hello", "Hello", isReasoning: false)).ShouldDrainNow);
        var firstChunk = Assert.Single(coalescer.DrainReady());
        Assert.Equal("Hello", firstChunk.AccumulatedText);
        Assert.False(firstChunk.IsReasoning);

        // 4. Rapid streaming tokens within 25ms coalesce without immediate UI drain.
        var accumulated = new StringBuilder("Hello");
        for (var i = 1; i <= 30; i++)
        {
            now = TimeSpan.FromMilliseconds(8 + i * 0.5); // 8.5ms .. 23ms (< 8 + 25ms)
            var token = $" t{i}";
            accumulated.Append(token);
            var decision = coalescer.Push(MakeUpdate(
                "p1",
                ProviderStreamState.Streaming,
                token,
                accumulated.ToString(),
                detected: i == 1 ? LanguageCode.English : null));
            Assert.False(decision.ShouldDrainNow);
        }

        Assert.Empty(coalescer.DrainReady());

        // 5. Once window elapses, FlushDue promotes the latest coalesced snapshot (preserving DetectedSourceLanguage).
        now = TimeSpan.FromMilliseconds(35);
        var due = coalescer.FlushDue();
        Assert.True(due.HasReadyUpdates);
        var flushed = Assert.Single(coalescer.DrainReady());
        Assert.Equal(accumulated.ToString(), flushed.AccumulatedText);
        Assert.Equal(LanguageCode.English, flushed.DetectedSourceLanguage);

        // 6. More rapid updates followed by Completed flush immediately with exact final text.
        now = TimeSpan.FromMilliseconds(40);
        accumulated.Append(" tail");
        Assert.False(coalescer.Push(MakeUpdate("p1", ProviderStreamState.Streaming, " tail", accumulated.ToString())).ShouldDrainNow);

        now = TimeSpan.FromMilliseconds(42);
        accumulated.Append("!");
        Assert.True(coalescer.Push(MakeUpdate("p1", ProviderStreamState.Completed, "", accumulated.ToString())).ShouldDrainNow);
        var completed = Assert.Single(coalescer.DrainReady());
        Assert.Equal(ProviderStreamState.Completed, completed.State);
        Assert.Equal(accumulated.ToString(), completed.AccumulatedText);

        // 7. Delta-only stream (empty AccumulatedText) concatenates deltas losslessly and flushes on FlushAll.
        now = TimeSpan.FromMilliseconds(50);
        Assert.True(coalescer.Push(MakeUpdate("p2", ProviderStreamState.Streaming, "A", "")).ShouldDrainNow);
        Assert.Equal("A", Assert.Single(coalescer.DrainReady()).DeltaText);

        now = TimeSpan.FromMilliseconds(52);
        Assert.False(coalescer.Push(MakeUpdate("p2", ProviderStreamState.Streaming, "B", "")).ShouldDrainNow);
        now = TimeSpan.FromMilliseconds(54);
        Assert.False(coalescer.Push(MakeUpdate("p2", ProviderStreamState.Streaming, "C", "")).ShouldDrainNow);

        Assert.True(coalescer.FlushAll());
        Assert.Equal("BC", Assert.Single(coalescer.DrainReady()).DeltaText);
    }

    [Fact]
    public async Task TranslationPopupViewModel_RapidStreamingTranslateAndPolish_CoalescesUpdatesAndDeliversCompleteText()
    {
        var tokens = Enumerable.Range(1, 80).Select(i => $"词{i} ").ToArray();
        var expectedFullText = string.Concat(tokens);

        var settings = CreateSettingsWithProviders(
            new ProviderInstanceConfig
            {
                Id = "fast-llm-translate",
                DisplayName = "Fast Translate LLM",
                Kind = ProviderKind.OpenAiCompatible,
                Role = ProviderRole.Translate,
                Enabled = true,
                SortOrder = 0,
            },
            new ProviderInstanceConfig
            {
                Id = "fast-llm-polish",
                DisplayName = "Fast Polish LLM",
                Kind = ProviderKind.OpenAiCompatible,
                Role = ProviderRole.Polish,
                TriggerMode = PolishTriggerMode.AutoParallel,
                Enabled = true,
                SortOrder = 1,
            });

        IEnumerable<ProviderStreamUpdate> BuildBurst(string providerId, LanguageCode target)
        {
            yield return new ProviderStreamUpdate(
                providerId, providerId, ProviderKind.OpenAiCompatible,
                ProviderStreamState.Pending, "", "", null, target, null, TimeSpan.Zero);
            yield return new ProviderStreamUpdate(
                providerId, providerId, ProviderKind.OpenAiCompatible,
                ProviderStreamState.Streaming, "", "", null, target, null, TimeSpan.Zero, IsReasoning: true);

            var sb = new StringBuilder();
            foreach (var token in tokens)
            {
                sb.Append(token);
                yield return new ProviderStreamUpdate(
                    providerId, providerId, ProviderKind.OpenAiCompatible,
                    ProviderStreamState.Streaming, token, sb.ToString(),
                    LanguageCode.English, target, null, TimeSpan.Zero, IsReasoning: false);
            }

            yield return new ProviderStreamUpdate(
                providerId, providerId, ProviderKind.OpenAiCompatible,
                ProviderStreamState.Completed, "", expectedFullText,
                LanguageCode.English, target, null, TimeSpan.FromMilliseconds(45));
        }

        var vm = CreateViewModel(
            settings,
            translationOrchestrator: new BurstTranslationOrchestrator(_ => BuildBurst("fast-llm-translate", LanguageCode.ChineseSimplified)),
            polishOrchestrator: new BurstPolishOrchestrator(_ => BuildBurst("fast-llm-polish", LanguageCode.English)),
            ocrOrchestrator: new CapturingOcrOrchestrator());

        var translateChangeCount = 0;
        var polishChangeCount = 0;
        vm.ServiceCards.CollectionChanged += (_, args) =>
        {
            if (args.NewItems is null)
            {
                return;
            }

            foreach (ServiceCardViewModel card in args.NewItems)
            {
                card.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName != nameof(ServiceCardViewModel.TranslatedText))
                    {
                        return;
                    }

                    if (card.IsTranslateMode)
                    {
                        Interlocked.Increment(ref translateChangeCount);
                    }
                    else
                    {
                        Interlocked.Increment(ref polishChangeCount);
                    }
                };
            }
        };

        await vm.TranslateTextAsync("Hello rapid streaming world");

        var translateCard = Assert.Single(vm.ServiceCards, c => c.ProviderInstanceId == "fast-llm-translate");
        var polishCard = Assert.Single(vm.ServiceCards, c => c.ProviderInstanceId == "fast-llm-polish");

        Assert.Equal(ProviderStreamState.Completed, translateCard.State);
        Assert.Equal(expectedFullText, translateCard.TranslatedText);
        Assert.Equal(ProviderStreamState.Completed, polishCard.State);
        Assert.Equal(expectedFullText, polishCard.TranslatedText);

        // Coalescing must cut 80 back-to-back streaming updates down dramatically while keeping 100% accuracy.
        Assert.InRange(translateChangeCount, 1, 20);
        Assert.InRange(polishChangeCount, 1, 20);
    }

    [Fact]
    public async Task AppUiComponents_ImplementSettingsTeardownLazyHoverZeroDelayScreenshotAndCachedBrushes()
    {
        // Reflection checks for new public API surface on WinUI windows (without instantiating XAML windows).
        Assert.NotNull(typeof(FloatingHoverIconWindow).GetProperty(nameof(FloatingHoverIconWindow.IsIconVisible)));
        var hoverVisibilityEvent = typeof(FloatingHoverIconWindow).GetEvent(
            nameof(FloatingHoverIconWindow.VisibilityChanged),
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.NotNull(hoverVisibilityEvent);
        Assert.Equal(typeof(EventHandler<bool>), hoverVisibilityEvent!.EventHandlerType);
        Assert.NotNull(typeof(TranslationPopupWindow).GetEvent(nameof(TranslationPopupWindow.PopupShown)));
        Assert.NotNull(typeof(TranslationPopupWindow).GetEvent(nameof(TranslationPopupWindow.PopupHidden)));
        Assert.NotNull(typeof(SettingsWindow).GetEvent(nameof(SettingsWindow.WindowClosed)));
        Assert.NotNull(typeof(SettingsWindow).GetProperty(nameof(SettingsWindow.AllowClose)));

        var repoRoot = FindRepoRoot();

        // 1. SettingsWindow.xaml.cs: closes genuinely, clears ContentFrame + BackStack, raises WindowClosed.
        var settingsWindowSrc = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "src", "PopTranslate.App", "Windows", "SettingsWindow.xaml.cs"));
        Assert.Contains("ContentFrame.Content = null;", settingsWindowSrc, StringComparison.Ordinal);
        Assert.Contains("ContentFrame.BackStack.Clear();", settingsWindowSrc, StringComparison.Ordinal);
        Assert.Contains("WindowClosed?.Invoke", settingsWindowSrc, StringComparison.Ordinal);
        Assert.DoesNotContain("args.Cancel = true;", settingsWindowSrc, StringComparison.Ordinal);

        // 2. AppCoordinator.cs: lazy hover window, visibility sync, conditional 80ms delay, raw pixel release, idle trim.
        var coordinatorSrc = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "src", "PopTranslate.App", "Services", "AppCoordinator.cs"));
        Assert.Contains("EnsureHoverIconWindow(", coordinatorSrc, StringComparison.Ordinal);
        Assert.Contains("_selectionHoverMonitor.SetHoverIconVisible(", coordinatorSrc, StringComparison.Ordinal);
        Assert.Contains("_windowHelperService.TrimProcessWorkingSet()", coordinatorSrc, StringComparison.Ordinal);
        Assert.Contains("_popupViewModel.ReleaseOcrRawPixels()", coordinatorSrc, StringComparison.Ordinal);
        Assert.Contains("_popupViewModel.ReleaseIdleMemory()", coordinatorSrc, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(25)", coordinatorSrc, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(2)", coordinatorSrc, StringComparison.Ordinal);
        Assert.Contains("GC.Collect(2, GCCollectionMode.Optimized, blocking: false)", coordinatorSrc, StringComparison.Ordinal);
        Assert.Contains("if (hadVisibleOwnOverlay)", coordinatorSrc, StringComparison.Ordinal);
        Assert.Contains("_selectionHoverMonitor.UnregisterIgnoredWindow(", coordinatorSrc, StringComparison.Ordinal);

        // 3. TranslationResultCardControl.xaml.cs: caches Light & Dark SolidColorBrush via [ThreadStatic] without per-render allocations.
        var cardControlSrc = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "src", "PopTranslate.App", "Controls", "TranslationResultCardControl.xaml.cs"));
        Assert.Contains("[ThreadStatic]", cardControlSrc, StringComparison.Ordinal);
        Assert.Contains("GetBrushes(", cardControlSrc, StringComparison.Ordinal);

        var renderMarkdownIndex = cardControlSrc.IndexOf("private void RenderFormattedMarkdown()", StringComparison.Ordinal);
        Assert.True(renderMarkdownIndex > 0);
        var renderSection = cardControlSrc[renderMarkdownIndex..];
        Assert.DoesNotContain("new SolidColorBrush(", renderSection, StringComparison.Ordinal);
    }

    private static AppSettings CreateSettingsWithProviders(params ProviderInstanceConfig[] providers)
    {
        var settings = AppSettings.CreateDefault();
        settings.Window.AutoCopyFirstResult = false;
        settings.AutoBackTranslate = false;
        settings.Providers = [.. providers];
        return settings;
    }

    private static TranslationPopupViewModel CreateViewModel(
        AppSettings settings,
        ITranslationOrchestrator translationOrchestrator,
        IPolishOrchestrator polishOrchestrator,
        IOcrOrchestrator ocrOrchestrator)
    {
        var detector = new LocalLanguageDetector();
        var vm = new TranslationPopupViewModel(
            new InMemorySettingsStore(settings),
            new TextPreprocessor(),
            detector,
            new DualTargetLanguageRouter(detector),
            translationOrchestrator,
            new NoOpBackTranslationService(),
            polishOrchestrator,
            new LocalTextDiffService(),
            ocrOrchestrator,
            new NoOpTextWriteBackService(),
            new NoOpTtsService());
        vm.ApplySettings(settings, applySessionDefaults: true);
        return vm;
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

    private sealed class InMemorySettingsStore(AppSettings initial) : ISettingsStore
    {
        public string SettingsFilePath => string.Empty;

        public AppSettings Current { get; private set; } = initial;

        public event EventHandler<AppSettings>? SettingsChanged;

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings;
            SettingsChanged?.Invoke(this, settings);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingOcrOrchestrator : IOcrOrchestrator
    {
        public int CallCount { get; private set; }

        public OcrImage? LastImage { get; private set; }

        public Task<OcrResult> RecognizeAsync(OcrImage image, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastImage = image;
            return Task.FromResult(new OcrResult(
                IsSuccess: false,
                Text: string.Empty,
                DetectedLanguage: null,
                ProviderKind: OcrProviderKind.WindowsNative,
                ErrorMessage: "stub",
                Elapsed: TimeSpan.FromMilliseconds(1)));
        }

        public Task<IReadOnlyList<OcrLanguagePackInfo>> GetInstalledWindowsOcrLanguagesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OcrLanguagePackInfo>>([]);

        public Task<ProviderHealthCheckResult> TestActiveProviderAsync(
            OcrConfig config,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderHealthCheckResult(true, "ok", TimeSpan.Zero));
    }

    private sealed class BurstTranslationOrchestrator(
        Func<TranslationRequest, IEnumerable<ProviderStreamUpdate>> producer) : ITranslationOrchestrator
    {
        public async IAsyncEnumerable<ProviderStreamUpdate> TranslateParallelAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var update in producer(request))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
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
                "stub", "stub", ProviderKind.OpenAiCompatible, string.Empty,
                null, LanguageCode.ChineseSimplified, false, "stub", TimeSpan.Zero));

        public Task<ProviderHealthCheckResult> TestProviderAsync(
            ProviderInstanceConfig config,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderHealthCheckResult(true, "ok", TimeSpan.Zero));
    }

    private sealed class BurstPolishOrchestrator(
        Func<PolishRequest, IEnumerable<ProviderStreamUpdate>> producer) : IPolishOrchestrator
    {
        public async IAsyncEnumerable<ProviderStreamUpdate> PolishParallelAsync(
            PolishRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var update in producer(request))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }
        }

        public Task<IReadOnlyList<TranslationResult>> PolishAllAsync(
            PolishRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TranslationResult>>([]);

        public IReadOnlyList<ProviderInstanceConfig> GetEligibleProviders() => [];
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
                request.ProviderInstanceId, "stub", ProviderKind.OpenAiCompatible, string.Empty,
                null, request.BackTargetLanguage, false, "stub", TimeSpan.Zero));
    }

    private sealed class NoOpTextWriteBackService : ITextWriteBackService
    {
        public Task<bool> ReplaceTextInWindowAsync(
            nint targetWindowHandle,
            string replacementText,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class NoOpTtsService : ITtsService
    {
        public bool IsSpeaking => false;

        public Task SpeakAsync(
            string text,
            LanguageCode? language = null,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }
}
