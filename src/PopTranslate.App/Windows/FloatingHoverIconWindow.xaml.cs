using PopTranslate.App.Services;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using WinUIEx;

namespace PopTranslate.App.Windows;

public sealed partial class FloatingHoverIconWindow : WindowEx
{
    private const double IconSizeDips = 34;

    private readonly IWindowHelperService _windowHelperService;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _autoHideTimer;
    private nint _hwnd;
    private bool _isPointerHovering;
    private bool _isVisible;

    public FloatingHoverIconWindow(IWindowHelperService windowHelperService)
    {
        _windowHelperService = windowHelperService;
        InitializeComponent();

        SystemBackdrop = new DesktopAcrylicBackdrop();
        AppAssetHelper.TryApplyWindowIcon(this);

        _hwnd = this.GetWindowHandle();
        ConfigureWindowChrome();

        _autoHideTimer = DispatcherQueue.CreateTimer();
        _autoHideTimer.IsRepeating = false;
        _autoHideTimer.Tick += (_, _) =>
        {
            if (!_isPointerHovering)
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

    public bool AllowClose { get; set; }

    public nint WindowHandle => _hwnd;

    public void ApplyTheme(AppThemeMode mode) => AppAssetHelper.ApplyTheme(this, mode);

    public void PreWarm()
    {
        ConfigureWindowChrome();
        AppWindow.Hide();
        _isVisible = false;
    }

    public void ShowAt(ScreenPoint anchorPoint, int autoDismissMs)
    {
        var monitor = _windowHelperService.GetMonitorAtPoint(anchorPoint);
        var scale = monitor.DpiScale > 0 ? monitor.DpiScale : 1.0;
        var sizePx = Math.Max(28, (int)Math.Ceiling(IconSizeDips * scale));

        var pos = _windowHelperService.CalculateClampedWindowPosition(
            anchorPoint,
            sizePx,
            sizePx,
            cursorOffsetPx: 14);

        _windowHelperService.ConfigureNoActivateOverlayWindow(_hwnd);
        _windowHelperService.MoveAndResizeWindow(_hwnd, pos.X, pos.Y, sizePx, sizePx, activate: false);
        _windowHelperService.ShowWindowNoActivate(_hwnd);

        _isVisible = true;
        RestartAutoHideTimer(autoDismissMs);
    }

    public void HideIcon()
    {
        _autoHideTimer.Stop();
        _isPointerHovering = false;

        if (!_isVisible && !AppWindow.IsVisible)
        {
            return;
        }

        _isVisible = false;
        AppWindow.Hide();
    }

    private void ConfigureWindowChrome()
    {
        // Best-effort: ITaskbarList may be unavailable before Explorer starts or in non-shell sessions.
        // Win32 WS_EX_TOOLWINDOW applied below natively hides this window from taskbar & Alt+Tab.
        try
        {
            IsShownInSwitchers = false;
        }
        catch
        {
            // Ignore Shell_TrayWnd / ITaskbarList unavailability.
        }

        IsAlwaysOnTop = true;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        }

        _windowHelperService.ConfigureNoActivateOverlayWindow(_hwnd);
    }

    private void RestartAutoHideTimer(int autoDismissMs)
    {
        var clampedMs = Math.Clamp(autoDismissMs, 1500, 15000);
        _autoHideTimer.Stop();
        _autoHideTimer.Interval = TimeSpan.FromMilliseconds(clampedMs);
        _autoHideTimer.Start();
    }

    private void HoverButton_Click(object sender, RoutedEventArgs e)
    {
        HideIcon();
        IconClicked?.Invoke(this, EventArgs.Empty);
    }

    private void HoverButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerHovering = true;
        _autoHideTimer.Stop();
    }

    private void HoverButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerHovering = false;
        if (_isVisible)
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
