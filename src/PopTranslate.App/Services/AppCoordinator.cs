using PopTranslate.App.ViewModels;
using PopTranslate.App.Windows;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace PopTranslate.App.Services;

/// <summary>
/// Prevents PopTranslate's own clipboard writes (auto-copy, manual copy, silent OCR, selection fallback,
/// and write-back paste) from re-triggering the temporary clipboard translation listener.
/// </summary>
internal static class ClipboardLoopGuard
{
    private static readonly Lock SyncRoot = new();
    private static string? _lastInternalCopiedText;
    private static DateTimeOffset _lastInternalCopyUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset _suppressUntilUtc = DateTimeOffset.MinValue;

    public static void SuppressFor(TimeSpan duration)
    {
        lock (SyncRoot)
        {
            var candidate = DateTimeOffset.UtcNow.Add(duration);
            if (candidate > _suppressUntilUtc)
            {
                _suppressUntilUtc = candidate;
            }
        }
    }

    public static bool TryCopyText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        lock (SyncRoot)
        {
            _lastInternalCopiedText = text;
            _lastInternalCopyUtc = DateTimeOffset.UtcNow;
            var candidate = _lastInternalCopyUtc.AddMilliseconds(900);
            if (candidate > _suppressUntilUtc)
            {
                _suppressUntilUtc = candidate;
            }
        }

        return AppAssetHelper.TryCopyTextToClipboard(text);
    }

    public static bool ShouldIgnoreClipboardEvent(string? candidateText = null)
    {
        lock (SyncRoot)
        {
            var now = DateTimeOffset.UtcNow;
            if (now < _suppressUntilUtc)
            {
                return true;
            }

            if (candidateText is not null &&
                string.Equals(candidateText, _lastInternalCopiedText, StringComparison.Ordinal) &&
                (now - _lastInternalCopyUtc).TotalSeconds < 3.0)
            {
                return true;
            }

            return false;
        }
    }
}

/// <summary>
/// Central UI-thread orchestrator connecting global hotkeys, single Fluent 2 hover icon,
/// screenshot/silent OCR, unified parallel translation &amp; AI polish popup, Mica settings window, and tray residency.
/// </summary>
public sealed class AppCoordinator : IDisposable
{
    private const int MaxClipboardAutoTranslateLength = 8000;
    private static readonly TimeSpan PopupIdleCleanupDelay = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan SettingsCloseTrimDelay = TimeSpan.FromSeconds(2);

    private readonly IServiceProvider _serviceProvider;
    private readonly ISettingsStore _settingsStore;
    private readonly IGlobalHotkeyService _globalHotkeyService;
    private readonly ISelectionHoverMonitor _selectionHoverMonitor;
    private readonly ITextSelectionService _textSelectionService;
    private readonly ITextWriteBackService _textWriteBackService;
    private readonly ITranslationOrchestrator _translationOrchestrator;
    private readonly IScreenshotCaptureService _screenshotCaptureService;
    private readonly IOcrOrchestrator _ocrOrchestrator;
    private readonly IProxyHttpClientFactory _proxyHttpClientFactory;
    private readonly IWindowHelperService _windowHelperService;
    private readonly IForegroundAppGuard _foregroundAppGuard;
    private readonly IStartupPrivilegeService _startupPrivilegeService;
    private readonly SingleInstanceGuard _singleInstanceGuard;
    private readonly TrayIconHost _trayIconHost;
    private readonly TranslationPopupViewModel _popupViewModel;
    private readonly SettingsViewModel _settingsViewModel;

    private DispatcherQueue? _uiDispatcher;
    private DispatcherQueueTimer? _hoverMuteExpiryTimer;
    private DispatcherQueueTimer? _idleCleanupTimer;
    private DispatcherQueueTimer? _settingsTrimTimer;
    private CancellationTokenSource? _replaceTranslateCts;
    private FloatingHoverIconWindow? _hoverIconWindow;
    private TranslationPopupWindow? _popupWindow;
    private SettingsWindow? _settingsWindow;

    private HoverSelectionEventArgs? _pendingHoverSelection;
    private HotkeyConfig? _lastAppliedHotkeys;
    private ProxyConfig? _lastAppliedProxy;
    private IReadOnlyDictionary<HotkeyAction, HotkeyRegistrationResult> _lastHotkeyResults =
        new Dictionary<HotkeyAction, HotkeyRegistrationResult>();

    private nint _lastSourceWindowHandle;
    private DateTimeOffset? _hoverMutedUntilUtc;
    private DateTimeOffset _lastDoubleTapHandledUtc = DateTimeOffset.MinValue;
    private bool _isClipboardListenerEnabled;
    private string? _lastClipboardListenerText;

    private bool _initialized;
    private bool _isExiting;
    private bool _disposed;

    public AppCoordinator(
        IServiceProvider serviceProvider,
        ISettingsStore settingsStore,
        IGlobalHotkeyService globalHotkeyService,
        ISelectionHoverMonitor selectionHoverMonitor,
        ITextSelectionService textSelectionService,
        ITextWriteBackService textWriteBackService,
        ITranslationOrchestrator translationOrchestrator,
        IScreenshotCaptureService screenshotCaptureService,
        IOcrOrchestrator ocrOrchestrator,
        IProxyHttpClientFactory proxyHttpClientFactory,
        IWindowHelperService windowHelperService,
        IForegroundAppGuard foregroundAppGuard,
        IStartupPrivilegeService startupPrivilegeService,
        SingleInstanceGuard singleInstanceGuard,
        TrayIconHost trayIconHost,
        TranslationPopupViewModel popupViewModel,
        SettingsViewModel settingsViewModel)
    {
        _serviceProvider = serviceProvider;
        _settingsStore = settingsStore;
        _globalHotkeyService = globalHotkeyService;
        _selectionHoverMonitor = selectionHoverMonitor;
        _textSelectionService = textSelectionService;
        _textWriteBackService = textWriteBackService;
        _translationOrchestrator = translationOrchestrator;
        _screenshotCaptureService = screenshotCaptureService;
        _ocrOrchestrator = ocrOrchestrator;
        _proxyHttpClientFactory = proxyHttpClientFactory;
        _windowHelperService = windowHelperService;
        _foregroundAppGuard = foregroundAppGuard;
        _startupPrivilegeService = startupPrivilegeService;
        _singleInstanceGuard = singleInstanceGuard;
        _trayIconHost = trayIconHost;
        _popupViewModel = popupViewModel;
        _settingsViewModel = settingsViewModel;
    }

    public nint LastSourceWindowHandle => _lastSourceWindowHandle;

    private bool IsHoverMuted =>
        _hoverMutedUntilUtc is { } until && DateTimeOffset.UtcNow < until;

