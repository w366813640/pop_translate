using PopTranslate.App.Pages;
using PopTranslate.App.Services;
using PopTranslate.App.ViewModels;
using PopTranslate.Core.Models;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WinUIEx;

namespace PopTranslate.App.Windows;

public sealed partial class SettingsWindow : WindowEx
{
    private readonly nint _hwnd;
    private bool _hasCenteredOnce;
    private bool _suppressTabFlush;
    private bool _isVisualTreeTornDown;
    private bool _isClosed;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppAssetHelper.TryApplyWindowIcon(this);

        _hwnd = this.GetWindowHandle();

        Activated += async (_, args) =>
        {
            if (!_isClosed && args.WindowActivationState == Microsoft.UI.Xaml.WindowActivationState.Deactivated)
            {
                await FlushPendingChangesSafelyAsync();
            }
        };

        AppWindow.Closing += async (_, _) =>
        {
            var flushTask = FlushPendingChangesSafelyAsync();
            TearDownVisualTree();
            await flushTask;
        };

        Closed += (_, _) =>
        {
            if (_isClosed)
            {
                return;
            }

            _isClosed = true;
            TearDownVisualTree();
            WindowClosed?.Invoke(this, EventArgs.Empty);
        };

        AppWindow.Hide();
    }

    public SettingsViewModel ViewModel { get; }

    public event EventHandler? ExitRequested;

    public event EventHandler? WindowClosed;

    public bool AllowClose { get; set; }

    public nint WindowHandle => _hwnd;

    public void ApplyTheme(AppThemeMode mode)
    {
        if (_isClosed)
        {
            return;
        }

        AppAssetHelper.ApplyTheme(this, mode);
    }

    public async Task ShowAndRefreshAsync(string? initialPageTag = null)
    {
        if (_isClosed)
        {
            return;
        }

        await ViewModel.FlushPendingChangesAsync();
        await ViewModel.RefreshAllAsync();

        if (_isClosed)
        {
            return;
        }

        var tag = string.IsNullOrWhiteSpace(initialPageTag) ? ViewModel.SelectedPageTag : initialPageTag;
        _suppressTabFlush = true;
        try
        {
            SelectNavigationItem(tag);
        }
        finally
        {
            _suppressTabFlush = false;
        }

        if (!_hasCenteredOnce)
        {
            _hasCenteredOnce = true;
            this.CenterOnScreen();
        }

        AppWindow.Show(activateWindow: true);
        Activate();
        this.BringToFront();
    }

    private void SelectNavigationItem(string tag)
    {
        var target = SettingsNavView.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            ?? SettingsNavView.MenuItems.OfType<NavigationViewItem>().First();

        if (!ReferenceEquals(SettingsNavView.SelectedItem, target))
        {
            SettingsNavView.SelectedItem = target;
        }
        else
        {
            NavigateByTag(target.Tag as string ?? "General");
        }
    }

    private void SettingsNavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem { Tag: "Exit" })
        {
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private async void SettingsNavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_isClosed || _isVisualTreeTornDown)
        {
            return;
        }

        if (args.SelectedItemContainer is NavigationViewItem { Tag: string tag })
        {
            if (!_suppressTabFlush)
            {
                await FlushPendingChangesSafelyAsync();
                if (_isClosed || _isVisualTreeTornDown)
                {
                    return;
                }
            }

            ViewModel.SelectedPageTag = tag;
            NavigateByTag(tag);
        }
    }

    private void TearDownVisualTree()
    {
        if (_isVisualTreeTornDown)
        {
            return;
        }

        _isVisualTreeTornDown = true;

        try
        {
            ContentFrame.Content = null;
            ContentFrame.BackStack.Clear();
            ContentFrame.ForwardStack.Clear();
            SystemBackdrop = null;
        }
        catch
        {
            // Ignore teardown races if WinUI window visual tree is already releasing.
        }
    }

    private async Task FlushPendingChangesSafelyAsync()
    {
        try
        {
            await ViewModel.FlushPendingChangesAsync();
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("Settings", "Failed to flush pending settings changes.", ex);
        }
    }

    private void NavigateByTag(string tag)
    {
        var transition = new EntranceNavigationTransitionInfo();
        switch (tag)
        {
            case "Providers":
                ContentFrame.Navigate(typeof(ProvidersSettingsPage), ViewModel.Providers, transition);
                break;
            case "Ocr":
                ContentFrame.Navigate(typeof(OcrSettingsPage), ViewModel.Ocr, transition);
                break;
            case "Hotkeys":
                ContentFrame.Navigate(typeof(HotkeysSettingsPage), ViewModel.Hotkeys, transition);
                break;
            case "Network":
                ContentFrame.Navigate(typeof(NetworkSettingsPage), ViewModel.Network, transition);
                break;
            default:
                ContentFrame.Navigate(typeof(GeneralSettingsPage), ViewModel.General, transition);
                break;
        }
    }
}
