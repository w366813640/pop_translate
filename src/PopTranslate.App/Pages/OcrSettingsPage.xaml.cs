using PopTranslate.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace PopTranslate.App.Pages;

public sealed partial class OcrSettingsPage : Page
{
    public OcrSettingsPage()
    {
        InitializeComponent();
    }

    public OcrSettingsViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is OcrSettingsViewModel vm)
        {
            ViewModel = vm;
            Bindings.Update();
        }
    }

    private void VisionApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { IsLoaded: true } box &&
            ViewModel is not null &&
            !string.Equals(ViewModel.VisionApiKeyPlaintext, box.Password, StringComparison.Ordinal))
        {
            ViewModel.VisionApiKeyPlaintext = box.Password;
        }
    }
}
