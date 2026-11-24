using PopTranslate.App.Services;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using WinUIEx;

namespace PopTranslate.App.Windows;

public sealed partial class FloatingHoverIconWindow : WindowEx
{
    private const double CompactWidthDips = 34;
    private const double ExpandedWidthDips = 72;
    private const double PillHeightDips = 34;

    private readonly IWindowHelperService _windowHelperService;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _autoHideTimer;
    private nint _hwnd;
    private ScreenPoint _currentPosPx;
    private double _currentScale = 1.0;
    private string? _sourceProcessName;
    private bool _isExpanded;
    private bool _isPointerHovering;
    private bool _isContextMenuOpen;
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
    public event EventHandler? PolishClicked;
    public event EventHandler<string>? BlacklistProcessRequested;
    public event EventHandler<TimeSpan>? MuteHoverRequested;
    public event EventHandler? DisableHoverIconRequested;

    public bool AllowClose { get; set; }

    public nint WindowHandle => _hwnd;

    public void ApplyTheme(AppThemeMode mode) => AppAssetHelper.ApplyTheme(this, mode);

    public void PreWarm()
    {
        ConfigureWindowChrome();
        SetExpandedVisualState(expanded: false);
        AppWindow.Hide();
        _isVisible = false;
    }

    public void ShowAt(ScreenPoint anchorPoint, int autoDismissMs, string? sourceProcessName = null)
    {
        _sourceProcessName = NormalizeProcessDisplayName(sourceProcessName);
        UpdateContextMenuProcessItem();
        SetExpandedVisualState(expanded: false);

        var monitor = _windowHelperService.GetMonitorAtPoint(anchorPoint);
        var scale = monitor.DpiScale > 0 ? monitor.DpiScale : 1.0;
        _currentScale = scale;

        var widthPx = Math.Max(28, (int)Math.Ceiling(CompactWidthDips * scale));
        var heightPx = Math.Max(28, (int)Math.Ceiling(PillHeightDips * scale));
        var cursorOffsetPx = Math.Max(8, (int)Math.Round(14 * scale));

        var pos = _windowHelperService.CalculateClampedWindowPosition(
            anchorPoint,
            widthPx,
            heightPx,
            cursorOffsetPx: cursorOffsetPx);
        _currentPosPx = pos;

        _windowHelperService.ConfigureNoActivateOverlayWindow(_hwnd);
        _windowHelperService.MoveAndResizeWindow(_hwnd, pos.X, pos.Y, widthPx, heightPx, activate: false);
        _windowHelperService.ShowWindowNoActivate(_hwnd);

        _isVisible = true;
        RestartAutoHideTimer(autoDismissMs);
    }

    public void HideIcon()
    {
        _autoHideTimer.Stop();
        _isPointerHovering = false;
        _isContextMenuOpen = false;
        if (HoverContextFlyout.IsOpen)
        {
            HoverContextFlyout.Hide();
        }

        SetExpandedVisualState(expanded: false);

        if (!_isVisible && !AppWindow.IsVisible)
        {
            return;
        }

        _isVisible = false;
        AppWindow.Hide();
    }

    private void ExpandPillToDualActions()
    {
        if (_isExpanded || !_isVisible)
        {
            return;
        }

        SetExpandedVisualState(expanded: true);

        var scale = _currentScale > 0 ? _currentScale : _windowHelperService.GetDpiScaleForWindow(_hwnd);
        if (scale <= 0)
        {
            scale = 1.0;
        }

        var expandedWidthPx = Math.Max(56, (int)Math.Ceiling(ExpandedWidthDips * scale));
        var heightPx = Math.Max(28, (int)Math.Ceiling(PillHeightDips * scale));

        var targetX = _currentPosPx.X;
        var targetY = _currentPosPx.Y;

        var monitor = _windowHelperService.GetMonitorAtPoint(_currentPosPx);
        var work = monitor.WorkArea;
        if (!work.IsEmpty && targetX + expandedWidthPx > work.Right)
        {
            targetX = Math.Max(work.X, work.Right - expandedWidthPx);
        }

        _currentPosPx = new ScreenPoint(targetX, targetY);
        _windowHelperService.MoveAndResizeWindow(_hwnd, targetX, targetY, expandedWidthPx, heightPx, activate: false);
    }

    private void SetExpandedVisualState(bool expanded)
    {
        _isExpanded = expanded;
        if (expanded)
        {
            PolishColumn.Width = new GridLength(1, GridUnitType.Star);
            ActionDivider.Visibility = Visibility.Visible;
            PolishActionButton.Visibility = Visibility.Visible;
        }
        else
        {
            PolishColumn.Width = new GridLength(0, GridUnitType.Pixel);
            ActionDivider.Visibility = Visibility.Collapsed;
            PolishActionButton.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateContextMenuProcessItem()
    {
        if (!string.IsNullOrWhiteSpace(_sourceProcessName))
        {
            BlacklistCurrentAppMenuItem.Text = $"屏蔽当前应用 ({_sourceProcessName})";
            BlacklistCurrentAppMenuItem.IsEnabled = true;
            BlacklistCurrentAppMenuItem.Visibility = Visibility.Visible;
        }
        else
        {
            BlacklistCurrentAppMenuItem.Text = "屏蔽当前应用";
            BlacklistCurrentAppMenuItem.IsEnabled = false;
            BlacklistCurrentAppMenuItem.Visibility = Visibility.Collapsed;
        }
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

    private void ConfigureWindowChrome()
    {
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

    private void TranslateActionButton_Click(object sender, RoutedEventArgs e)
    {
        HideIcon();
        TranslateClicked?.Invoke(this, EventArgs.Empty);
        IconClicked?.Invoke(this, EventArgs.Empty);
    }

    private void PolishActionButton_Click(object sender, RoutedEventArgs e)
    {
        HideIcon();
        PolishClicked?.Invoke(this, EventArgs.Empty);
    }

    private void BlacklistCurrentAppMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var processName = _sourceProcessName;
        HideIcon();
        if (!string.IsNullOrWhiteSpace(processName))
        {
            BlacklistProcessRequested?.Invoke(this, processName);
        }
    }

    private void MuteHover30MinMenuItem_Click(object sender, RoutedEventArgs e)
    {
        HideIcon();
        MuteHoverRequested?.Invoke(this, TimeSpan.FromMinutes(30));
    }

    private void DisableHoverIconMenuItem_Click(object sender, RoutedEventArgs e)
    {
        HideIcon();
        DisableHoverIconRequested?.Invoke(this, EventArgs.Empty);
    }

    private void HoverContextFlyout_Opening(object? sender, object e)
    {
        _isContextMenuOpen = true;
        _autoHideTimer.Stop();
    }

    private void HoverContextFlyout_Closed(object? sender, object e)
    {
        _isContextMenuOpen = false;
        if (_isVisible && !_isPointerHovering)
        {
            RestartAutoHideTimer(1800);
        }
    }

    private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerHovering = true;
        _autoHideTimer.Stop();
        ExpandPillToDualActions();
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
