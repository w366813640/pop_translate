using PopTranslate.App.Services;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using WinRT.Interop;

namespace PopTranslate.App.Windows;

public sealed partial class FloatingHoverIconWindow : Window
{
    public const double IconWidthDips = 32;
    public const double IconHeightDips = 32;
    private const int MenuCommandReplaceTranslate = 10;
    private const int MenuCommandBlacklistProcess = 1;
    private const int MenuCommandMuteHover30Min = 2;
    private const int MenuCommandDisableHoverIcon = 3;
    private const int MenuCommandOpenSettings = 4;
    private const int MenuCommandExitApp = 5;

    private readonly IWindowHelperService _windowHelperService;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _autoHideTimer;
    private readonly nint _hwnd;
    private string? _sourceProcessName;
    private bool _isPointerHovering;
    private bool _isContextMenuOpen;
    private long _lastContextMenuClosedTick;
    private bool _isVisible;

    public FloatingHoverIconWindow(IWindowHelperService windowHelperService)
    {
        _windowHelperService = windowHelperService;
        InitializeComponent();

        SystemBackdrop = new DesktopAcrylicBackdrop();
        TryApplyNativeIcon();

        _hwnd = WindowNative.GetWindowHandle(this);
        ConfigureWindowChrome();

        // Single unified right-button release listener (even when inner Button marks pointer events handled)
        // to avoid duplicate RightTapped + PointerReleased double-popups.
        RootGrid.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(RootGrid_PointerReleased),
            handledEventsToo: true);

        _autoHideTimer = DispatcherQueue.CreateTimer();
        _autoHideTimer.IsRepeating = false;
        _autoHideTimer.Tick += (_, _) =>
        {
            if (!_isPointerHovering && !_isContextMenuOpen)
            {
                HideIcon();
            }
        };

        AppWindow.Closing += (_, args) =>
        {
            if (!AllowClose)
            {
                args.Cancel = true;
                HideIcon();
            }
        };

