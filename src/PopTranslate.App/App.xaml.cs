using System.Diagnostics;
using PopTranslate.App.Services;
using PopTranslate.Core.DependencyInjection;
using PopTranslate.Win32.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace PopTranslate.App;

public partial class App : Application
{
    private AppCoordinator? _coordinator;

    public IServiceProvider Services { get; }

    public App()
    {
        InitializeComponent();
        UnhandledException += OnAppUnhandledException;

        var services = new ServiceCollection();
        services.AddPopTranslateCore();
        services.AddPopTranslateWin32();
        services.AddPopTranslateApp();
        Services = services.BuildServiceProvider();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var singleInstance = Services.GetRequiredService<SingleInstanceGuard>();
        if (!singleInstance.TryAcquire())
        {
            singleInstance.SignalExistingInstance();
            Exit();
            return;
        }

        try
        {
            _coordinator = Services.GetRequiredService<AppCoordinator>();
            await _coordinator.InitializeAsync(DispatcherQueue.GetForCurrentThread());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PopTranslate] Startup initialization failed: {ex}");
            Exit();
        }
    }

    private static void OnAppUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Debug.WriteLine($"[PopTranslate] Unhandled UI exception: {e.Exception}");
    }
}
