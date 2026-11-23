using PopTranslate.App.ViewModels;
using PopTranslate.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace PopTranslate.App.Pages;

public sealed partial class HotkeysSettingsPage : Page
{
    public HotkeysSettingsPage()
    {
        InitializeComponent();
    }

    public HotkeysSettingsViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is HotkeysSettingsViewModel vm)
        {
            ViewModel = vm;
            Bindings.Update();
        }
    }

    private void SelectionHotkey_GestureChanged(object sender, HotkeyGesture e) =>
        ViewModel.UpdateGesture(HotkeyAction.SelectionTranslate, e);

    private void InputHotkey_GestureChanged(object sender, HotkeyGesture e) =>
        ViewModel.UpdateGesture(HotkeyAction.InputTranslate, e);

    private void ScreenshotHotkey_GestureChanged(object sender, HotkeyGesture e) =>
        ViewModel.UpdateGesture(HotkeyAction.ScreenshotTranslate, e);

    private void SelectionPolishHotkey_GestureChanged(object sender, HotkeyGesture e) =>
        ViewModel.UpdateGesture(HotkeyAction.SelectionPolish, e);

    private void BlacklistInputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            if (ViewModel.AddBlacklistProcessCommand.CanExecute(null))
            {
                ViewModel.AddBlacklistProcessCommand.Execute(null);
            }
        }
    }

    private void RemoveBlacklistItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string processName })
        {
            ViewModel.RemoveBlacklistProcessCommand.Execute(processName);
        }
    }
}
