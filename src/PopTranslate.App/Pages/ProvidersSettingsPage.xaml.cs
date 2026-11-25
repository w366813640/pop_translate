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

    private void AddPolishProvider_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.AddPolishProviderCommand.CanExecute(null))
        {
            ViewModel.AddPolishProviderCommand.Execute(null);
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

    private void OpenAiApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { IsLoaded: true } box &&
            ViewModel?.SelectedProvider is { IsOpenAi: true } provider &&
            !string.Equals(provider.OpenAiApiKey, box.Password, StringComparison.Ordinal))
        {
            provider.OpenAiApiKey = box.Password;
        }
    }

    private void DeepLApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { IsLoaded: true } box &&
            ViewModel?.SelectedProvider is { IsDeepL: true } provider &&
            !string.Equals(provider.DeepLApiKey, box.Password, StringComparison.Ordinal))
        {
            provider.DeepLApiKey = box.Password;
        }
    }
}
