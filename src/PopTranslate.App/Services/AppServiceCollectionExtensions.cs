using PopTranslate.App.ViewModels;
using PopTranslate.App.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace PopTranslate.App.Services;

public static class AppServiceCollectionExtensions
{
    public static IServiceCollection AddPopTranslateApp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Lifecycle & System Tray Services
        services.AddSingleton<SingleInstanceGuard>();
        services.AddSingleton<TrayIconHost>();
        services.AddSingleton<AppCoordinator>();

        // ViewModels
        services.AddSingleton<TranslationPopupViewModel>();
        services.AddSingleton<GeneralSettingsViewModel>();
        services.AddSingleton<ProvidersSettingsViewModel>();
        services.AddSingleton<OcrSettingsViewModel>();
        services.AddSingleton<HotkeysSettingsViewModel>();
        services.AddSingleton<NetworkSettingsViewModel>();
        services.AddSingleton<SettingsViewModel>();

        // WinUI 3 Windows (instantiated lazily on UI thread during AppCoordinator.InitializeAsync / OpenSettingsAsync)
        services.AddSingleton<FloatingHoverIconWindow>();
        services.AddSingleton<TranslationPopupWindow>();
        services.AddSingleton<SettingsWindow>();

        return services;
    }
}
