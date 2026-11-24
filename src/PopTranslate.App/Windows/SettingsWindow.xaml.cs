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

    public SettingsWindow(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppAssetHelper.TryApplyWindowIcon(this);

        _hwnd = this.GetWindowHandle();

        AppWindow.Closing += async (_, args) =>
        {
            if (!AllowClose)
            {
                args.Cancel = true;
                AppWindow.Hide();
                await ViewModel.FlushPendingChangesAsync();
            }
            else
            {
                await ViewModel.FlushPendingChangesAsync();
            }
        };

        AppWindow.Hide();
    }

    public SettingsViewModel ViewModel { get; }

    public bool AllowClose { get; set; }

    public nint WindowHandle => _hwnd;

    public void ApplyTheme(AppThemeMode mode) => AppAssetHelper.ApplyTheme(this, mode);

    public async Task ShowAndRefreshAsync(string? initialPageTag = null)
    {
        await ViewModel.FlushPendingChangesAsync();
        await ViewModel.RefreshAllAsync();

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

    private async void SettingsNavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is NavigationViewItem { Tag: string tag })
        {
            if (!_suppressTabFlush)
            {
                await ViewModel.FlushPendingChangesAsync();
            }

            ViewModel.SelectedPageTag = tag;
            NavigateByTag(tag);
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