    public async Task InitializeAsync(DispatcherQueue uiDispatcher, string[]? args = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
        {
            return;
        }

        _uiDispatcher = uiDispatcher;
        _initialized = true;

        var isFirstEverLaunch = !File.Exists(_settingsStore.SettingsFilePath);
        var settings = await _settingsStore.LoadAsync();
        var isAdmin = SafeIsAdmin();

        // Lazily pre-warm hover icon ONLY when enabled in settings (avoids allocating an unused WinUI window + Acrylic host).
        if (settings.Selection.EnableHoverIcon)
        {
            EnsureHoverIconWindow(settings);
        }
        else
        {
            _selectionHoverMonitor.SetHoverIconVisible(false);
        }

        // Pre-warm Desktop Acrylic unified popup hidden for instant (<30ms) display.
        _popupWindow = _serviceProvider.GetRequiredService<TranslationPopupWindow>();
        _popupWindow.PreWarm(settings);
        _popupWindow.SettingsRequested += (_, pageTag) => _ = OpenSettingsAsync(pageTag);
        _popupWindow.ScreenshotRequested += (_, _) => _ = TriggerScreenshotTranslationAsync(forceSilentCopyOnly: false);
        _popupWindow.FixedPositionMoved += OnPopupFixedPositionMoved;
        _popupWindow.PopupWidthChanged += OnPopupWidthChanged;
        _popupWindow.PopupShown += OnPopupShown;
        _popupWindow.PopupHidden += OnPopupHidden;
        _popupViewModel.PinStateChanged += OnPopupPinStateChanged;
        _selectionHoverMonitor.RegisterIgnoredWindow(_popupWindow.WindowHandle);

        // Wire SettingsViewModel privilege restart request.
        _settingsViewModel.RestartAsAdministratorRequested += (_, _) => PostUi(RestartAsAdministrator);

        // Wire persistent Windows 11 system tray icon (always dispatch via PostUi to prevent synchronous menu re-entrancy).
        _trayIconHost.InputTranslateRequested += (_, _) => PostUi(() => OpenInputTranslation(centerOnScreen: true));
        _trayIconHost.ReplaceTranslateRequested += (_, _) =>
            PostUi(() => _ = HandleReplaceTranslateAsync(_windowHelperService.GetForegroundWindowHandle(), _windowHelperService.GetCursorPosition()));
        _trayIconHost.PolishInputRequested += (_, _) => PostUi(() => OpenInputPolish(centerOnScreen: true));
        _trayIconHost.ScreenshotTranslateRequested += (_, _) => PostUi(() => _ = TriggerScreenshotTranslationAsync(forceSilentCopyOnly: false));
        _trayIconHost.SilentOcrRequested += (_, _) => PostUi(() => _ = TriggerScreenshotTranslationAsync(forceSilentCopyOnly: true));
        _trayIconHost.HoverIconToggled += (_, enabled) => PostUi(() => _ = ToggleHoverIconAsync(enabled));
        _trayIconHost.HoverMuteToggled += (_, muted) => PostUi(() => SetHoverMute(muted ? TimeSpan.FromMinutes(30) : null));
        _trayIconHost.ClipboardListenerToggled += (_, enabled) => PostUi(() => SetClipboardListenerEnabled(enabled));
        _trayIconHost.SettingsRequested += (_, _) => PostUi(() => _ = OpenSettingsAsync());
        _trayIconHost.RestartAsAdministratorRequested += (_, _) => PostUi(RestartAsAdministrator);
        _trayIconHost.ExitRequested += (_, _) => PostUi(RequestExit);
        _trayIconHost.Initialize(settings, isAdmin, _uiDispatcher, IsHoverMuted, _isClipboardListenerEnabled);

        // Wire OS services.
        _globalHotkeyService.HotkeyPressed += OnGlobalHotkeyPressed;
        _selectionHoverMonitor.SelectionDetected += OnSelectionHoverDetected;
        _selectionHoverMonitor.DismissRequested += OnSelectionHoverDismissRequested;
        _selectionHoverMonitor.DoubleTapCopyDetected += OnDoubleTapCopyDetected;
        _settingsStore.SettingsChanged += OnSettingsChanged;
        _singleInstanceGuard.SecondInstanceArgumentsReceived += OnSecondInstanceArgumentsReceived;

        // Apply initial hotkeys, proxy baseline, and hover/double-tap monitor.
        _lastAppliedProxy = settings.Proxy.Clone();
        ApplyHotkeys(settings.Hotkeys, force: true);
        ApplyHoverAndDoubleTapConfig(settings.Selection, settings.Hotkeys);

        await HandleStartupArgumentsAsync(args ?? [], isFirstEverLaunch, settings);
    }

    public void OpenInputTranslation(
        string? initialText = null,
        nint? sourceWindowHandle = null,
        bool centerOnScreen = false) =>
        OpenUnifiedPopupForInput(initialText, sourceWindowHandle, prioritizePolish: false, centerOnScreen: centerOnScreen);

    public void OpenInputPolish(
        string? initialText = null,
        nint? sourceWindowHandle = null,
        bool centerOnScreen = false) =>
        OpenUnifiedPopupForInput(initialText, sourceWindowHandle, prioritizePolish: true, centerOnScreen: centerOnScreen);

    private void OpenUnifiedPopupForInput(
        string? initialText,
        nint? sourceWindowHandle,
        bool prioritizePolish,
        bool centerOnScreen)
    {
        if (_disposed || _popupWindow is null)
        {
            return;
        }

        _hoverIconWindow?.HideIcon();
        var resolvedSource = SanitizeSourceWindowHandle(
            sourceWindowHandle ?? _windowHelperService.GetForegroundWindowHandle());
        _lastSourceWindowHandle = resolvedSource;
        _popupViewModel.SetSourceWindowHandle(resolvedSource);

        var cursor = _windowHelperService.GetCursorPosition();
        if (!string.IsNullOrWhiteSpace(initialText))
        {
            _popupViewModel.SetPopupVisible(true);
            _ = _popupViewModel.TranslateTextAsync(
                initialText,
                isFromOcr: false,
                resetLanguageSelection: true,
                prioritizePolish: prioritizePolish,
                collapseSourceCard: true);
            _popupWindow.ShowPopup(cursor, focusSourceInput: false, centerOnScreen: centerOnScreen);
        }
        else
        {
            _popupViewModel.SetPopupVisible(true);
            if (_popupViewModel.TryResumeLastSession(prioritizePolish))
            {
                _popupWindow.ShowPopup(cursor, focusSourceInput: false, centerOnScreen: centerOnScreen);
            }
            else
            {
                _popupViewModel.PrepareForInput(prioritizePolish: prioritizePolish, collapseSourceCard: false);
                _popupWindow.ShowPopup(cursor, focusSourceInput: true, centerOnScreen: centerOnScreen);
            }
        }
    }

    public async Task OpenSettingsAsync(string? initialPageTag = null)
    {
        if (_disposed)
        {
            return;
        }

        _settingsTrimTimer?.Stop();

        if (_settingsWindow is null)
        {
            var settingsWindow = _serviceProvider.GetRequiredService<SettingsWindow>();
            settingsWindow.ApplyTheme(_settingsStore.Current.Window.Theme);
            settingsWindow.ExitRequested += OnSettingsWindowExitRequested;
            settingsWindow.WindowClosed += OnSettingsWindowClosed;
            _selectionHoverMonitor.RegisterIgnoredWindow(settingsWindow.WindowHandle);
            _settingsWindow = settingsWindow;
        }

        _settingsViewModel.UpdateHotkeyRegistrationResults(_lastHotkeyResults);
        await _settingsWindow.ShowAndRefreshAsync(initialPageTag);
    }

