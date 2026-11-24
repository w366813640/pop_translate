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
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        AppDiagnosticsLogger.Info("Lifecycle", "PopTranslate process initialized.");

        var services = new ServiceCollection();
        services.AddPopTranslateCore();
        services.AddPopTranslateWin32();
        services.AddPopTranslateApp();
        Services = services.BuildServiceProvider();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var cliArgs = GetCommandLineArguments();
        var singleInstance = Services.GetRequiredService<SingleInstanceGuard>();
        if (!singleInstance.TryAcquire())
        {
            singleInstance.SignalExistingInstance(cliArgs);
            Exit();
            return;
        }

        try
        {
            _coordinator = Services.GetRequiredService<AppCoordinator>();
            await _coordinator.InitializeAsync(DispatcherQueue.GetForCurrentThread(), cliArgs);
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Error("Startup", "Startup initialization failed.", ex);
            Exit();
        }
    }

    private static string[] GetCommandLineArguments()
    {
        try
        {
            var rawArgs = Environment.GetCommandLineArgs();
            return rawArgs.Length > 1 ? rawArgs[1..] : [];
        }
        catch
        {
            return [];
        }
    }

    private void OnAppUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        AppDiagnosticsLogger.Error("UIUnhandled", "Unhandled WinUI exception.", e.Exception);

        if (_coordinator is not null &&
            e.Exception is not OutOfMemoryException and not AccessViolationException)
        {
            e.Handled = true;
        }
    }

    private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        AppDiagnosticsLogger.Error(
            "AppDomain",
            $"Unhandled AppDomain exception (IsTerminating={e.IsTerminating}).",
            e.ExceptionObject as Exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppDiagnosticsLogger.Error("TaskScheduler", "Unobserved task exception.", e.Exception);
        e.SetObserved();
    }
}
