using PopTranslate.App.ViewModels;
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
}