        AppWindow.Hide();
    }

    public event EventHandler? IconClicked;
    public event EventHandler? TranslateClicked;
    public event EventHandler? ReplaceTranslateClicked;
    public event EventHandler<string>? BlacklistProcessRequested;
    public event EventHandler<TimeSpan>? MuteHoverRequested;
    public event EventHandler? DisableHoverIconRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;
    public new event EventHandler<bool>? VisibilityChanged;

    public bool AllowClose { get; set; }

    public nint WindowHandle => _hwnd;

    public bool IsIconVisible => _isVisible && AppWindow.IsVisible;

    public static IReadOnlyList<NativeContextMenuItem> BuildContextMenuItems(string? sourceProcessName)
    {
        var normalized = NormalizeProcessDisplayName(sourceProcessName);
        var blacklistItem = !string.IsNullOrWhiteSpace(normalized)
            ? new NativeContextMenuItem(MenuCommandBlacklistProcess, $"屏蔽当前应用 ({normalized})", IsEnabled: true)
            : new NativeContextMenuItem(MenuCommandBlacklistProcess, "屏蔽当前应用", IsEnabled: false);

        return
        [
            new NativeContextMenuItem(MenuCommandReplaceTranslate, "替换翻译选中文本", IsEnabled: true),
            new NativeContextMenuItem(0, IsSeparator: true),
            blacklistItem,
            new NativeContextMenuItem(MenuCommandMuteHover30Min, "暂停划词 30 分钟", IsEnabled: true),
            new NativeContextMenuItem(MenuCommandDisableHoverIcon, "关闭划词悬浮图标", IsEnabled: true),
            new NativeContextMenuItem(0, IsSeparator: true),
            new NativeContextMenuItem(MenuCommandOpenSettings, "打开设置...", IsEnabled: true),
            new NativeContextMenuItem(MenuCommandExitApp, "退出程序", IsEnabled: true),
        ];
    }

    public static (int WidthPx, int HeightPx) CalculatePhysicalSize(double dpiScale)
    {
        var safeScale = dpiScale > 0 ? dpiScale : 1.0;
        var widthPx = Math.Max(24, (int)Math.Ceiling(IconWidthDips * safeScale));
        var heightPx = Math.Max(24, (int)Math.Ceiling(IconHeightDips * safeScale));
        return (widthPx, heightPx);
    }

    public void ApplyTheme(AppThemeMode mode) => AppAssetHelper.ApplyTheme(this, mode);

    public void PreWarm()
    {
        ConfigureWindowChrome();
        var scale = _windowHelperService.GetDpiScaleForWindow(_hwnd);
        var (widthPx, heightPx) = CalculatePhysicalSize(scale);
        _windowHelperService.MoveAndResizeWindow(_hwnd, 0, 0, widthPx, heightPx, activate: false);
        AppWindow.Hide();
        _isVisible = false;
    }

    public void ShowAt(ScreenPoint anchorPoint, int autoDismissMs, string? sourceProcessName = null)
    {
        _sourceProcessName = NormalizeProcessDisplayName(sourceProcessName);

        var monitor = _windowHelperService.GetMonitorAtPoint(anchorPoint);
        var scale = monitor.DpiScale > 0 ? monitor.DpiScale : 1.0;

        var (widthPx, heightPx) = CalculatePhysicalSize(scale);
        var cursorOffsetPx = Math.Max(8, (int)Math.Round(12 * scale));

        var pos = _windowHelperService.CalculateClampedWindowPosition(
            anchorPoint,
            widthPx,
            heightPx,
            cursorOffsetPx: cursorOffsetPx);

        _windowHelperService.ConfigureNoActivateOverlayWindow(_hwnd);
        _windowHelperService.MoveAndResizeWindow(_hwnd, pos.X, pos.Y, widthPx, heightPx, activate: false);
        _windowHelperService.ShowWindowNoActivate(_hwnd);

        _isVisible = true;
        VisibilityChanged?.Invoke(this, true);
        RestartAutoHideTimer(autoDismissMs);
    }

    public void HideIcon()
    {
        _autoHideTimer.Stop();
        _isPointerHovering = false;
        _isContextMenuOpen = false;

        if (!_isVisible && !AppWindow.IsVisible)
        {
            return;
        }

        _isVisible = false;
        AppWindow.Hide();
        VisibilityChanged?.Invoke(this, false);
    }

    private static string? NormalizeProcessDisplayName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        var trimmed = Path.GetFileName(processName.Trim());
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"{trimmed}.exe";
    }

    private void TryApplyNativeIcon()
    {
        try
        {
            var iconPath = AppAssetHelper.IconPath;
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }
        }
        catch
        {
            // Never fail hover icon creation if icon loading is unavailable.
        }
    }

    private void ConfigureWindowChrome()
    {
        try
        {
            AppWindow.IsShownInSwitchers = false;
        }
        catch
        {
            // Ignore Shell_TrayWnd / ITaskbarList unavailability.
        }

        var presenter = OverlappedPresenter.CreateForContextMenu();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);

        _windowHelperService.ConfigureNoActivateOverlayWindow(_hwnd);
    }

    private void RestartAutoHideTimer(int autoDismissMs)
    {
        var clampedMs = Math.Clamp(autoDismissMs, 1500, 15000);
        _autoHideTimer.Stop();
        _autoHideTimer.Interval = TimeSpan.FromMilliseconds(clampedMs);
        _autoHideTimer.Start();
    }

    private void TranslateActionButton_Click(object sender, RoutedEventArgs e)
    {
        HideIcon();
        TranslateClicked?.Invoke(this, EventArgs.Empty);
        IconClicked?.Invoke(this, EventArgs.Empty);
    }

    private void RootGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(null);
        if (point.Properties.PointerUpdateKind == PointerUpdateKind.RightButtonReleased)
        {
            e.Handled = true;
            ShowNativeHoverContextMenu();
        }
    }

    private void ShowNativeHoverContextMenu()
    {
        if (_isContextMenuOpen || Environment.TickCount64 - _lastContextMenuClosedTick < 250)
        {
            return;
        }

        _isContextMenuOpen = true;
        _autoHideTimer.Stop();

        var processSnapshot = _sourceProcessName;
        var items = BuildContextMenuItems(processSnapshot);
        var cursorPos = _windowHelperService.GetCursorPosition();

        int selectedCommand;
        try
        {
            selectedCommand = _windowHelperService.ShowNativeContextMenu(_hwnd, cursorPos, items);
        }
        finally
        {
            _lastContextMenuClosedTick = Environment.TickCount64;
            _isContextMenuOpen = false;
        }

        switch (selectedCommand)
        {
            case MenuCommandReplaceTranslate:
                HideIcon();
                DispatcherQueue.TryEnqueue(() => ReplaceTranslateClicked?.Invoke(this, EventArgs.Empty));
                break;

            case MenuCommandBlacklistProcess:
                HideIcon();
                if (!string.IsNullOrWhiteSpace(processSnapshot))
                {
                    DispatcherQueue.TryEnqueue(() => BlacklistProcessRequested?.Invoke(this, processSnapshot));
                }
                break;

            case MenuCommandMuteHover30Min:
                HideIcon();
                DispatcherQueue.TryEnqueue(() => MuteHoverRequested?.Invoke(this, TimeSpan.FromMinutes(30)));
                break;

            case MenuCommandDisableHoverIcon:
                HideIcon();
                DispatcherQueue.TryEnqueue(() => DisableHoverIconRequested?.Invoke(this, EventArgs.Empty));
                break;

            case MenuCommandOpenSettings:
                HideIcon();
                DispatcherQueue.TryEnqueue(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
                break;

            case MenuCommandExitApp:
                HideIcon();
                DispatcherQueue.TryEnqueue(() => ExitRequested?.Invoke(this, EventArgs.Empty));
                break;

            default:
                if (_isVisible && !_isPointerHovering)
                {
                    RestartAutoHideTimer(1800);
                }
                break;
        }
    }

    private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerHovering = true;
        _autoHideTimer.Stop();
    }

    private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerHovering = false;
        if (_isVisible && !_isContextMenuOpen)
        {
            RestartAutoHideTimer(1800);
        }
    }

    private void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            HideIcon();
        }
    }
}
