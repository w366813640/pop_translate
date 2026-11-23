using System.ComponentModel;
using PopTranslate.App.Services;
using PopTranslate.App.ViewModels;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;
using WinUIEx;

namespace PopTranslate.App.Windows;

public sealed partial class TranslationPopupWindow : WindowEx
{
    private const double MinPopupHeightDips = 230;
    private const double MinPopupWidthDips = 340;
    private const double MaxPopupWidthDips = 760;

    private readonly IWindowHelperService _windowHelperService;
    private WindowBehaviorConfig _behaviorConfig = new();
    private nint _hwnd;
    private bool _isPopupVisible;
    private bool _isInternalDeactivationCheckPending;
    private bool _isProgrammaticResize;
    private bool _isResizeQueued;
    private int _openDropDownCount;
    private DateTimeOffset _lastShownUtc = DateTimeOffset.MinValue;

    // Borderless toolbar drag tracking (physical screen pixels)
    private bool _isDraggingToolbar;
    private ScreenPoint _dragStartCursorPx;
    private PointInt32 _dragStartWindowPosPx;

    public TranslationPopupWindow(
        TranslationPopupViewModel viewModel,
        IWindowHelperService windowHelperService)
    {
        ViewModel = viewModel;
        _windowHelperService = windowHelperService;

        InitializeComponent();

        SystemBackdrop = new DesktopAcrylicBackdrop();
        AppAssetHelper.TryApplyWindowIcon(this);
        ExtendsContentIntoTitleBar = true;

        _hwnd = this.GetWindowHandle();
        ViewModel.AttachDispatcher(DispatcherQueue);
        ViewModel.ContentLayoutChanged += (_, _) => ScheduleDynamicHeightUpdate();
        ViewModel.PinStateChanged += (_, isPinned) => ApplyTopMostState(isPinned);
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        SyncModeToggleButtons();
        ConfigureWindowChrome();

        Activated += TranslationPopupWindow_Activated;
        SizeChanged += TranslationPopupWindow_SizeChanged;
        AppWindow.Closing += (_, args) =>
        {
            if (!AllowClose)
            {
                args.Cancel = true;
                HidePopup();
            }
        };

        AppWindow.Hide();
    }

    public event EventHandler<string?>? SettingsRequested;
    public event EventHandler? ScreenshotRequested;
    public event EventHandler<ScreenPoint>? FixedPositionMoved;
    public event EventHandler<double>? PopupWidthChanged;

    public TranslationPopupViewModel ViewModel { get; }

    public bool AllowClose { get; set; }

    public nint WindowHandle => _hwnd;

    public bool IsPopupVisible => _isPopupVisible && AppWindow.IsVisible;

    public void PreWarm(AppSettings settings)
    {
        ApplySettings(settings, applySessionDefaults: true);
        ConfigureWindowChrome();
        AppWindow.Hide();
        _isPopupVisible = false;
    }

    public void ApplySettings(AppSettings settings, bool applySessionDefaults = false)
    {
        _behaviorConfig = settings.Window.Clone();
        AppAssetHelper.ApplyTheme(this, settings.Window.Theme);
        ViewModel.ApplySettings(settings, applySessionDefaults);
        ApplyTopMostState(ViewModel.IsPinned);
    }

