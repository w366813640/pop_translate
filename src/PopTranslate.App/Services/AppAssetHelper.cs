using PopTranslate.Core.Models;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using WinUIEx;

namespace PopTranslate.App.Services;

internal static class AppAssetHelper
{
    public static string IconPath => Path.Combine(AppContext.BaseDirectory, "Assets", "poptranslate.ico");

    public static void TryApplyWindowIcon(Window window)
    {
        try
        {
            var iconPath = IconPath;
            if (File.Exists(iconPath))
            {
                window.SetIcon(iconPath);
            }
        }
        catch
        {
            // Never crash window initialization if icon loading fails.
        }
    }

    public static ElementTheme ToElementTheme(AppThemeMode mode) => mode switch
    {
        AppThemeMode.Light => ElementTheme.Light,
        AppThemeMode.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    public static void ApplyTheme(Window window, AppThemeMode mode)
    {
        if (window.Content is FrameworkElement root)
        {
            root.RequestedTheme = ToElementTheme(mode);
        }
    }

    public static bool TryCopyTextToClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
