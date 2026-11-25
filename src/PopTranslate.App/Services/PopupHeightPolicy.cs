namespace PopTranslate.App.Services;

/// <summary>
/// Raycast-style two-state fixed popup panel height policy for WinUI 3 + Desktop Acrylic.
/// Avoids all token-by-token HWND resizing and compositor clip/offset lag during streaming:
/// (1) Compact idle input state stays at a crisp 230 DIPs;
/// (2) Active / results state uses one stable, user-resizable fixed panel height (default 520 DIPs)
///     with native ScrollViewer scrolling for long translations.
/// </summary>
public static class PopupHeightPolicy
{
    public const double CompactIdleHeightDips = 230.0;
    public const double DefaultPanelHeightDips = 520.0;
    public const double MinExpandedPanelHeightDips = 320.0;

    public static double ResolvePanelHeightDips(
        bool hasExpandedContent,
        double configuredPanelHeightDips,
        double minHeightDips = 190.0,
        double maxHeightDips = 1000.0)
    {
        var safeMin = Math.Max(160.0, minHeightDips);
        var safeMax = Math.Max(safeMin, maxHeightDips);

        if (!hasExpandedContent)
        {
            return Math.Clamp(CompactIdleHeightDips, safeMin, safeMax);
        }

        var rawPanel = configuredPanelHeightDips > 0 && !double.IsNaN(configuredPanelHeightDips)
            ? configuredPanelHeightDips
            : DefaultPanelHeightDips;
        var expandedMin = Math.Min(safeMax, Math.Max(safeMin, MinExpandedPanelHeightDips));
        return Math.Clamp(rawPanel, expandedMin, safeMax);
    }
}
