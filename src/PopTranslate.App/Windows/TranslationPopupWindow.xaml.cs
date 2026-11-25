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
    private const double DefaultFluentPopupWidthDips = 460;
    private const double MinPopupHeightDips = 190;
    private const double MinPopupWidthDips = 360;
    private const double MaxPopupWidthDips = 760;
    private static readonly TimeSpan HeightThrottleInterval = TimeSpan.FromMilliseconds(60);

    private readonly IWindowHelperService _windowHelperService;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _heightThrottleTimer;
    private WindowBehaviorConfig _behaviorConfig = new();
    private nint _hwnd;
    private bool _isPopupVisible;
    private bool _isInternalDeactivationCheckPending;
    private bool _isProgrammaticResize;
    private bool _isResizeQueued;
    private bool _isManualHeightLocked;
    private bool _isInternalFlyoutOpen;
    private int _targetProgrammaticHeightPx;
    private int _openDropDownCount;
    private DateTimeOffset _lastShownUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastHeightUpdateUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFlyoutActivityUtc = DateTimeOffset.MinValue;

    // Borderless toolbar drag tracking (physical screen pixels)
    private bool _isDraggingToolbar;
    private bool _hasDraggedDistance;
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

        _heightThrottleTimer = DispatcherQueue.CreateTimer();
        _heightThrottleTimer.Interval = HeightThrottleInterval;
        _heightThrottleTimer.IsRepeating = false;
        _heightThrottleTimer.Tick += (_, _) =>
        {
            if (IsPopupVisible && !_isManualHeightLocked)
            {
                _lastHeightUpdateUtc = DateTimeOffset.UtcNow;
                UpdateWindowHeightToFitContent();
            }
        };

        ViewModel.AttachDispatcher(DispatcherQueue);
        ViewModel.ContentLayoutChanged += (_, _) => ScheduleDynamicHeightUpdate();
        ViewModel.PinStateChanged += (_, isPinned) => ApplyTopMostState(isPinned);
        ViewModel.HidePopupRequested += (_, _) => HidePopup(cancelTranslations: false);
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

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
    public event EventHandler? PopupShown;
    public event EventHandler? PopupHidden;

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
        if (Math.Abs(_behaviorConfig.PopupWidth - 440) < 1)
        {
            _behaviorConfig.PopupWidth = DefaultFluentPopupWidthDips;
        }

        AppAssetHelper.ApplyTheme(this, settings.Window.Theme);
        ViewModel.ApplySettings(settings, applySessionDefaults);
        ApplyTopMostState(ViewModel.IsPinned);
    }

    public void ShowPopup(ScreenPoint? anchorPoint, bool focusSourceInput, bool centerOnScreen = false)
    {
        _heightThrottleTimer.Stop();
        _isManualHeightLocked = false;

        var rawWidth = _behaviorConfig.PopupWidth > 0 ? _behaviorConfig.PopupWidth : DefaultFluentPopupWidthDips;
        var desiredWidthDips = Math.Clamp(rawWidth, MinPopupWidthDips, MaxPopupWidthDips);
        var desiredHeightDips = CalculateInitialDesiredHeightDips(desiredWidthDips);

        var anchor = anchorPoint ?? _windowHelperService.GetCursorPosition();
        var monitor = _windowHelperService.GetMonitorAtPoint(anchor);
        var scale = monitor.DpiScale > 0 ? monitor.DpiScale : 1.0;

        var widthPx = Math.Max(320, (int)Math.Ceiling(desiredWidthDips * scale));
        var heightPx = Math.Max(180, (int)Math.Ceiling(desiredHeightDips * scale));
        var cursorOffsetPx = Math.Max(8, (int)Math.Round(14 * scale));

        var targetPos = ResolvePopupPosition(
            _windowHelperService,
            _behaviorConfig,
            anchor,
            widthPx,
            heightPx,
            cursorOffsetPx,
            centerOnScreen);

        _lastShownUtc = DateTimeOffset.UtcNow;
        _lastHeightUpdateUtc = _lastShownUtc;
        _isPopupVisible = true;
        ViewModel.SetPopupVisible(true);
        _targetProgrammaticHeightPx = heightPx;

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
            FocusSourceInput();
        }

        ScheduleDynamicHeightUpdate();
        PopupShown?.Invoke(this, EventArgs.Empty);
    }

    public void HidePopup(bool cancelTranslations = true)
    {
        _heightThrottleTimer.Stop();

        if (cancelTranslations)
        {
            ViewModel.CancelActiveTranslation();
        }
        else
        {
            ViewModel.StopActiveSpeech();
        }

        _openDropDownCount = 0;
        _isInternalFlyoutOpen = false;
        ViewModel.ResetInternalFlyoutState();
        _isDraggingToolbar = false;
        _hasDraggedDistance = false;
        _isManualHeightLocked = false;
        _isPopupVisible = false;
        ViewModel.SetPopupVisible(false);

        if (AppWindow.IsVisible)
        {
            AppWindow.Hide();
        }

        PopupHidden?.Invoke(this, EventArgs.Empty);
    }

    public void FocusSourceInput()
    {
        if (ViewModel.IsSourceCardCollapsed)
        {
            ViewModel.IsSourceCardCollapsed = false;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            SourceTextBox.Focus(FocusState.Programmatic);
            SourceTextBox.SelectAll();
        });
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TranslationPopupViewModel.IsSourceCardCollapsed)
            or nameof(TranslationPopupViewModel.HasOcrContext)
            or nameof(TranslationPopupViewModel.HasStatusBanner)
            or nameof(TranslationPopupViewModel.HasPendingInputGuard))
        {
            ScheduleDynamicHeightUpdate();
        }
    }

    private void SourceTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        SourceFocusAccentBorder.Opacity = 1.0;
    }

    private void SourceTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        SourceFocusAccentBorder.Opacity = 0.5;
    }

    private void SourceLangMenuFlyout_Opening(object? sender, object e)
    {
        MarkInternalFlyoutOpened();

        SourceLangMenuFlyout.Items.Clear();
        var selectedCode = ViewModel.SelectedSourceLanguage?.Code ?? LanguageCode.Auto;

        foreach (var option in ViewModel.SourceLanguageOptions)
        {
            var captured = option;
            var item = new ToggleMenuFlyoutItem
            {
                Text = option.DisplayLabel,
                IsChecked = option.Code == selectedCode,
            };
            item.Click += (_, _) => ViewModel.SelectedSourceLanguage = captured;
            SourceLangMenuFlyout.Items.Add(item);
        }
    }

    private void TargetLangMenuFlyout_Opening(object? sender, object e)
    {
        MarkInternalFlyoutOpened();

        TargetLangMenuFlyout.Items.Clear();
        var selectedCode = ViewModel.SelectedTargetLanguage?.Code ?? LanguageCode.Auto;

        foreach (var option in ViewModel.TargetLanguageOptions)
        {
            var captured = option;
            var item = new ToggleMenuFlyoutItem
            {
                Text = option.DisplayLabel,
                IsChecked = option.Code == selectedCode,
            };
            item.Click += (_, _) => ViewModel.SelectedTargetLanguage = captured;
            TargetLangMenuFlyout.Items.Add(item);
        }
    }

    private void OcrModeMenuFlyout_Opening(object? sender, object e)
    {
        MarkInternalFlyoutOpened();

        OcrModeMenuFlyout.Items.Clear();
        var selectedMode = ViewModel.SelectedOcrPromptMode?.Mode ?? OcrPromptMode.PlainText;

        foreach (var option in ViewModel.OcrPromptModeOptions)
        {
            var captured = option;
            var item = new ToggleMenuFlyoutItem
            {
                Text = option.DisplayLabel,
                IsChecked = option.Mode == selectedMode,
            };
            item.Click += (_, _) => ViewModel.SelectedOcrPromptMode = captured;
            OcrModeMenuFlyout.Items.Add(item);
        }
    }

    private void HistoryMenuFlyout_Opening(object? sender, object e)
    {
        MarkInternalFlyoutOpened();

        HistoryMenuFlyout.Items.Clear();
        if (ViewModel.HistoryEntries.Count == 0)
        {
            HistoryMenuFlyout.Items.Add(new MenuFlyoutItem
            {
                Text = "暂无翻译历史",
                IsEnabled = false,
            });
            return;
        }

        foreach (var entry in ViewModel.HistoryEntries)
        {
            var capturedId = entry.Id;
            var item = new MenuFlyoutItem
            {
                Text = $"{entry.PreviewText}  ({entry.LanguagePairLabel})",
            };
            item.Click += (_, _) => ViewModel.RestoreHistoryEntry(capturedId);
            HistoryMenuFlyout.Items.Add(item);
        }

        HistoryMenuFlyout.Items.Add(new MenuFlyoutSeparator());
        var clearItem = new MenuFlyoutItem
        {
            Text = "清空历史记录",
        };
        clearItem.Click += (_, _) => ViewModel.ClearHistory();
        HistoryMenuFlyout.Items.Add(clearItem);
    }

    private void MarkInternalFlyoutOpened()
    {
        _openDropDownCount++;
        _isInternalFlyoutOpen = true;
        _lastFlyoutActivityUtc = DateTimeOffset.UtcNow;
        ViewModel.NotifyInternalFlyoutOpened();
    }

    private void InternalFlyout_Closed(object? sender, object e)
    {
        _openDropDownCount = Math.Max(0, _openDropDownCount - 1);
        _isInternalFlyoutOpen = _openDropDownCount > 0;
        _lastFlyoutActivityUtc = DateTimeOffset.UtcNow;
        ViewModel.NotifyInternalFlyoutClosed();
    }

    private bool IsAnyInternalFlyoutOrPopupOpen()
    {
        var now = DateTimeOffset.UtcNow;
        return _isInternalFlyoutOpen ||
               _openDropDownCount > 0 ||
               ViewModel.HasOpenInternalFlyout ||
               (now - _lastFlyoutActivityUtc).TotalMilliseconds < 220 ||
               (now - ViewModel.LastFlyoutActivityUtc).TotalMilliseconds < 220 ||
               HasOpenInternalPopups();
    }

    private void ClosePopupButton_Click(object sender, RoutedEventArgs e)
    {
        HidePopup();
    }

    private void CollapsedSourcePreview_Tapped(object sender, TappedRoutedEventArgs e)
    {
        FocusSourceInput();
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
        if (!IsPopupVisible || _isManualHeightLocked)
        {
            return;
        }

        var elapsed = DateTimeOffset.UtcNow - _lastHeightUpdateUtc;
        if (elapsed < HeightThrottleInterval)
        {
            if (!_heightThrottleTimer.IsRunning)
            {
                var remaining = HeightThrottleInterval - elapsed;
                _heightThrottleTimer.Interval = remaining > TimeSpan.FromMilliseconds(12)
                    ? remaining
                    : TimeSpan.FromMilliseconds(12);
                _heightThrottleTimer.Start();
            }

            return;
        }

        if (_isResizeQueued)
        {
            return;
        }

        _isResizeQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _isResizeQueued = false;
            if (!IsPopupVisible || _isManualHeightLocked)
            {
                return;
            }

            _lastHeightUpdateUtc = DateTimeOffset.UtcNow;
            UpdateWindowHeightToFitContent();
        });
    }

    private void UpdateWindowHeightToFitContent()
    {
        if (_isManualHeightLocked || _isDraggingToolbar)
        {
            return;
        }

        var scale = _windowHelperService.GetDpiScaleForWindow(_hwnd);
        if (scale <= 0)
        {
            scale = 1.0;
        }

        var maxHeightDips = Math.Clamp(_behaviorConfig.PopupMaxHeight, 300, 1000);
        var currentSizePx = AppWindow.Size;
        var currentHeightDips = currentSizePx.Height / scale;
        var measuredDesiredDips = CalculateMeasuredDesiredHeightDips();

        var resolvedHeightDips = PopupHeightPolicy.ResolveDynamicHeightDips(new PopupDynamicHeightInput(
            CurrentHeightDips: currentHeightDips,
            MeasuredDesiredHeightDips: measuredDesiredDips,
            IsAnyCardLoading: ViewModel.IsAnyCardLoading,
            MinHeightDips: MinPopupHeightDips,
            MaxHeightDips: maxHeightDips,
            LookaheadStepDips: PopupHeightPolicy.DefaultLookaheadStepDips));

        var targetHeightPx = Math.Max(180, (int)Math.Ceiling(resolvedHeightDips * scale));

        if (Math.Abs(currentSizePx.Height - targetHeightPx) < 4)
        {
            _targetProgrammaticHeightPx = currentSizePx.Height;
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

        _targetProgrammaticHeightPx = targetHeightPx;
        _isProgrammaticResize = true;
        try
        {
            if (newY == pos.Y)
            {
                AppWindow.Resize(new SizeInt32(currentSizePx.Width, targetHeightPx));
            }
            else
            {
                AppWindow.MoveAndResize(new RectInt32(pos.X, newY, currentSizePx.Width, targetHeightPx));
            }
        }
        finally
        {
            _isProgrammaticResize = false;
        }
    }

    private double CalculateInitialDesiredHeightDips(double desiredWidthDips)
    {
        var maxHeightDips = Math.Clamp(_behaviorConfig.PopupMaxHeight, 300, 1000);
        var baseChromeH = CalculateBaseChromeHeightDips();
        var measuredCardsH = CalculateMeasuredCardsHeightDips();
        var expandedActiveCards = ViewModel.ServiceCards.Count(c => c.IsLoading && !c.IsCollapsed);
        var collapsedOrOnDemandCards = ViewModel.ServiceCards.Count(c => c.IsCollapsed || c.IsWaitingOnDemand);

        return PopupHeightPolicy.EstimateInitialHeightDips(new PopupInitialHeightInput(
            BaseChromeHeightDips: baseChromeH,
            MeasuredCardsHeightDips: measuredCardsH,
            SourceTextLength: ViewModel.SourceText?.Length ?? 0,
            ExpandedActiveCardCount: expandedActiveCards,
            CollapsedOrOnDemandCardCount: collapsedOrOnDemandCards,
            IsAnyCardLoading: ViewModel.IsAnyCardLoading,
            PopupWidthDips: desiredWidthDips,
            MinHeightDips: MinPopupHeightDips,
            MaxHeightDips: maxHeightDips));
    }

    private double CalculateMeasuredDesiredHeightDips()
    {
        var maxHeightDips = Math.Clamp(_behaviorConfig.PopupMaxHeight, 300, 1000);
        return Math.Clamp(CalculateBaseChromeHeightDips() + CalculateMeasuredCardsHeightDips(), MinPopupHeightDips, maxHeightDips);
    }

    private double CalculateBaseChromeHeightDips()
    {
        var toolbarH = TopToolbarGrid.ActualHeight > 0 ? TopToolbarGrid.ActualHeight : 54;
        var fallbackSourceH = (ViewModel.IsSourceCardCollapsed ? 50 : 122) + (ViewModel.HasOcrContext ? 42 : 0);
        var sourceH = IsPopupVisible && SourceSectionPanel.ActualHeight > 0
            ? SourceSectionPanel.ActualHeight + 12
            : fallbackSourceH;
        var bannerH = StatusBannerBorder.Visibility == Visibility.Visible && StatusBannerBorder.ActualHeight > 0
            ? StatusBannerBorder.ActualHeight + 10
            : 0;
        return toolbarH + sourceH + bannerH + 6;
    }

    private double CalculateMeasuredCardsHeightDips() =>
        ViewModel.ServiceCards.Count == 0 && !ViewModel.ShowNoEnabledProvidersHint && !ViewModel.HasPendingInputGuard
            ? 64
            : (CardsContentPanel.ActualHeight > 0 ? CardsContentPanel.ActualHeight + 16 : 64);

    public static ScreenPoint ResolvePopupPosition(
        IWindowHelperService windowHelperService,
        WindowBehaviorConfig behaviorConfig,
        ScreenPoint anchor,
        int widthPx,
        int heightPx,
        int cursorOffsetPx,
        bool centerOnScreen)
    {
        if (behaviorConfig.PositionMode == WindowPositionMode.RememberFixedPosition)
        {
            return ClampTopLeftToWorkArea(
                windowHelperService,
                new ScreenPoint(behaviorConfig.FixedX, behaviorConfig.FixedY),
                widthPx,
                heightPx);
        }

        if (centerOnScreen)
        {
            return windowHelperService.CalculateCenteredWindowPosition(
                anchor,
                widthPx,
                heightPx);
        }

        return windowHelperService.CalculateClampedWindowPosition(
            anchor,
            widthPx,
            heightPx,
            cursorOffsetPx: cursorOffsetPx);
    }

    private static ScreenPoint ClampTopLeftToWorkArea(
        IWindowHelperService windowHelperService,
        ScreenPoint desired,
        int widthPx,
        int heightPx)
    {
        var monitor = windowHelperService.GetMonitorAtPoint(desired);
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

        // Guard against spurious deactivation immediately after show or while any MenuFlyout is open.
        if (IsAnyInternalFlyoutOrPopupOpen() || (DateTimeOffset.UtcNow - _lastShownUtc).TotalMilliseconds < 180)
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
            await Task.Delay(80);
            DispatcherQueue.TryEnqueue(() =>
            {
                _isInternalDeactivationCheckPending = false;
                if (!IsPopupVisible || ViewModel.IsPinned || !_behaviorConfig.HideOnClickOutside)
                {
                    return;
                }

                if (IsAnyInternalFlyoutOrPopupOpen())
                {
                    return;
                }

                if (_windowHelperService.GetForegroundWindowHandle() == _hwnd)
                {
                    return;
                }

                // Preserve in-flight translations in background so accidental click-outside still lands in history cache.
                HidePopup(cancelTranslations: false);
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

        // Lock session auto-height if the user manually dragged the top/bottom window border vertically.
        var actualHeightPx = AppWindow.Size.Height;
        if (_targetProgrammaticHeightPx > 0 && Math.Abs(actualHeightPx - _targetProgrammaticHeightPx) >= 12)
        {
            _isManualHeightLocked = true;
            _heightThrottleTimer.Stop();
        }
    }

    private void CardsContentPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ScheduleDynamicHeightUpdate();
    }

    private void RootContainer_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && _behaviorConfig.HideOnEscape && !IsAnyInternalFlyoutOrPopupOpen())
        {
            e.Handled = true;
            HidePopup();
            return;
        }

        var ctrlDown = IsModifierDown(VirtualKey.Control);
        var shiftDown = IsModifierDown(VirtualKey.Shift);
        var altDown = IsModifierDown(VirtualKey.Menu);

        // Ctrl + Shift + Enter: Write-Back (Replace Selected Text in Source Window)
        if (ctrlDown && shiftDown && !altDown && e.Key == VirtualKey.Enter)
        {
            if (ViewModel.HasSourceWindow)
            {
                e.Handled = true;
                _ = ViewModel.WriteBackFirstResultAsync();
            }

            return;
        }

        // Ctrl + 1..9: Copy Nth result card
        if (ctrlDown && !shiftDown && !altDown && TryGetZeroBasedCardSlot(e.Key, out var cardIndex))
        {
            ViewModel.CopyCardByIndex(cardIndex);
            e.Handled = true;
            return;
        }

        // Ctrl + Shift + C: Copy 1st available result card
        if (ctrlDown && shiftDown && !altDown && e.Key == VirtualKey.C)
        {
            ViewModel.CopyFirstAvailableCard();
            e.Handled = true;
            return;
        }

        // Ctrl + P: Toggle Pin
        if (ctrlDown && !shiftDown && !altDown && e.Key == VirtualKey.P)
        {
            ViewModel.IsPinned = !ViewModel.IsPinned;
            e.Handled = true;
            return;
        }

        // Alt + Q (or legacy Alt + P): Trigger Polish cards immediately if popup is already open
        if (altDown && !ctrlDown && !shiftDown && e.Key is VirtualKey.Q or VirtualKey.P)
        {
            _ = ViewModel.TriggerAllPolishCardsAsync();
            e.Handled = true;
            return;
        }

        // Alt + S: Swap Source & Target languages
        if (altDown && !ctrlDown && !shiftDown && e.Key == VirtualKey.S)
        {
            if (ViewModel.SwapLanguagesCommand.CanExecute(null))
            {
                ViewModel.SwapLanguagesCommand.Execute(null);
                e.Handled = true;
            }

            return;
        }

        // Ctrl + H: Open Translation History menu
        if (ctrlDown && !shiftDown && !altDown && e.Key == VirtualKey.H)
        {
            HistoryMenuFlyout.ShowAt(HistoryButton);
            e.Handled = true;
            return;
        }

        // Ctrl + I: Expand & Focus Source input box
        if (ctrlDown && !shiftDown && !altDown && e.Key == VirtualKey.I)
        {
            FocusSourceInput();
            e.Handled = true;
        }
    }

    private static bool IsModifierDown(VirtualKey modifierKey) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(modifierKey)
            .HasFlag(CoreVirtualKeyStates.Down);

    private static bool TryGetZeroBasedCardSlot(VirtualKey key, out int index)
    {
        if (key >= VirtualKey.Number1 && key <= VirtualKey.Number9)
        {
            index = (int)(key - VirtualKey.Number1);
            return true;
        }

        if (key >= VirtualKey.NumberPad1 && key <= VirtualKey.NumberPad9)
        {
            index = (int)(key - VirtualKey.NumberPad1);
            return true;
        }

        index = -1;
        return false;
    }

    private void SourceTextBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        if (e.Key == VirtualKey.Escape && _behaviorConfig.HideOnEscape && !IsAnyInternalFlyoutOrPopupOpen())
        {
            e.Handled = true;
            HidePopup();
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            var shiftDown = IsModifierDown(VirtualKey.Shift);
            var ctrlDown = IsModifierDown(VirtualKey.Control);

            if (ctrlDown && shiftDown)
            {
                if (ViewModel.HasSourceWindow)
                {
                    e.Handled = true;
                    _ = ViewModel.WriteBackFirstResultAsync();
                }

                return;
            }

            // Enter or Ctrl+Enter executes immediately; Shift+Enter inserts a newline.
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

    private void GuardEditSourceButton_Click(object sender, RoutedEventArgs e)
    {
        FocusSourceInput();
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
        _hasDraggedDistance = false;
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

        if (!_hasDraggedDistance && (Math.Abs(dx) >= 3 || Math.Abs(dy) >= 3))
        {
            _hasDraggedDistance = true;
            if (!ViewModel.IsPinned)
            {
                ViewModel.IsPinned = true;
            }
        }

        if (_hasDraggedDistance)
        {
            AppWindow.Move(new PointInt32(_dragStartWindowPosPx.X + dx, _dragStartWindowPosPx.Y + dy));
        }

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
        var dragged = _hasDraggedDistance;
        _isDraggingToolbar = false;
        _hasDraggedDistance = false;

        if (pointer is not null)
        {
            TopToolbarGrid.ReleasePointerCapture(pointer);
        }

        if (!dragged)
        {
            return;
        }

        if (!ViewModel.IsPinned)
        {
            ViewModel.IsPinned = true;
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
