using PopTranslate.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace PopTranslate.App.Pages;

public sealed partial class NetworkSettingsPage : Page
{
    public NetworkSettingsPage()
    {
        InitializeComponent();
    }

    public NetworkSettingsViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is NetworkSettingsViewModel vm)
        {
            ViewModel = vm;
            Bindings.Update();
        }
    }
}