    public void ShowPopup(ScreenPoint? anchorPoint, bool focusSourceInput)
    {
        SyncModeToggleButtons();

        var desiredWidthDips = Math.Clamp(_behaviorConfig.PopupWidth, MinPopupWidthDips, MaxPopupWidthDips);
        var desiredHeightDips = CalculateDesiredHeightDips();

        var anchor = anchorPoint ?? _windowHelperService.GetCursorPosition();
        var monitor = _windowHelperService.GetMonitorAtPoint(anchor);
        var scale = monitor.DpiScale > 0 ? monitor.DpiScale : 1.0;

        var widthPx = Math.Max(320, (int)Math.Ceiling(desiredWidthDips * scale));
        var heightPx = Math.Max(200, (int)Math.Ceiling(desiredHeightDips * scale));

        ScreenPoint targetPos;
        if (_behaviorConfig.PositionMode == WindowPositionMode.RememberFixedPosition)
        {
            targetPos = ClampTopLeftToWorkArea(
                new ScreenPoint(_behaviorConfig.FixedX, _behaviorConfig.FixedY),
                widthPx,
                heightPx);
        }
        else
        {
            targetPos = _windowHelperService.CalculateClampedWindowPosition(
                anchor,
                widthPx,
                heightPx,
                cursorOffsetPx: 14);
        }

        _lastShownUtc = DateTimeOffset.UtcNow;
        _isPopupVisible = true;

        _isProgrammaticResize = true;
        try
        {
            _windowHelperService.ConfigurePopupToolWindow(_hwnd, hideFromTaskbar: true);
            _windowHelperService.SetWindowTopMost(_hwnd, ViewModel.IsPinned);
            _windowHelperService.MoveAndResizeWindow(_hwnd, targetPos.X, targetPos.Y, widthPx, heightPx, activate: true);
        }
        finally
        {
            _isProgrammaticResize = false;
        }

        AppWindow.Show(activateWindow: true);
        _windowHelperService.ForceForegroundWindow(_hwnd);

        if (focusSourceInput)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                SourceTextBox.Focus(FocusState.Programmatic);
                SourceTextBox.SelectAll();
            });
        }

        ScheduleDynamicHeightUpdate();
    }

    public void HidePopup(bool cancelTranslations = true)
    {
        if (cancelTranslations)
        {
            ViewModel.CancelActiveTranslation();
        }

        _openDropDownCount = 0;
        _isDraggingToolbar = false;
        _isPopupVisible = false;

        if (AppWindow.IsVisible)
        {
            AppWindow.Hide();
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TranslationPopupViewModel.CurrentMode)
            or nameof(TranslationPopupViewModel.IsTranslateMode)
            or nameof(TranslationPopupViewModel.IsPolishMode))
        {
            SyncModeToggleButtons();
            Title = ViewModel.WindowTitle;
            ScheduleDynamicHeightUpdate();
        }
    }

    private void SyncModeToggleButtons()
    {
        TranslateModeButton.IsChecked = ViewModel.IsTranslateMode;
        PolishModeButton.IsChecked = ViewModel.IsPolishMode;
    }

    private void TranslateModeButton_Click(object sender, RoutedEventArgs e)
    {
        TranslateModeButton.IsChecked = true;
        PolishModeButton.IsChecked = false;
        if (ViewModel.SelectTranslateModeCommand.CanExecute(null))
        {
            ViewModel.SelectTranslateModeCommand.Execute(null);
        }
    }

    private void PolishModeButton_Click(object sender, RoutedEventArgs e)
    {
        PolishModeButton.IsChecked = true;
        TranslateModeButton.IsChecked = false;
        if (ViewModel.SelectPolishModeCommand.CanExecute(null))
        {
            ViewModel.SelectPolishModeCommand.Execute(null);
        }
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

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        }

        _windowHelperService.ConfigurePopupToolWindow(_hwnd, hideFromTaskbar: true);
        ApplyTopMostState(ViewModel.IsPinned);
    }

    private void ApplyTopMostState(bool topMost)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = topMost;
        }

        _windowHelperService.SetWindowTopMost(_hwnd, topMost);
    }

    private void ScheduleDynamicHeightUpdate()
    {
        if (_isResizeQueued || !IsPopupVisible)
        {
            return;
        }

        _isResizeQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _isResizeQueued = false;
            if (!IsPopupVisible)
            {
                return;
            }

            UpdateWindowHeightToFitContent();
        });
    }

    private void UpdateWindowHeightToFitContent()
    {
        var scale = _windowHelperService.GetDpiScaleForWindow(_hwnd);
        if (scale <= 0)
        {
            scale = 1.0;
        }

        var desiredHeightDips = CalculateDesiredHeightDips();
        var currentSizePx = AppWindow.Size;
        var targetHeightPx = Math.Max(200, (int)Math.Ceiling(desiredHeightDips * scale));

        if (Math.Abs(currentSizePx.Height - targetHeightPx) < 4)
        {
            return;
        }

        var pos = AppWindow.Position;
        var monitor = _windowHelperService.GetMonitorAtPoint(new ScreenPoint(pos.X + currentSizePx.Width / 2, pos.Y + 20));
        var work = monitor.WorkArea;

        var newY = pos.Y;
        if (!work.IsEmpty && newY + targetHeightPx > work.Bottom)
        {
            newY = Math.Max(work.Y, work.Bottom - targetHeightPx);
        }

        _isProgrammaticResize = true;
        try
        {
            AppWindow.MoveAndResize(new RectInt32(pos.X, newY, currentSizePx.Width, targetHeightPx));
        }
        finally
        {
            _isProgrammaticResize = false;
        }
    }

    private double CalculateDesiredHeightDips()
    {
        var maxHeightDips = Math.Clamp(_behaviorConfig.PopupMaxHeight, 300, 1000);

        // Measure current child heights safely; fallback to comfortable compact height before first layout pass.
        var toolbarH = TopToolbarGrid.ActualHeight > 0 ? TopToolbarGrid.ActualHeight : 40;
        var subToolbarH = SubToolbarContainer.ActualHeight > 0 ? SubToolbarContainer.ActualHeight : 36;
        var sourceH = SourceCardBorder.ActualHeight > 0 ? SourceCardBorder.ActualHeight + 10 : 108;
        var bannerH = StatusBannerBorder.Visibility == Visibility.Visible && StatusBannerBorder.ActualHeight > 0
            ? StatusBannerBorder.ActualHeight + 8
            : 0;
        var cardsH = CardsContentPanel.ActualHeight > 0 ? CardsContentPanel.ActualHeight + 16 : 64;

        var total = toolbarH + subToolbarH + sourceH + bannerH + cardsH + 8;
        return Math.Clamp(total, MinPopupHeightDips, maxHeightDips);
    }

    private ScreenPoint ClampTopLeftToWorkArea(ScreenPoint desired, int widthPx, int heightPx)
    {
        var monitor = _windowHelperService.GetMonitorAtPoint(desired);
        var work = monitor.WorkArea;
        if (work.IsEmpty)
        {
            return desired;
        }

        var maxX = Math.Max(work.X, work.Right - widthPx);
        var maxY = Math.Max(work.Y, work.Bottom - heightPx);
        return new ScreenPoint(
            Math.Clamp(desired.X, work.X, maxX),
            Math.Clamp(desired.Y, work.Y, maxY));
    }

    private void TranslationPopupWindow_Activated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            return;
        }

        if (!IsPopupVisible || ViewModel.IsPinned || !_behaviorConfig.HideOnClickOutside)
        {
            return;
        }

        // Guard against spurious deactivation immediately after show or while ComboBox/Flyout popups are open.
        if (_openDropDownCount > 0 || (DateTimeOffset.UtcNow - _lastShownUtc).TotalMilliseconds < 180)
        {
            return;
        }

        if (_isInternalDeactivationCheckPending)
        {
            return;
        }

        _isInternalDeactivationCheckPending = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(70);
            DispatcherQueue.TryEnqueue(() =>
            {
                _isInternalDeactivationCheckPending = false;
                if (!IsPopupVisible || ViewModel.IsPinned || !_behaviorConfig.HideOnClickOutside)
                {
                    return;
                }

                if (_openDropDownCount > 0 || HasOpenInternalPopups())
                {
                    return;
                }

                if (_windowHelperService.GetForegroundWindowHandle() == _hwnd)
                {
                    return;
                }

                HidePopup();
            });
        });
    }

    private bool HasOpenInternalPopups()
    {
        if (Content?.XamlRoot is null)
        {
            return false;
        }

        try
        {
            return VisualTreeHelper.GetOpenPopupsForXamlRoot(Content.XamlRoot).Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private void TranslationPopupWindow_SizeChanged(object sender, Microsoft.UI.Xaml.WindowSizeChangedEventArgs args)
    {
        if (_isProgrammaticResize || !IsPopupVisible)
        {
            return;
        }

        var widthDips = Math.Clamp(args.Size.Width, MinPopupWidthDips, MaxPopupWidthDips);
        if (Math.Abs(widthDips - _behaviorConfig.PopupWidth) >= 8)
        {
            _behaviorConfig.PopupWidth = Math.Round(widthDips);
            PopupWidthChanged?.Invoke(this, _behaviorConfig.PopupWidth);
        }
    }

    private void CardsContentPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ScheduleDynamicHeightUpdate();
    }

    private void LanguageComboBox_DropDownOpened(object? sender, object e)
    {
        _openDropDownCount++;
    }

    private void LanguageComboBox_DropDownClosed(object? sender, object e)
    {
        _openDropDownCount = Math.Max(0, _openDropDownCount - 1);
    }

    private void RootContainer_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && _behaviorConfig.HideOnEscape && _openDropDownCount == 0 && !HasOpenInternalPopups())
        {
            e.Handled = true;
            HidePopup();
        }
    }

    private void SourceTextBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && _behaviorConfig.HideOnEscape)
        {
            e.Handled = true;
            HidePopup();
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            var shiftDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(CoreVirtualKeyStates.Down);
            var ctrlDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                .HasFlag(CoreVirtualKeyStates.Down);

            // Enter or Ctrl+Enter executes current mode immediately; Shift+Enter inserts a newline.
            if (!shiftDown || ctrlDown)
            {
                e.Handled = true;
                if (ViewModel.RetranslateCommand.CanExecute(null))
                {
                    ViewModel.RetranslateCommand.Execute(null);
                }
            }
        }
    }

    private void CustomPolishPromptTextBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && _behaviorConfig.HideOnEscape)
        {
            e.Handled = true;
            HidePopup();
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            if (ViewModel.HasSourceText && ViewModel.RetranslateCommand.CanExecute(null))
            {
                ViewModel.RetranslateCommand.Execute(null);
            }
        }
    }

    private void ScreenshotButton_Click(object sender, RoutedEventArgs e)
    {
        ScreenshotRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke(this, null);
    }

    private void NoProvidersSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke(this, "Providers");
    }

    private void TopToolbarGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(TopToolbarGrid).Properties;
        if (!props.IsLeftButtonPressed)
        {
            return;
        }

        // Do not start window drag when clicking an interactive control inside the toolbar.
        if (e.OriginalSource is DependencyObject source && IsInteractiveToolbarChild(source))
        {
            return;
        }

        _isDraggingToolbar = true;
        _dragStartCursorPx = _windowHelperService.GetCursorPosition();
        _dragStartWindowPosPx = AppWindow.Position;
        TopToolbarGrid.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void TopToolbarGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingToolbar)
        {
            return;
        }

        var currentCursor = _windowHelperService.GetCursorPosition();
        var dx = currentCursor.X - _dragStartCursorPx.X;
        var dy = currentCursor.Y - _dragStartCursorPx.Y;
        AppWindow.Move(new PointInt32(_dragStartWindowPosPx.X + dx, _dragStartWindowPosPx.Y + dy));
        e.Handled = true;
    }

    private void TopToolbarGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingToolbar)
        {
            return;
        }

        FinishToolbarDrag(e.Pointer);
        e.Handled = true;
    }

    private void TopToolbarGrid_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_isDraggingToolbar)
        {
            FinishToolbarDrag(null);
        }
    }

    private void FinishToolbarDrag(Pointer? pointer)
    {
        _isDraggingToolbar = false;
        if (pointer is not null)
        {
            TopToolbarGrid.ReleasePointerCapture(pointer);
        }

        var pos = AppWindow.Position;
        _behaviorConfig.FixedX = pos.X;
        _behaviorConfig.FixedY = pos.Y;
        FixedPositionMoved?.Invoke(this, new ScreenPoint(pos.X, pos.Y));
    }

    private static bool IsInteractiveToolbarChild(DependencyObject element)
    {
        var current = element;
        while (current is not null)
        {
            if (current is ButtonBase or ComboBox or TextBox)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
