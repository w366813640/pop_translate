using PopTranslate.Core.Contracts;
using PopTranslate.Win32.Guards;
using PopTranslate.Win32.Hotkeys;
using PopTranslate.Win32.Ocr;
using PopTranslate.Win32.Screenshot;
using PopTranslate.Win32.Selection;
using PopTranslate.Win32.Startup;
using PopTranslate.Win32.Windowing;
using Microsoft.Extensions.DependencyInjection;

namespace PopTranslate.Win32.DependencyInjection;

public static class Win32ServiceCollectionExtensions
{
    /// <summary>
    /// Registers PopTranslate.Win32 native Windows 11 services and WindowsOcrProvider.
    /// </summary>
    public static IServiceCollection AddPopTranslateWin32(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IForegroundAppGuard, ForegroundAppGuard>();
        services.AddSingleton<IWindowHelperService, WindowHelperService>();
        services.AddSingleton<IGlobalHotkeyService, GlobalHotkeyService>();

        services.AddSingleton<UiAutomationSelectionReader>();
        services.AddSingleton<ClipboardBackupService>();
        services.AddSingleton<ClipboardFallbackSelectionReader>();
        services.AddSingleton<ITextSelectionService, TextSelectionService>();
        services.AddSingleton<ISelectionHoverMonitor, MouseSelectionHoverMonitor>();

        services.AddSingleton<IScreenshotCaptureService, ScreenshotCaptureService>();
        services.AddSingleton<IStartupPrivilegeService, StartupPrivilegeService>();

        // Multi-registration alongside VisionLlmOcrProvider from PopTranslate.Core
        services.AddSingleton<IOcrProvider, WindowsOcrProvider>();

        return services;
    }
}