    public async Task TriggerScreenshotTranslationAsync(bool forceSilentCopyOnly = false)
    {
        if (_disposed || _popupWindow is null || _screenshotCaptureService.IsCaptureActive)
        {
            return;
        }

        var hadVisibleOwnOverlay = _popupWindow.IsPopupVisible || (_hoverIconWindow?.IsIconVisible ?? false);
        _hoverIconWindow?.HideIcon();
        _popupWindow.HidePopup(cancelTranslations: true);

        if (hadVisibleOwnOverlay)
        {
            // Allow DWM to finish hiding our popup/hover frame before taking the frozen monitor snapshot.
            await Task.Delay(80);
        }

        CapturedScreenshot? screenshot;
        try
        {
            screenshot = await _screenshotCaptureService.CaptureRegionAsync();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (!forceSilentCopyOnly)
            {
                _popupViewModel.SetSourceWindowHandle(0);
                _popupWindow.ShowPopup(_windowHelperService.GetCursorPosition(), focusSourceInput: false);
                _popupViewModel.ShowStatusError($"启动截图选区失败：{ex.Message}");
            }
            else
            {
                AppDiagnosticsLogger.Warn("SilentOcr", "Screenshot region capture failed.", ex);
            }

            return;
        }

        if (screenshot is null || screenshot.PngBytes.Length == 0)
        {
            return;
        }

        var isSilentCopyOnly = forceSilentCopyOnly || screenshot.CopyOnly;
        if (isSilentCopyOnly)
        {
            await ExecuteSilentOcrAsync(screenshot);
            return;
        }

        _lastSourceWindowHandle = 0;
        _popupViewModel.SetSourceWindowHandle(0);

        var anchor = new ScreenPoint(screenshot.CaptureBounds.Right, screenshot.CaptureBounds.Bottom);
        _popupViewModel.BeginOcrCapture(screenshot);
        _popupWindow.ShowPopup(anchor, focusSourceInput: false);

        try
        {
            var ocrResult = await _ocrOrchestrator.RecognizeAsync(screenshot.ToOcrImage());
            _popupViewModel.ReleaseOcrRawPixels();
            if (ocrResult.IsSuccess && !string.IsNullOrWhiteSpace(ocrResult.Text))
            {
                await _popupViewModel.TranslateTextAsync(
                    ocrResult.Text,
                    isFromOcr: true,
                    resetLanguageSelection: true,
                    prioritizePolish: false,
                    collapseSourceCard: true);
            }
            else
            {
                var error = string.IsNullOrWhiteSpace(ocrResult.ErrorMessage)
                    ? "未在所选截图区域中识别到文字。"
                    : $"OCR 识别失败：{ocrResult.ErrorMessage}";
                _popupViewModel.ShowStatusError(error);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled cleanly.
        }
        catch (Exception ex)
        {
            _popupViewModel.ShowStatusError($"OCR 识别异常：{ex.Message}");
        }
        finally
        {
            _popupViewModel.ReleaseOcrRawPixels();
        }
    }

    private async Task ExecuteSilentOcrAsync(CapturedScreenshot screenshot)
    {
        try
        {
            var ocrResult = await _ocrOrchestrator.RecognizeAsync(screenshot.ToOcrImage());
            if (ocrResult.IsSuccess && !string.IsNullOrWhiteSpace(ocrResult.Text))
            {
                ClipboardLoopGuard.TryCopyText(ocrResult.Text.Trim());
                AppDiagnosticsLogger.Info("SilentOcr", "Silent OCR completed and copied to clipboard.");
            }
            else
            {
                AppDiagnosticsLogger.Warn(
                    "SilentOcr",
                    string.IsNullOrWhiteSpace(ocrResult.ErrorMessage)
                        ? "Silent OCR detected no text in selected region."
                        : $"Silent OCR failed: {ocrResult.ErrorMessage}");
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled cleanly.
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("SilentOcr", "Silent OCR threw an exception.", ex);
        }
    }

    public void RestartAsAdministrator()
    {
        if (_disposed || SafeIsAdmin())
        {
            return;
        }

        var exePath = AppAssetHelper.ResolveEntryExecutablePath();
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return;
        }

        // Release single-instance lock so the newly elevated instance can acquire it seamlessly.
        _singleInstanceGuard.ReleaseForRestart();
        var launched = _startupPrivilegeService.RestartAsAdministrator(exePath, "--elevated");
        if (launched)
        {
            RequestExit();
        }
        else
        {
            // User declined UAC prompt; restore single-instance lock for current session.
            _singleInstanceGuard.TryAcquire();
        }
    }

    public void RequestExit()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        _ = ExecuteExitAsync();
    }

    private async Task ExecuteExitAsync()
    {
        try
        {
            await _settingsViewModel.FlushPendingChangesAsync();
        }
        catch
        {
            // Best-effort final settings flush on shutdown.
        }

        Dispose();
        Application.Current.Exit();
    }

    private async Task HandleStartupArgumentsAsync(string[] args, bool isFirstEverLaunch, AppSettings settings)
    {
        var isMinimized = HasFlag(args, "--minimized", "-minimized", "/minimized");
        var isElevated = HasFlag(args, "--elevated", "-elevated");

        if (isElevated)
        {
            await OpenSettingsAsync("Network");
            return;
        }

        if (await TryDispatchCliVerbAsync(args))
        {
            return;
        }

        if (isMinimized)
        {
            // Silent tray boot — never open InputTranslate on logon startup!
            return;
        }

        if (isFirstEverLaunch)
        {
            try
            {
                await _settingsStore.SaveAsync(settings);
            }
            catch (Exception ex)
            {
                AppDiagnosticsLogger.Warn("Startup", "Initial settings file persistence skipped.", ex);
            }

            await OpenSettingsAsync("General");
            return;
        }

        // Manual interactive launch without --minimized: surface the unified popup immediately
        // in the center of the desktop so the user gets instant visual confirmation.
        OpenInputTranslation(centerOnScreen: true);
    }

    private void OnSecondInstanceArgumentsReceived(object? sender, string[] args)
    {
        EnqueueUi(() => _ = HandleSecondInstanceArgumentsAsync(args));
    }

    private async Task HandleSecondInstanceArgumentsAsync(string[] args)
    {
        if (_disposed)
        {
            return;
        }

        if (HasFlag(args, "--minimized", "-minimized", "/minimized"))
        {
            // Secondary logon / background auto-start race: remain quietly in tray.
            return;
        }

        if (HasFlag(args, "--elevated", "-elevated"))
        {
            await OpenSettingsAsync("Network");
            return;
        }

        if (await TryDispatchCliVerbAsync(args))
        {
            return;
        }

        // Default secondary interactive launch with no CLI arguments: open Input Translation popup centered on desktop.
        OpenInputTranslation(centerOnScreen: true);
    }

    private async Task<bool> TryDispatchCliVerbAsync(string[] args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        string? verb = null;
        var payloadParts = new List<string>();

        foreach (var raw in args)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var trimmed = raw.Trim();
            if (verb is null)
            {
                var normalized = trimmed.TrimStart('-', '/').ToLowerInvariant();
                if (normalized is "translate" or "replace" or "polish" or "ocr" or "silent-ocr" or "silentocr" or "input" or "settings" or "exit" or "quit")
                {
                    verb = normalized;
                    continue;
                }
            }
            else if (!trimmed.StartsWith("--", StringComparison.Ordinal))
            {
                payloadParts.Add(trimmed);
            }
        }

