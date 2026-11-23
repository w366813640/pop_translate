using PopTranslate.App.ViewModels;
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
}
