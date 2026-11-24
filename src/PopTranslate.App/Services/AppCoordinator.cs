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
/// Central UI-thread orchestrator connecting global hotkeys, dual-action hover pill,
/// screenshot/silent OCR, parallel translation &amp; polish popup, Mica settings window, and tray residency.
/// </summary>
public sealed class AppCoordinator : IDisposable
{
    private const int MaxClipboardAutoTranslateLength = 8000;

    private readonly IServiceProvider _serviceProvider;
    private readonly ISettingsStore _settingsStore;
    private readonly IGlobalHotkeyService _globalHotkeyService;
    private readonly ISelectionHoverMonitor _selectionHoverMonitor;
    private readonly ITextSelectionService _textSelectionService;
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

        // Pre-warm hover pill and Desktop Acrylic translation popup hidden for instant (<30ms) display.
        _hoverIconWindow = _serviceProvider.GetRequiredService<FloatingHoverIconWindow>();
        _hoverIconWindow.ApplyTheme(settings.Window.Theme);
        _hoverIconWindow.PreWarm();
        _hoverIconWindow.TranslateClicked += OnHoverTranslateClicked;
        _hoverIconWindow.PolishClicked += OnHoverPolishClicked;
        _hoverIconWindow.BlacklistProcessRequested += (_, processName) => EnqueueUi(() => _ = AddProcessToBlacklistAsync(processName));
        _hoverIconWindow.MuteHoverRequested += (_, duration) => EnqueueUi(() => SetHoverMute(duration));
        _hoverIconWindow.DisableHoverIconRequested += (_, _) => EnqueueUi(() => _ = ToggleHoverIconAsync(enabled: false));
        _selectionHoverMonitor.RegisterIgnoredWindow(_hoverIconWindow.WindowHandle);

        _popupWindow = _serviceProvider.GetRequiredService<TranslationPopupWindow>();
        _popupWindow.PreWarm(settings);
        _popupWindow.SettingsRequested += (_, pageTag) => _ = OpenSettingsAsync(pageTag);
        _popupWindow.ScreenshotRequested += (_, _) => _ = TriggerScreenshotTranslationAsync(forceSilentCopyOnly: false);
        _popupWindow.FixedPositionMoved += OnPopupFixedPositionMoved;
        _popupWindow.PopupWidthChanged += OnPopupWidthChanged;
        _selectionHoverMonitor.RegisterIgnoredWindow(_popupWindow.WindowHandle);

        // Wire SettingsViewModel privilege restart request.
        _settingsViewModel.RestartAsAdministratorRequested += (_, _) => RestartAsAdministrator();

        // Wire persistent Windows 11 system tray icon.
        _trayIconHost.InputTranslateRequested += (_, _) => EnqueueUi(() => OpenInputTranslation());
        _trayIconHost.PolishInputRequested += (_, _) => EnqueueUi(() => OpenInputPolish());
        _trayIconHost.ScreenshotTranslateRequested += (_, _) => EnqueueUi(() => _ = TriggerScreenshotTranslationAsync(forceSilentCopyOnly: false));
        _trayIconHost.SilentOcrRequested += (_, _) => EnqueueUi(() => _ = TriggerScreenshotTranslationAsync(forceSilentCopyOnly: true));
        _trayIconHost.HoverIconToggled += (_, enabled) => EnqueueUi(() => _ = ToggleHoverIconAsync(enabled));
        _trayIconHost.HoverMuteToggled += (_, muted) => EnqueueUi(() => SetHoverMute(muted ? TimeSpan.FromMinutes(30) : null));
        _trayIconHost.ClipboardListenerToggled += (_, enabled) => EnqueueUi(() => SetClipboardListenerEnabled(enabled));
        _trayIconHost.SettingsRequested += (_, _) => EnqueueUi(() => _ = OpenSettingsAsync());
        _trayIconHost.RestartAsAdministratorRequested += (_, _) => EnqueueUi(RestartAsAdministrator);
        _trayIconHost.ExitRequested += (_, _) => EnqueueUi(RequestExit);
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

    public void OpenInputTranslation(string? initialText = null, nint? sourceWindowHandle = null)
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
        _popupViewModel.PrepareForInput();

