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

        // WinUI 3 Windows
        // FloatingHoverIconWindow is lazily pre-warmed only when hover icon is enabled;
        // TranslationPopupWindow stays pre-warmed for <30ms open latency;
        // SettingsWindow is transient so closing it tears down Mica and all XAML pages.
        services.AddSingleton<FloatingHoverIconWindow>();
        services.AddSingleton<TranslationPopupWindow>();
        services.AddTransient<SettingsWindow>();

        return services;
    }
}
