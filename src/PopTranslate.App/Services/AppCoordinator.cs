using PopTranslate.App.ViewModels;
using PopTranslate.App.Windows;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace PopTranslate.App.Services;

/// <summary>
/// Central UI-thread orchestrator connecting global hotkeys, zero-invasive hover icon,
/// screenshot OCR, parallel translation popup, Mica settings window, and tray residency.
/// </summary>
public sealed class AppCoordinator : IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISettingsStore _settingsStore;
    private readonly IGlobalHotkeyService _globalHotkeyService;
    private readonly ISelectionHoverMonitor _selectionHoverMonitor;
    private readonly ITextSelectionService _textSelectionService;
    private readonly IScreenshotCaptureService _screenshotCaptureService;
    private readonly IOcrOrchestrator _ocrOrchestrator;
    private readonly IProxyHttpClientFactory _proxyHttpClientFactory;
    private readonly IWindowHelperService _windowHelperService;
    private readonly IStartupPrivilegeService _startupPrivilegeService;
    private readonly SingleInstanceGuard _singleInstanceGuard;
    private readonly TrayIconHost _trayIconHost;
    private readonly TranslationPopupViewModel _popupViewModel;
    private readonly SettingsViewModel _settingsViewModel;

    private DispatcherQueue? _uiDispatcher;
    private FloatingHoverIconWindow? _hoverIconWindow;
    private TranslationPopupWindow? _popupWindow;
    private SettingsWindow? _settingsWindow;

    private HoverSelectionEventArgs? _pendingHoverSelection;
    private HotkeyConfig? _lastAppliedHotkeys;
    private ProxyConfig? _lastAppliedProxy;
    private IReadOnlyDictionary<HotkeyAction, HotkeyRegistrationResult> _lastHotkeyResults =
        new Dictionary<HotkeyAction, HotkeyRegistrationResult>();

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
        _startupPrivilegeService = startupPrivilegeService;
        _singleInstanceGuard = singleInstanceGuard;
        _trayIconHost = trayIconHost;
        _popupViewModel = popupViewModel;
        _settingsViewModel = settingsViewModel;
    }

    public async Task InitializeAsync(DispatcherQueue uiDispatcher)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
        {
            return;
        }

        _uiDispatcher = uiDispatcher;
        _initialized = true;

        var settings = await _settingsStore.LoadAsync();
        var isAdmin = SafeIsAdmin();

        // Pre-warm hover icon and Desktop Acrylic translation popup hidden for instant (<30ms) display.
        _hoverIconWindow = _serviceProvider.GetRequiredService<FloatingHoverIconWindow>();
        _hoverIconWindow.ApplyTheme(settings.Window.Theme);
        _hoverIconWindow.PreWarm();
        _hoverIconWindow.IconClicked += OnHoverIconClicked;
        _selectionHoverMonitor.RegisterIgnoredWindow(_hoverIconWindow.WindowHandle);

        _popupWindow = _serviceProvider.GetRequiredService<TranslationPopupWindow>();
        _popupWindow.PreWarm(settings);
        _popupWindow.SettingsRequested += (_, pageTag) => _ = OpenSettingsAsync(pageTag);
        _popupWindow.ScreenshotRequested += (_, _) => _ = TriggerScreenshotTranslationAsync();
        _popupWindow.FixedPositionMoved += OnPopupFixedPositionMoved;
        _popupWindow.PopupWidthChanged += OnPopupWidthChanged;
        _selectionHoverMonitor.RegisterIgnoredWindow(_popupWindow.WindowHandle);

        // Wire SettingsViewModel privilege restart request.
        _settingsViewModel.RestartAsAdministratorRequested += (_, _) => RestartAsAdministrator();

        // Wire persistent Windows 11 system tray icon.
        _trayIconHost.InputTranslateRequested += (_, _) => EnqueueUi(OpenInputTranslation);
        _trayIconHost.PolishInputRequested += (_, _) => EnqueueUi(OpenInputPolish);
        _trayIconHost.ScreenshotTranslateRequested += (_, _) => EnqueueUi(() => _ = TriggerScreenshotTranslationAsync());
        _trayIconHost.HoverIconToggled += (_, enabled) => EnqueueUi(() => _ = ToggleHoverIconAsync(enabled));
        _trayIconHost.SettingsRequested += (_, _) => EnqueueUi(() => _ = OpenSettingsAsync());
        _trayIconHost.RestartAsAdministratorRequested += (_, _) => EnqueueUi(RestartAsAdministrator);
        _trayIconHost.ExitRequested += (_, _) => EnqueueUi(RequestExit);
        _trayIconHost.Initialize(settings, isAdmin, _uiDispatcher);

        // Wire OS services.
        _globalHotkeyService.HotkeyPressed += OnGlobalHotkeyPressed;
        _selectionHoverMonitor.SelectionDetected += OnSelectionHoverDetected;
        _selectionHoverMonitor.DismissRequested += OnSelectionHoverDismissRequested;
        _settingsStore.SettingsChanged += OnSettingsChanged;
        _singleInstanceGuard.SecondInstanceSignaled += (_, _) => EnqueueUi(OpenInputTranslation);

        // Apply initial hotkeys, proxy baseline, and hover monitor.
        _lastAppliedProxy = settings.Proxy.Clone();
        ApplyHotkeys(settings.Hotkeys, force: true);
        ApplyHoverMonitorConfig(settings.Selection);
    }

    public void OpenInputTranslation()
    {
        if (_disposed || _popupWindow is null)
        {
            return;
        }

        _hoverIconWindow?.HideIcon();
        _popupViewModel.PrepareForInput();
        var cursor = _windowHelperService.GetCursorPosition();
        _popupWindow.ShowPopup(cursor, focusSourceInput: true);
    }

    public void OpenInputPolish()
    {
        if (_disposed || _popupWindow is null)
        {
            return;
        }

        _hoverIconWindow?.HideIcon();
        _popupViewModel.PrepareForPolishInput();
        var cursor = _windowHelperService.GetCursorPosition();
        _popupWindow.ShowPopup(cursor, focusSourceInput: true);
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

    public async Task TriggerScreenshotTranslationAsync()
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
            _popupWindow.ShowPopup(_windowHelperService.GetCursorPosition(), focusSourceInput: false);
            _popupViewModel.ShowStatusError($"启动截图选区失败：{ex.Message}");
            return;
        }

        if (screenshot is null || screenshot.PngBytes.Length == 0)
        {
            return;
        }

        var anchor = new ScreenPoint(screenshot.CaptureBounds.Right, screenshot.CaptureBounds.Bottom);
        _popupViewModel.BeginOcrCapture();
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
                OpenInputTranslation();
                break;

            case HotkeyAction.ScreenshotTranslate:
                await TriggerScreenshotTranslationAsync();
                break;

            case HotkeyAction.SelectionPolish:
                await HandleSelectionPolishHotkeyAsync(args);
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

    private void OnSelectionHoverDetected(object? sender, HoverSelectionEventArgs e)
    {
        EnqueueUi(() =>
        {
            if (_disposed || _hoverIconWindow is null || _screenshotCaptureService.IsCaptureActive)
            {
                return;
            }

            if (!_settingsStore.Current.Selection.EnableHoverIcon)
            {
                return;
            }

            _pendingHoverSelection = e;
            _hoverIconWindow.ShowAt(e.AnchorPoint, _settingsStore.Current.Selection.HoverIconAutoDismissMs);
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

        _ = HandleHoverIconClickAsync(pending);
    }

    private async Task HandleHoverIconClickAsync(HoverSelectionEventArgs pending)
    {
        if (_popupWindow is null)
        {
            return;
        }

        // Fast zero-clipboard path when UIA already supplied the selected preview text.
        if (pending.HasUiaText && !string.IsNullOrWhiteSpace(pending.PreviewText))
        {
            _popupViewModel.PrepareForInput();
            _popupWindow.ShowPopup(pending.AnchorPoint, focusSourceInput: false);
            await _popupViewModel.TranslateTextAsync(pending.PreviewText, isFromOcr: false, resetLanguageSelection: true);
            return;
        }

        // Fallback capture ONLY after explicit user click on the floating icon.
        try
        {
            var captured = await _textSelectionService.CaptureSelectionAsync(
                SelectionTriggerSource.HoverIcon,
                _settingsStore.Current.Selection,
                pending.SourceWindowHandle);

            if (captured.IsSuccess && !string.IsNullOrWhiteSpace(captured.Text))
            {
                _popupViewModel.PrepareForInput();
                _popupWindow.ShowPopup(pending.AnchorPoint, focusSourceInput: false);
                await _popupViewModel.TranslateTextAsync(captured.Text, isFromOcr: false, resetLanguageSelection: true);
            }
            else
            {
                _popupViewModel.PrepareForInput(
                    string.IsNullOrWhiteSpace(captured.FailureReason)
                        ? "未能从目标窗口读取选区，请直接输入或粘贴。"
                        : captured.FailureReason);
                _popupWindow.ShowPopup(pending.AnchorPoint, focusSourceInput: true);
            }
        }
        catch (Exception ex)
        {
            _popupViewModel.PrepareForInput();
            _popupWindow.ShowPopup(pending.AnchorPoint, focusSourceInput: true);
            _popupViewModel.ShowStatusError($"划词读取失败：{ex.Message}");
        }
    }

    private async Task ToggleHoverIconAsync(bool enabled)
    {
        try
        {
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
            ApplyHoverMonitorConfig(settings.Selection);

            _hoverIconWindow?.ApplyTheme(settings.Window.Theme);
            _popupWindow?.ApplySettings(settings);
            _settingsWindow?.ApplyTheme(settings.Window.Theme);
            _trayIconHost.UpdateState(settings, SafeIsAdmin());
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

    private void ApplyHoverMonitorConfig(SelectionConfig selectionConfig)
    {
        if (selectionConfig.EnableHoverIcon)
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
            _hoverIconWindow?.HideIcon();
            if (_selectionHoverMonitor.IsRunning)
            {
                _selectionHoverMonitor.Stop();
            }
        }
    }

    private static bool HotkeysEqual(HotkeyConfig? left, HotkeyConfig right) =>
        left is not null &&
        left.SelectionTranslate == right.SelectionTranslate &&
        left.InputTranslate == right.InputTranslate &&
        left.ScreenshotTranslate == right.ScreenshotTranslate &&
        left.SelectionPolish == right.SelectionPolish;

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

        _settingsStore.SettingsChanged -= OnSettingsChanged;
        _globalHotkeyService.HotkeyPressed -= OnGlobalHotkeyPressed;
        _selectionHoverMonitor.SelectionDetected -= OnSelectionHoverDetected;
        _selectionHoverMonitor.DismissRequested -= OnSelectionHoverDismissRequested;

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