        var cursor = _windowHelperService.GetCursorPosition();
        if (!string.IsNullOrWhiteSpace(initialText))
        {
            _popupWindow.ShowPopup(cursor, focusSourceInput: false);
            _ = _popupViewModel.TranslateTextAsync(initialText, isFromOcr: false, resetLanguageSelection: true);
        }
        else
        {
            _popupWindow.ShowPopup(cursor, focusSourceInput: true);
        }
    }

    public void OpenInputPolish(string? initialText = null, nint? sourceWindowHandle = null)
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
        _popupViewModel.PrepareForPolishInput();

        var cursor = _windowHelperService.GetCursorPosition();
        if (!string.IsNullOrWhiteSpace(initialText))
        {
            _popupWindow.ShowPopup(cursor, focusSourceInput: false);
            _ = _popupViewModel.PolishTextAsync(initialText, resetStyleToDefault: true);
        }
        else
        {
            _popupWindow.ShowPopup(cursor, focusSourceInput: true);
        }
    }

    public async Task OpenSettingsAsync(string? initialPageTag = null)
    {
        if (_disposed)
        {
            return;
        }

        if (_settingsWindow is null)
        {
            _settingsWindow = _serviceProvider.GetRequiredService<SettingsWindow>();
            _settingsWindow.ApplyTheme(_settingsStore.Current.Window.Theme);
            _selectionHoverMonitor.RegisterIgnoredWindow(_settingsWindow.WindowHandle);
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

        _hoverIconWindow?.HideIcon();
        _popupWindow.HidePopup(cancelTranslations: true);

        // Allow DWM to finish hiding our popup frame before taking the frozen monitor snapshot.
        await Task.Delay(80);

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
            if (ocrResult.IsSuccess && !string.IsNullOrWhiteSpace(ocrResult.Text))
            {
                await _popupViewModel.TranslateTextAsync(ocrResult.Text, isFromOcr: true, resetLanguageSelection: true);
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

        var exePath = Environment.ProcessPath;
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
        }
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

        // Default secondary interactive launch with no CLI arguments: open Input Translation popup.
        OpenInputTranslation();
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
                if (normalized is "translate" or "polish" or "ocr" or "silent-ocr" or "silentocr" or "input" or "settings")
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
                    await HandleSelectionTranslateHotkeyAsync(
                        new HotkeyPressedEventArgs(HotkeyAction.SelectionTranslate, HotkeyGesture.Empty, cursor, fgHwnd));
                }
                return true;

            case "polish":
                if (!string.IsNullOrWhiteSpace(trailingText))
                {
                    OpenInputPolish(trailingText, fgHwnd);
                }
                else
                {
                    await HandleSelectionPolishHotkeyAsync(
                        new HotkeyPressedEventArgs(HotkeyAction.SelectionPolish, HotkeyGesture.Empty, cursor, fgHwnd));
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
                await HandleSelectionTranslateHotkeyAsync(args);
                break;

            case HotkeyAction.InputTranslate:
                OpenInputTranslation(sourceWindowHandle: args.ForegroundWindowHandle);
                break;

            case HotkeyAction.ScreenshotTranslate:
                await TriggerScreenshotTranslationAsync(forceSilentCopyOnly: false);
                break;

            case HotkeyAction.SelectionPolish:
                await HandleSelectionPolishHotkeyAsync(args);
                break;

            case HotkeyAction.SilentOcr:
                await TriggerScreenshotTranslationAsync(forceSilentCopyOnly: true);
                break;
        }
    }

    private async Task HandleSelectionTranslateHotkeyAsync(HotkeyPressedEventArgs args)
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
                _popupViewModel.PrepareForInput();
                _popupWindow.ShowPopup(args.CursorPosition, focusSourceInput: false);
                await _popupViewModel.TranslateTextAsync(captured.Text, isFromOcr: false, resetLanguageSelection: true);
            }
            else
            {
                _popupViewModel.PrepareForInput(
                    string.IsNullOrWhiteSpace(captured.FailureReason)
                        ? "未读取到选中文本，可直接输入或粘贴翻译。"
                        : captured.FailureReason);
                _popupWindow.ShowPopup(args.CursorPosition, focusSourceInput: true);
            }
        }
        catch (Exception ex)
        {
            _popupViewModel.PrepareForInput();
            _popupWindow.ShowPopup(args.CursorPosition, focusSourceInput: true);
            _popupViewModel.ShowStatusError($"取词失败：{ex.Message}");
        }
    }

    private async Task HandleSelectionPolishHotkeyAsync(HotkeyPressedEventArgs args)
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
                _popupViewModel.PrepareForPolishInput();
                _popupWindow.ShowPopup(args.CursorPosition, focusSourceInput: false);
                await _popupViewModel.PolishTextAsync(captured.Text, resetStyleToDefault: true);
            }
            else
            {
                _popupViewModel.PrepareForPolishInput(
                    string.IsNullOrWhiteSpace(captured.FailureReason)
                        ? "未读取到选中文本，可直接输入或粘贴润色。"
                        : captured.FailureReason);
                _popupWindow.ShowPopup(args.CursorPosition, focusSourceInput: true);
            }
        }
        catch (Exception ex)
        {
            _popupViewModel.PrepareForPolishInput();
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
        _popupViewModel.PrepareForInput();
        _popupWindow.ShowPopup(cursor, focusSourceInput: false);
        await _popupViewModel.TranslateTextAsync(text, isFromOcr: false, resetLanguageSelection: true);
    }

    private void OnSelectionHoverDetected(object? sender, HoverSelectionEventArgs e)
    {
        EnqueueUi(() =>
        {
            if (_disposed || _hoverIconWindow is null || _screenshotCaptureService.IsCaptureActive)
            {
                return;
            }

            if (!_settingsStore.Current.Selection.EnableHoverIcon || IsHoverMuted)
            {
                return;
            }

            _pendingHoverSelection = e;
            var processName = !string.IsNullOrWhiteSpace(e.SourceProcessName)
                ? e.SourceProcessName
                : _foregroundAppGuard.GetProcessNameForWindow(e.SourceWindowHandle);

            _hoverIconWindow.ShowAt(
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

    private void OnHoverTranslateClicked(object? sender, EventArgs e)
    {
        var pending = _pendingHoverSelection;
        _pendingHoverSelection = null;

        if (pending is null || _popupWindow is null)
        {
            return;
        }

        _ = HandleHoverActionAsync(pending, polishMode: false);
    }

    private void OnHoverPolishClicked(object? sender, EventArgs e)
    {
        var pending = _pendingHoverSelection;
        _pendingHoverSelection = null;

        if (pending is null || _popupWindow is null)
        {
            return;
        }

        _ = HandleHoverActionAsync(pending, polishMode: true);
    }

    private async Task HandleHoverActionAsync(HoverSelectionEventArgs pending, bool polishMode)
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
            if (polishMode)
            {
                _popupViewModel.PrepareForPolishInput();
                _popupWindow.ShowPopup(pending.AnchorPoint, focusSourceInput: false);
                await _popupViewModel.PolishTextAsync(pending.PreviewText, resetStyleToDefault: true);
            }
            else
            {
                _popupViewModel.PrepareForInput();
                _popupWindow.ShowPopup(pending.AnchorPoint, focusSourceInput: false);
                await _popupViewModel.TranslateTextAsync(pending.PreviewText, isFromOcr: false, resetLanguageSelection: true);
            }

            return;
        }

        ClipboardLoopGuard.SuppressFor(TimeSpan.FromMilliseconds(900));

        // Fallback capture ONLY after explicit user click on the floating pill.
        try
        {
            var captured = await _textSelectionService.CaptureSelectionAsync(
                SelectionTriggerSource.HoverIcon,
                _settingsStore.Current.Selection,
                pending.SourceWindowHandle);

            if (captured.IsSuccess && !string.IsNullOrWhiteSpace(captured.Text))
            {
                if (polishMode)
                {
                    _popupViewModel.PrepareForPolishInput();
                    _popupWindow.ShowPopup(pending.AnchorPoint, focusSourceInput: false);
                    await _popupViewModel.PolishTextAsync(captured.Text, resetStyleToDefault: true);
                }
                else
                {
                    _popupViewModel.PrepareForInput();
                    _popupWindow.ShowPopup(pending.AnchorPoint, focusSourceInput: false);
                    await _popupViewModel.TranslateTextAsync(captured.Text, isFromOcr: false, resetLanguageSelection: true);
                }
            }
            else
            {
                var fallbackMsg = string.IsNullOrWhiteSpace(captured.FailureReason)
                    ? "未能从目标窗口读取选区，请直接输入或粘贴。"
                    : captured.FailureReason;
                if (polishMode)
                {
                    _popupViewModel.PrepareForPolishInput(fallbackMsg);
                }
                else
                {
                    _popupViewModel.PrepareForInput(fallbackMsg);
                }

                _popupWindow.ShowPopup(pending.AnchorPoint, focusSourceInput: true);
            }
        }
        catch (Exception ex)
        {
            if (polishMode)
            {
                _popupViewModel.PrepareForPolishInput();
            }
            else
            {
                _popupViewModel.PrepareForInput();
            }

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
        _popupViewModel.PrepareForInput();
        _popupWindow.ShowPopup(cursor, focusSourceInput: false);
        await _popupViewModel.TranslateTextAsync(text, isFromOcr: false, resetLanguageSelection: true);
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

            _hoverIconWindow?.ApplyTheme(settings.Window.Theme);
            _popupWindow?.ApplySettings(settings);
            _settingsWindow?.ApplyTheme(settings.Window.Theme);
            _trayIconHost.UpdateState(settings, SafeIsAdmin(), IsHoverMuted, _isClipboardListenerEnabled);
        });
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
        left.InputTranslate == right.InputTranslate &&
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hoverMuteExpiryTimer?.Stop();

        if (_isClipboardListenerEnabled)
        {
            try { Clipboard.ContentChanged -= OnSystemClipboardContentChanged; } catch { }
            _isClipboardListenerEnabled = false;
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
