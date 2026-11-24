using PopTranslate.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace PopTranslate.App.Pages;

public sealed partial class ProvidersSettingsPage : Page
{
    public ProvidersSettingsPage()
    {
        InitializeComponent();
    }

    public ProvidersSettingsViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is ProvidersSettingsViewModel vm)
        {
            ViewModel = vm;
            Bindings.Update();
        }
    }

    private void AddLlmPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string presetKey } &&
            ViewModel.AddLlmPresetCommand.CanExecute(presetKey))
        {
            ViewModel.AddLlmPresetCommand.Execute(presetKey);
        }
    }

    private void CancelDeleteProvider_Click(object sender, RoutedEventArgs e)
    {
        DeleteConfirmFlyout.Hide();
    }

    private void ConfirmDeleteProvider_Click(object sender, RoutedEventArgs e)
    {
        DeleteConfirmFlyout.Hide();
        if (ViewModel.DeleteSelectedProviderCommand.CanExecute(null))
        {
            ViewModel.DeleteSelectedProviderCommand.Execute(null);
        }
    }
}