        if (verb is null)
        {
            return false;
        }

        var trailingText = payloadParts.Count > 0 ? string.Join(' ', payloadParts) : null;
        var fgHwnd = _windowHelperService.GetForegroundWindowHandle();
        var cursor = _windowHelperService.GetCursorPosition();

        switch (verb)
        {
            case "translate":
                if (!string.IsNullOrWhiteSpace(trailingText))
                {
                    OpenInputTranslation(trailingText, fgHwnd);
                }
                else
                {
                    await HandleSelectionHotkeyAsync(
                        new HotkeyPressedEventArgs(HotkeyAction.SelectionTranslate, HotkeyGesture.Empty, cursor, fgHwnd),
                        prioritizePolish: false);
                }
                return true;

            case "replace":
                await HandleReplaceTranslateAsync(fgHwnd, cursor, trailingText);
                return true;

            case "polish":
                if (!string.IsNullOrWhiteSpace(trailingText))
                {
                    OpenInputPolish(trailingText, fgHwnd);
                }
                else
                {
                    await HandleSelectionHotkeyAsync(
                        new HotkeyPressedEventArgs(HotkeyAction.SelectionPolish, HotkeyGesture.Empty, cursor, fgHwnd),
                        prioritizePolish: true);
                }
                return true;

            case "ocr":
                await TriggerScreenshotTranslationAsync(forceSilentCopyOnly: false);
                return true;

            case "silent-ocr":
            case "silentocr":
                await TriggerScreenshotTranslationAsync(forceSilentCopyOnly: true);
                return true;

            case "input":
                OpenInputTranslation(trailingText, fgHwnd);
                return true;

            case "settings":
                await OpenSettingsAsync(trailingText);
                return true;

            case "exit":
            case "quit":
                RequestExit();
                return true;

            default:
                return false;
        }
    }

    private static bool HasFlag(string[] args, params string[] candidates)
    {
        foreach (var arg in args)
        {
            foreach (var candidate in candidates)
            {
                if (string.Equals(arg?.Trim(), candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void OnGlobalHotkeyPressed(object? sender, HotkeyPressedEventArgs e)
    {
        EnqueueUi(() => _ = HandleHotkeyAsync(e));
    }

    private async Task HandleHotkeyAsync(HotkeyPressedEventArgs args)
    {
        if (_disposed || _popupWindow is null)
        {
            return;
        }

        switch (args.Action)
        {
            case HotkeyAction.SelectionTranslate:
                await HandleSelectionHotkeyAsync(args, prioritizePolish: false);
                break;

            case HotkeyAction.ScreenshotTranslate:
                await TriggerScreenshotTranslationAsync(forceSilentCopyOnly: false);
                break;

            case HotkeyAction.SelectionPolish:
                await HandleSelectionHotkeyAsync(args, prioritizePolish: true);
                break;

            case HotkeyAction.SilentOcr:
                await TriggerScreenshotTranslationAsync(forceSilentCopyOnly: true);
                break;

            case HotkeyAction.ReplaceTranslate:
                await HandleReplaceTranslateAsync(args.ForegroundWindowHandle, args.CursorPosition);
                break;
        }
    }

    private async Task HandleReplaceTranslateAsync(
        nint foregroundHwnd,
        ScreenPoint anchorPoint,
        string? preCapturedText = null)
    {
        if (_disposed || _popupWindow is null)
        {
            return;
        }

        _hoverIconWindow?.HideIcon();
        var sourceHwnd = SanitizeSourceWindowHandle(foregroundHwnd);
        if (sourceHwnd == 0 && _lastSourceWindowHandle != 0)
        {
            sourceHwnd = SanitizeSourceWindowHandle(_lastSourceWindowHandle);
        }

        _lastSourceWindowHandle = sourceHwnd;
        _popupViewModel.SetSourceWindowHandle(sourceHwnd);

        _replaceTranslateCts?.Cancel();
        _replaceTranslateCts?.Dispose();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        _replaceTranslateCts = cts;
        var token = cts.Token;

        ClipboardLoopGuard.SuppressFor(TimeSpan.FromMilliseconds(1200));

        try
        {
            var sourceText = preCapturedText;
            if (string.IsNullOrWhiteSpace(sourceText))
            {
                if (sourceHwnd == 0)
                {
                    _popupViewModel.PrepareForInput(
                        initialHint: "未检测到可替换的目标输入窗口，请先在输入框中选中文字后再触发替换翻译。",
                        prioritizePolish: false,
                        collapseSourceCard: false);
                    _popupWindow.ShowPopup(anchorPoint, focusSourceInput: true);
                    return;
                }

                var captured = await _textSelectionService.CaptureSelectionAsync(
                    SelectionTriggerSource.Hotkey,
                    _settingsStore.Current.Selection,
                    sourceHwnd,
                    token);

                if (!captured.IsSuccess || string.IsNullOrWhiteSpace(captured.Text))
                {
                    var hint = string.IsNullOrWhiteSpace(captured.FailureReason)
                        ? "未读取到选中文本，请先在输入框中选中要替换的文字。"
                        : captured.FailureReason;
                    _popupViewModel.PrepareForInput(
                        initialHint: hint,
                        prioritizePolish: false,
                        collapseSourceCard: false);
                    _popupWindow.ShowPopup(anchorPoint, focusSourceInput: true);
                    return;
                }

                sourceText = captured.Text;
            }

            var translation = await _translationOrchestrator.TranslateFirstAvailableAsync(
                new TranslationRequest(
                    Text: sourceText,
                    SourceLanguage: LanguageCode.Auto,
                    TargetLanguage: LanguageCode.Auto),
                token);

            if (!translation.IsSuccess || string.IsNullOrWhiteSpace(translation.TranslatedText))
            {
                _popupViewModel.PrepareForInput(prioritizePolish: false, collapseSourceCard: false);
                _popupWindow.ShowPopup(anchorPoint, focusSourceInput: false);
                _popupViewModel.ShowStatusError(
                    string.IsNullOrWhiteSpace(translation.ErrorMessage)
                        ? "替换翻译未返回有效译文。"
                        : $"替换翻译失败：{translation.ErrorMessage}");
                return;
            }

            var replacement = translation.TranslatedText.Trim();
            if (sourceHwnd != 0)
            {
                ClipboardLoopGuard.SuppressFor(TimeSpan.FromMilliseconds(1200));
                var replaced = await _textWriteBackService.ReplaceTextInWindowAsync(sourceHwnd, replacement, token);
                if (replaced)
                {
                    AppDiagnosticsLogger.Info(
                        "ReplaceTranslate",
                        $"Replaced selection in-place via provider '{translation.ProviderDisplayName}'.");
                    return;
                }
            }

            // Fallback when UIPI or non-editable target window rejects write-back: copy result and surface popup.
            ClipboardLoopGuard.TryCopyText(replacement);
            _popupViewModel.PrepareForInput(prioritizePolish: false, collapseSourceCard: true);
            _popupWindow.ShowPopup(anchorPoint, focusSourceInput: false);
            await _popupViewModel.TranslateTextAsync(
                sourceText,
                isFromOcr: false,
                resetLanguageSelection: true,
                prioritizePolish: false,
                collapseSourceCard: true);
            _popupViewModel.ShowStatusError("目标窗口无法直接写入（可能为管理员权限或只读窗口），译文已自动复制到剪贴板。");
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer Replace-Translate trigger or timeout.
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("ReplaceTranslate", "Replace-translate pipeline failed.", ex);
            _popupViewModel.PrepareForInput(prioritizePolish: false, collapseSourceCard: false);
            _popupWindow.ShowPopup(anchorPoint, focusSourceInput: false);
            _popupViewModel.ShowStatusError($"替换翻译异常：{ex.Message}");
        }
    }

    private async Task HandleSelectionHotkeyAsync(HotkeyPressedEventArgs args, bool prioritizePolish)
    {
        if (_popupWindow is null)
        {
            return;
        }

        _hoverIconWindow?.HideIcon();
        var sourceHwnd = SanitizeSourceWindowHandle(args.ForegroundWindowHandle);
        _lastSourceWindowHandle = sourceHwnd;
        _popupViewModel.SetSourceWindowHandle(sourceHwnd);

        ClipboardLoopGuard.SuppressFor(TimeSpan.FromMilliseconds(900));

        try
        {
            var captured = await _textSelectionService.CaptureSelectionAsync(
                SelectionTriggerSource.Hotkey,
                _settingsStore.Current.Selection,
                args.ForegroundWindowHandle);

            if (captured.IsSuccess && !string.IsNullOrWhiteSpace(captured.Text))
            {
                _popupViewModel.SetPopupVisible(true);
                var translateTask = _popupViewModel.TranslateTextAsync(
                    captured.Text,
                    isFromOcr: false,
                    resetLanguageSelection: true,
                    prioritizePolish: prioritizePolish,
                    collapseSourceCard: true);
                _popupWindow.ShowPopup(args.CursorPosition, focusSourceInput: false);
                await translateTask;
            }
            else
            {
                _popupViewModel.SetPopupVisible(true);
                if (_popupViewModel.TryResumeLastSession(prioritizePolish))
                {
                    _popupWindow.ShowPopup(args.CursorPosition, focusSourceInput: false);
                }
                else
                {
                    // No text selected and no resumable session: open expanded input box and auto-focus for direct typing/pasting.
                    _popupViewModel.PrepareForInput(
                        initialHint: null,
                        prioritizePolish: prioritizePolish,
                        collapseSourceCard: false);
                    _popupWindow.ShowPopup(args.CursorPosition, focusSourceInput: true);
                }
            }
        }
        catch (Exception ex)
        {
            _popupViewModel.PrepareForInput(prioritizePolish: prioritizePolish, collapseSourceCard: false);
            _popupWindow.ShowPopup(args.CursorPosition, focusSourceInput: true);
            _popupViewModel.ShowStatusError($"取词失败：{ex.Message}");
        }
    }

    private void OnDoubleTapCopyDetected(object? sender, nint foregroundHwnd)
    {
        EnqueueUi(() => _ = HandleDoubleTapCopyAsync(foregroundHwnd));
    }

    private async Task HandleDoubleTapCopyAsync(nint foregroundHwnd)
    {
        if (_disposed || _popupWindow is null || !_settingsStore.Current.Hotkeys.EnableDoubleTapCopy)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if ((now - _lastDoubleTapHandledUtc).TotalMilliseconds < 350)
        {
            return;
        }

        _lastDoubleTapHandledUtc = now;
        var sourceHwnd = SanitizeSourceWindowHandle(foregroundHwnd);
        if (sourceHwnd == 0 && IsOwnWindow(foregroundHwnd))
        {
            return;
        }

        if (_foregroundAppGuard.IsForegroundAppSuppressed(_settingsStore.Current.Selection, out _, out _))
        {
            return;
        }

        _hoverIconWindow?.HideIcon();
        _lastSourceWindowHandle = sourceHwnd;
        _popupViewModel.SetSourceWindowHandle(sourceHwnd);
        ClipboardLoopGuard.SuppressFor(TimeSpan.FromMilliseconds(900));

        // Give the target window a moment to commit its native Ctrl+C text to the clipboard.
        await Task.Delay(55);

        var text = await TryReadClipboardTextAsync();
        if (string.IsNullOrWhiteSpace(text) && sourceHwnd != 0)
        {
            try
            {
                var captured = await _textSelectionService.CaptureSelectionAsync(
                    SelectionTriggerSource.Hotkey,
                    _settingsStore.Current.Selection,
                    sourceHwnd);
                if (captured.IsSuccess && !string.IsNullOrWhiteSpace(captured.Text))
                {
                    text = captured.Text;
                }
            }
            catch
            {
                // Ignore fallback failure on double-tap copy.
            }
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var cursor = _windowHelperService.GetCursorPosition();
        _popupViewModel.SetPopupVisible(true);
        var translateTask = _popupViewModel.TranslateTextAsync(
            text,
            isFromOcr: false,
            resetLanguageSelection: true,
            prioritizePolish: false,
            collapseSourceCard: true);
        _popupWindow.ShowPopup(cursor, focusSourceInput: false);
        await translateTask;
    }

    private void OnSelectionHoverDetected(object? sender, HoverSelectionEventArgs e)
    {
        EnqueueUi(() =>
        {
            if (_disposed || _screenshotCaptureService.IsCaptureActive)
            {
                return;
            }

            if (!_settingsStore.Current.Selection.EnableHoverIcon || IsHoverMuted)
            {
                return;
            }

            var hoverIconWindow = EnsureHoverIconWindow();
            _pendingHoverSelection = e;
            var processName = !string.IsNullOrWhiteSpace(e.SourceProcessName)
                ? e.SourceProcessName
                : _foregroundAppGuard.GetProcessNameForWindow(e.SourceWindowHandle);

            hoverIconWindow.ShowAt(
                e.AnchorPoint,
                _settingsStore.Current.Selection.HoverIconAutoDismissMs,
                processName);
        });
    }

    private void OnSelectionHoverDismissRequested(object? sender, EventArgs e)
    {
        EnqueueUi(() =>
        {
            _pendingHoverSelection = null;
            _hoverIconWindow?.HideIcon();
        });
    }

    private void OnHoverIconClicked(object? sender, EventArgs e)
    {
        var pending = _pendingHoverSelection;
        _pendingHoverSelection = null;

        if (pending is null || _popupWindow is null)
        {
            return;
        }

        _ = HandleHoverActionAsync(pending);
    }

    private void OnHoverReplaceTranslateClicked(object? sender, EventArgs e)
    {
        var pending = _pendingHoverSelection;
        _pendingHoverSelection = null;

        if (pending is null)
        {
            return;
        }

        var preCaptured = pending.HasUiaText && !string.IsNullOrWhiteSpace(pending.PreviewText)
            ? pending.PreviewText
            : null;
        _ = HandleReplaceTranslateAsync(pending.SourceWindowHandle, pending.AnchorPoint, preCaptured);
    }

    private async Task HandleHoverActionAsync(HoverSelectionEventArgs pending)
    {
        if (_popupWindow is null)
        {
            return;
        }

        var sourceHwnd = SanitizeSourceWindowHandle(pending.SourceWindowHandle);
        _lastSourceWindowHandle = sourceHwnd;
        _popupViewModel.SetSourceWindowHandle(sourceHwnd);

        // Fast zero-clipboard path when UIA already supplied the selected preview text.
        if (pending.HasUiaText && !string.IsNullOrWhiteSpace(pending.PreviewText))
        {
            _popupViewModel.SetPopupVisible(true);
            var translateTask = _popupViewModel.TranslateTextAsync(
                pending.PreviewText,
                isFromOcr: false,
                resetLanguageSelection: true,
                prioritizePolish: false,
                collapseSourceCard: true);
            _popupWindow.ShowPopup(pending.AnchorPoint, focusSourceInput: false);
            await translateTask;
            return;
        }

        ClipboardLoopGuard.SuppressFor(TimeSpan.FromMilliseconds(900));

        // Fallback capture ONLY after explicit user click on the floating icon.
        try
        {
            var captured = await _textSelectionService.CaptureSelectionAsync(
                SelectionTriggerSource.HoverIcon,
                _settingsStore.Current.Selection,
                pending.SourceWindowHandle);

            if (captured.IsSuccess && !string.IsNullOrWhiteSpace(captured.Text))
            {
                _popupViewModel.SetPopupVisible(true);
                var translateTask = _popupViewModel.TranslateTextAsync(
                    captured.Text,
                    isFromOcr: false,
                    resetLanguageSelection: true,
                    prioritizePolish: false,
                    collapseSourceCard: true);
                _popupWindow.ShowPopup(pending.AnchorPoint, focusSourceInput: false);
                await translateTask;
            }
            else
            {
                var fallbackMsg = string.IsNullOrWhiteSpace(captured.FailureReason)
                    ? "未能从目标窗口读取选区，请直接输入或粘贴。"
                    : captured.FailureReason;
                _popupViewModel.PrepareForInput(
                    initialHint: fallbackMsg,
                    prioritizePolish: false,
                    collapseSourceCard: false);
                _popupWindow.ShowPopup(pending.AnchorPoint, focusSourceInput: true);
            }
        }
        catch (Exception ex)
        {
            _popupViewModel.PrepareForInput(prioritizePolish: false, collapseSourceCard: false);
            _popupWindow.ShowPopup(pending.AnchorPoint, focusSourceInput: true);
            _popupViewModel.ShowStatusError($"划词读取失败：{ex.Message}");
        }
    }

    private void SetHoverMute(TimeSpan? duration)
    {
        _hoverMuteExpiryTimer?.Stop();

        if (duration is { TotalMilliseconds: > 0 } validDuration)
        {
            _hoverMutedUntilUtc = DateTimeOffset.UtcNow.Add(validDuration);
            _hoverIconWindow?.HideIcon();

            if (_uiDispatcher is not null)
            {
                _hoverMuteExpiryTimer ??= CreateHoverMuteExpiryTimer(_uiDispatcher);
                _hoverMuteExpiryTimer.Interval = validDuration;
                _hoverMuteExpiryTimer.Start();
            }
        }
        else
        {
            _hoverMutedUntilUtc = null;
        }

        var current = _settingsStore.Current;
        ApplyHoverAndDoubleTapConfig(current.Selection, current.Hotkeys);
        _trayIconHost.UpdateState(current, SafeIsAdmin(), IsHoverMuted, _isClipboardListenerEnabled);
    }

    private DispatcherQueueTimer CreateHoverMuteExpiryTimer(DispatcherQueue dispatcher)
    {
        var timer = dispatcher.CreateTimer();
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            _hoverMutedUntilUtc = null;
            if (!_disposed)
            {
                var current = _settingsStore.Current;
                ApplyHoverAndDoubleTapConfig(current.Selection, current.Hotkeys);
                _trayIconHost.UpdateState(current, SafeIsAdmin(), isHoverMuted: false, _isClipboardListenerEnabled);
            }
        };
        return timer;
    }

    private void SetClipboardListenerEnabled(bool enabled)
    {
        if (_isClipboardListenerEnabled == enabled)
        {
            return;
        }

        _isClipboardListenerEnabled = enabled;
        _lastClipboardListenerText = null;

        try
        {
            if (enabled)
            {
                Clipboard.ContentChanged += OnSystemClipboardContentChanged;
            }
            else
            {
                Clipboard.ContentChanged -= OnSystemClipboardContentChanged;
            }
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("ClipboardListener", "Failed to toggle system clipboard listener.", ex);
        }

        _trayIconHost.UpdateState(_settingsStore.Current, SafeIsAdmin(), IsHoverMuted, _isClipboardListenerEnabled);
    }

    private void OnSystemClipboardContentChanged(object? sender, object e)
    {
        if (!_isClipboardListenerEnabled || _disposed || ClipboardLoopGuard.ShouldIgnoreClipboardEvent())
        {
            return;
        }

        EnqueueUi(() => _ = HandleClipboardListenerChangeAsync());
    }

    private async Task HandleClipboardListenerChangeAsync()
    {
        if (!_isClipboardListenerEnabled || _disposed || _popupWindow is null || _screenshotCaptureService.IsCaptureActive)
        {
            return;
        }

        if (ClipboardLoopGuard.ShouldIgnoreClipboardEvent())
        {
            return;
        }

        var text = await TryReadClipboardTextAsync();
        if (string.IsNullOrWhiteSpace(text) ||
            text.Length > MaxClipboardAutoTranslateLength ||
            ClipboardLoopGuard.ShouldIgnoreClipboardEvent(text) ||
            string.Equals(text, _lastClipboardListenerText, StringComparison.Ordinal))
        {
            return;
        }

        _lastClipboardListenerText = text;
        var fgHwnd = _windowHelperService.GetForegroundWindowHandle();
        var sourceHwnd = SanitizeSourceWindowHandle(fgHwnd);
        if (sourceHwnd != 0)
        {
            _lastSourceWindowHandle = sourceHwnd;
        }

        _popupViewModel.SetSourceWindowHandle(_lastSourceWindowHandle);

        _hoverIconWindow?.HideIcon();
        var cursor = _windowHelperService.GetCursorPosition();
        _popupViewModel.SetPopupVisible(true);
        var translateTask = _popupViewModel.TranslateTextAsync(
            text,
            isFromOcr: false,
            resetLanguageSelection: true,
            prioritizePolish: false,
            collapseSourceCard: true);
        _popupWindow.ShowPopup(cursor, focusSourceInput: false);
        await translateTask;
    }

    private static async Task<string?> TryReadClipboardTextAsync()
    {
        try
        {
            var view = Clipboard.GetContent();
            if (view is not null && view.Contains(StandardDataFormats.Text))
            {
                return await view.GetTextAsync();
            }
        }
        catch
        {
            // Ignore transient clipboard lock contention.
        }

        return null;
    }

    private async Task AddProcessToBlacklistAsync(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return;
        }

        var normalized = Path.GetFileName(processName.Trim());
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (!normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            normalized = $"{normalized}.exe";
        }

        try
        {
            var updated = _settingsStore.Current.Clone();
            updated.Selection.ProcessBlacklist ??= [];
            if (!updated.Selection.ProcessBlacklist.Any(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                updated.Selection.ProcessBlacklist.Add(normalized);
                await _settingsStore.SaveAsync(updated);
                AppDiagnosticsLogger.Info("Selection", $"Added process '{normalized}' to hover blacklist.");
            }
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("Selection", "Failed to persist process blacklist addition.", ex);
        }
    }

    private async Task ToggleHoverIconAsync(bool enabled)
    {
        try
        {
            if (enabled && IsHoverMuted)
            {
                SetHoverMute(null);
            }

            var updated = _settingsStore.Current.Clone();
            updated.Selection.EnableHoverIcon = enabled;
            await _settingsStore.SaveAsync(updated);
        }
        catch
        {
            // Ignore transient settings save failure.
        }
    }

    private void OnPopupFixedPositionMoved(object? sender, ScreenPoint position)
    {
        var current = _settingsStore.Current;
        if (current.Window.PositionMode != WindowPositionMode.RememberFixedPosition)
        {
            return;
        }

        if (current.Window.FixedX == position.X && current.Window.FixedY == position.Y)
        {
            return;
        }

        _ = PersistWindowGeometryAsync(position.X, position.Y, null);
    }

    private void OnPopupWidthChanged(object? sender, double widthDips)
    {
        var current = _settingsStore.Current;
        if (Math.Abs(current.Window.PopupWidth - widthDips) < 4)
        {
            return;
        }

        _ = PersistWindowGeometryAsync(null, null, widthDips);
    }

    private async Task PersistWindowGeometryAsync(int? fixedX, int? fixedY, double? popupWidth)
    {
        try
        {
            var updated = _settingsStore.Current.Clone();
            if (fixedX.HasValue) updated.Window.FixedX = fixedX.Value;
            if (fixedY.HasValue) updated.Window.FixedY = fixedY.Value;
            if (popupWidth.HasValue) updated.Window.PopupWidth = popupWidth.Value;
            await _settingsStore.SaveAsync(updated);
        }
        catch
        {
            // Geometry persistence is best-effort.
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        EnqueueUi(() =>
        {
            if (_disposed)
            {
                return;
            }

            if (!ProxiesEqual(_lastAppliedProxy, settings.Proxy))
            {
                _lastAppliedProxy = settings.Proxy.Clone();
                _proxyHttpClientFactory.InvalidateProxyCache();
            }

            ApplyHotkeys(settings.Hotkeys, force: false);
            ApplyHoverAndDoubleTapConfig(settings.Selection, settings.Hotkeys);
            _settingsViewModel.Hotkeys.LoadFrom(settings);
            _settingsViewModel.General.SyncHoverIconFromExternal(settings.Selection.EnableHoverIcon);

            if (settings.Selection.EnableHoverIcon)
            {
                EnsureHoverIconWindow(settings);
            }

            _hoverIconWindow?.ApplyTheme(settings.Window.Theme);
            _popupWindow?.ApplySettings(settings);
            _settingsWindow?.ApplyTheme(settings.Window.Theme);
            _trayIconHost.UpdateState(settings, SafeIsAdmin(), IsHoverMuted, _isClipboardListenerEnabled);
        });
    }

    private FloatingHoverIconWindow EnsureHoverIconWindow(AppSettings? settings = null)
    {
        if (_hoverIconWindow is not null)
        {
            return _hoverIconWindow;
        }

        var activeSettings = settings ?? _settingsStore.Current;
        var hoverWindow = _serviceProvider.GetRequiredService<FloatingHoverIconWindow>();
        hoverWindow.ApplyTheme(activeSettings.Window.Theme);
        hoverWindow.PreWarm();
        hoverWindow.IconClicked += OnHoverIconClicked;
        hoverWindow.ReplaceTranslateClicked += OnHoverReplaceTranslateClicked;
        hoverWindow.BlacklistProcessRequested += (_, processName) => PostUi(() => _ = AddProcessToBlacklistAsync(processName));
        hoverWindow.MuteHoverRequested += (_, duration) => PostUi(() => SetHoverMute(duration));
        hoverWindow.DisableHoverIconRequested += (_, _) => PostUi(() => _ = ToggleHoverIconAsync(enabled: false));
        hoverWindow.SettingsRequested += (_, _) => PostUi(() => _ = OpenSettingsAsync());
        hoverWindow.ExitRequested += (_, _) => PostUi(RequestExit);
        hoverWindow.VisibilityChanged += OnHoverIconVisibilityChanged;
        _selectionHoverMonitor.RegisterIgnoredWindow(hoverWindow.WindowHandle);
        _selectionHoverMonitor.SetHoverIconVisible(hoverWindow.IsIconVisible);

        _hoverIconWindow = hoverWindow;
        return hoverWindow;
    }

    private void OnHoverIconVisibilityChanged(object? sender, bool isVisible)
    {
        if (!_disposed)
        {
            _selectionHoverMonitor.SetHoverIconVisible(isVisible);
        }
    }

    private void OnSettingsWindowExitRequested(object? sender, EventArgs e)
    {
        PostUi(RequestExit);
    }

    private void OnSettingsWindowClosed(object? sender, EventArgs e)
    {
        if (sender is SettingsWindow closedWindow)
        {
            closedWindow.ExitRequested -= OnSettingsWindowExitRequested;
            closedWindow.WindowClosed -= OnSettingsWindowClosed;

            if (closedWindow.WindowHandle != 0)
            {
                try
                {
                    _selectionHoverMonitor.UnregisterIgnoredWindow(closedWindow.WindowHandle);
                }
                catch
                {
                    // Ignore teardown races if hover monitor is already disposed.
                }
            }

            if (ReferenceEquals(_settingsWindow, closedWindow))
            {
                _settingsWindow = null;
            }
        }
        else
        {
            _settingsWindow = null;
        }

        if (!_disposed && !_isExiting)
        {
            ScheduleSettingsCloseMemoryTrim();
        }
    }

    private void OnPopupShown(object? sender, EventArgs e)
    {
        _idleCleanupTimer?.Stop();
    }

    private void OnPopupPinStateChanged(object? sender, bool isPinned)
    {
        if (isPinned)
        {
            _idleCleanupTimer?.Stop();
        }
    }

    private void OnPopupHidden(object? sender, EventArgs e)
    {
        if (_disposed || _isExiting || _uiDispatcher is null || _popupViewModel.IsPinned)
        {
            return;
        }

        _idleCleanupTimer ??= CreateOneShotTimer(_uiDispatcher, PopupIdleCleanupDelay, OnIdleCleanupTimerTick);
        _idleCleanupTimer.Stop();
        _idleCleanupTimer.Interval = PopupIdleCleanupDelay;
        _idleCleanupTimer.Start();
    }

    private void ScheduleSettingsCloseMemoryTrim()
    {
        if (_uiDispatcher is null || _disposed || _isExiting)
        {
            return;
        }

        _settingsTrimTimer ??= CreateOneShotTimer(_uiDispatcher, SettingsCloseTrimDelay, OnSettingsTrimTimerTick);
        _settingsTrimTimer.Stop();
        _settingsTrimTimer.Interval = SettingsCloseTrimDelay;
        _settingsTrimTimer.Start();
    }

    private void OnIdleCleanupTimerTick()
    {
        if (_disposed || _isExiting || _popupViewModel.IsPinned || (_popupWindow?.IsPopupVisible ?? false))
        {
            return;
        }

        _popupViewModel.ReleaseIdleMemory();
        PerformLightweightMemoryTrim();
    }

    private void OnSettingsTrimTimerTick()
    {
        if (_disposed || _isExiting || _settingsWindow is not null)
        {
            return;
        }

        PerformLightweightMemoryTrim();
    }

    private void PerformLightweightMemoryTrim()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
            _windowHelperService.TrimProcessWorkingSet();
        }
        catch
        {
            // Idle memory trimming is best-effort and must never disturb tray residency.
        }
    }

    private static DispatcherQueueTimer CreateOneShotTimer(
        DispatcherQueue dispatcher,
        TimeSpan interval,
        Action onTick)
    {
        var timer = dispatcher.CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = interval;
        timer.Tick += (_, _) => onTick();
        return timer;
    }

    private void ApplyHotkeys(HotkeyConfig config, bool force)
    {
        if (!force && HotkeysEqual(_lastAppliedHotkeys, config))
        {
            return;
        }

        _lastAppliedHotkeys = config.Clone();
        _lastHotkeyResults = _globalHotkeyService.ApplyHotkeys(config);
        _settingsViewModel.UpdateHotkeyRegistrationResults(_lastHotkeyResults);
    }

    private void ApplyHoverAndDoubleTapConfig(SelectionConfig selectionConfig, HotkeyConfig hotkeyConfig)
    {
        _selectionHoverMonitor.SetDoubleTapCopyEnabled(hotkeyConfig.EnableDoubleTapCopy);

        var shouldRunMonitor = (selectionConfig.EnableHoverIcon && !IsHoverMuted) || hotkeyConfig.EnableDoubleTapCopy;
        if (!selectionConfig.EnableHoverIcon || IsHoverMuted)
        {
            _hoverIconWindow?.HideIcon();
        }

        if (shouldRunMonitor)
        {
            if (_selectionHoverMonitor.IsRunning)
            {
                _selectionHoverMonitor.UpdateConfig(selectionConfig);
            }
            else
            {
                _selectionHoverMonitor.Start(selectionConfig);
            }
        }
        else
        {
            if (_selectionHoverMonitor.IsRunning)
            {
                _selectionHoverMonitor.Stop();
            }
        }
    }

    private nint SanitizeSourceWindowHandle(nint hwnd) =>
        hwnd == 0 || IsOwnWindow(hwnd) ? 0 : hwnd;

    private bool IsOwnWindow(nint hwnd) =>
        hwnd != 0 &&
        (hwnd == _popupWindow?.WindowHandle ||
         hwnd == _hoverIconWindow?.WindowHandle ||
         hwnd == _settingsWindow?.WindowHandle);

    private static bool HotkeysEqual(HotkeyConfig? left, HotkeyConfig right) =>
        left is not null &&
        left.SelectionTranslate == right.SelectionTranslate &&
        left.ReplaceTranslate == right.ReplaceTranslate &&
        left.ScreenshotTranslate == right.ScreenshotTranslate &&
        left.SelectionPolish == right.SelectionPolish &&
        left.SilentOcr == right.SilentOcr &&
        left.EnableDoubleTapCopy == right.EnableDoubleTapCopy;

    private static bool ProxiesEqual(ProxyConfig? left, ProxyConfig right) =>
        left is not null &&
        left.Mode == right.Mode &&
        string.Equals(left.Host, right.Host, StringComparison.Ordinal) &&
        left.Port == right.Port &&
        string.Equals(left.Username, right.Username, StringComparison.Ordinal) &&
        string.Equals(left.EncryptedPassword, right.EncryptedPassword, StringComparison.Ordinal) &&
        left.BypassLocalAddresses == right.BypassLocalAddresses;

    private bool SafeIsAdmin()
    {
        try
        {
            return _startupPrivilegeService.IsRunningAsAdministrator();
        }
        catch
        {
            return false;
        }
    }

    private void EnqueueUi(Action action)
    {
        if (_uiDispatcher is null || _uiDispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            _uiDispatcher.TryEnqueue(() => action());
        }
    }

    private void PostUi(Action action)
    {
        if (_uiDispatcher is not null)
        {
            _uiDispatcher.TryEnqueue(() => action());
        }
        else
        {
            action();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hoverMuteExpiryTimer?.Stop();
        _idleCleanupTimer?.Stop();
        _settingsTrimTimer?.Stop();
        try { _replaceTranslateCts?.Cancel(); _replaceTranslateCts?.Dispose(); } catch { }
        _replaceTranslateCts = null;

        if (_isClipboardListenerEnabled)
        {
            try { Clipboard.ContentChanged -= OnSystemClipboardContentChanged; } catch { }
            _isClipboardListenerEnabled = false;
        }

        if (_popupWindow is not null)
        {
            _popupWindow.PopupShown -= OnPopupShown;
            _popupWindow.PopupHidden -= OnPopupHidden;
        }

        _popupViewModel.PinStateChanged -= OnPopupPinStateChanged;

        if (_hoverIconWindow is not null)
        {
            _hoverIconWindow.VisibilityChanged -= OnHoverIconVisibilityChanged;
        }

        if (_settingsWindow is not null)
        {
            _settingsWindow.ExitRequested -= OnSettingsWindowExitRequested;
            _settingsWindow.WindowClosed -= OnSettingsWindowClosed;
        }

        _settingsStore.SettingsChanged -= OnSettingsChanged;
        _globalHotkeyService.HotkeyPressed -= OnGlobalHotkeyPressed;
        _selectionHoverMonitor.SelectionDetected -= OnSelectionHoverDetected;
        _selectionHoverMonitor.DismissRequested -= OnSelectionHoverDismissRequested;
        _selectionHoverMonitor.DoubleTapCopyDetected -= OnDoubleTapCopyDetected;
        _singleInstanceGuard.SecondInstanceArgumentsReceived -= OnSecondInstanceArgumentsReceived;

        _popupViewModel.CancelActiveTranslation();

        try { _screenshotCaptureService.CancelActiveCapture(); } catch { }
        try { _globalHotkeyService.UnregisterAll(); } catch { }
        try { _globalHotkeyService.Dispose(); } catch { }
        try { _selectionHoverMonitor.Stop(); } catch { }
        try { _selectionHoverMonitor.Dispose(); } catch { }
        try { _trayIconHost.Dispose(); } catch { }

        CloseWindowCleanly(_hoverIconWindow);
        CloseWindowCleanly(_popupWindow);
        CloseWindowCleanly(_settingsWindow);

        _singleInstanceGuard.Dispose();
    }

    private static void CloseWindowCleanly(Window? window)
    {
        if (window is null)
        {
            return;
        }

        try
        {
            switch (window)
            {
                case FloatingHoverIconWindow hover:
                    hover.AllowClose = true;
                    break;
                case TranslationPopupWindow popup:
                    popup.AllowClose = true;
                    break;
                case SettingsWindow settings:
                    settings.AllowClose = true;
                    break;
            }

            window.Close();
        }
        catch
        {
            // Ignore shutdown window teardown races.
        }
    }
}
