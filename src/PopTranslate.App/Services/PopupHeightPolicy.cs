namespace PopTranslate.App.Services;

public readonly record struct PopupInitialHeightInput(
    double BaseChromeHeightDips,
    double MeasuredCardsHeightDips,
    int SourceTextLength,
    int ExpandedActiveCardCount,
    int CollapsedOrOnDemandCardCount,
    bool IsAnyCardLoading,
    double PopupWidthDips,
    double MinHeightDips,
    double MaxHeightDips);

public readonly record struct PopupDynamicHeightInput(
    double CurrentHeightDips,
    double MeasuredDesiredHeightDips,
    bool IsAnyCardLoading,
    bool AllowShrink,
    double MinHeightDips,
    double MaxHeightDips,
    double LookaheadStepDips = 120.0,
    double GrowDeadbandDips = 6.0,
    double ShrinkDeadbandDips = 6.0);

/// <summary>
/// WinUI 3 anti-jank popup height &amp; scroll-follow policy for Desktop Acrylic streaming windows.
/// Minimizes HWND <c>AppWindow.Resize</c> churn (which forces DWM Acrylic reallocation) by:
/// (1) pre-estimating comfortable initial height from source length for long articles,
/// (2) growing monotonically with generous lookahead steps during active streaming,
/// (3) settling cleanly only when meaningful slack remains or on explicit user expand/collapse, and
/// (4) auto-following ScrollViewer bottom only while the user remains near the bottom.
/// </summary>
public static class PopupHeightPolicy
{
    public const double DefaultLookaheadStepDips = 120.0;
    public const double DefaultPostStreamShrinkDeadbandDips = 48.0;
    public const double DefaultScrollFollowToleranceDips = 44.0;

    public static double EstimateInitialHeightDips(PopupInitialHeightInput input)
    {
        var minH = Math.Max(120.0, input.MinHeightDips);
        var maxH = Math.Max(minH, input.MaxHeightDips);
        var measuredTotal = input.BaseChromeHeightDips + Math.Max(0.0, input.MeasuredCardsHeightDips);

        if (!input.IsAnyCardLoading || input.ExpandedActiveCardCount <= 0)
        {
            return Math.Clamp(measuredTotal, minH, maxH);
        }

        var effectiveTextWidth = Math.Max(220.0, input.PopupWidthDips - 64.0);
        var charsPerLine = Math.Max(18.0, effectiveTextWidth / 11.5);
        var sourceLen = Math.Max(1, input.SourceTextLength);

        var estimatedLines = sourceLen <= 30
            ? Math.Max(2.5, Math.Ceiling(sourceLen / charsPerLine) + 1.5)
            : Math.Ceiling(sourceLen / charsPerLine) + 1.75;

        // Card header + vertical padding (~74 DIPs) + estimated wrapped lines (~22 DIPs/line).
        var estimatedPerActiveCard = Math.Clamp(74.0 + estimatedLines * 22.0, 112.0, maxH);
        var totalCards = input.ExpandedActiveCardCount + Math.Max(0, input.CollapsedOrOnDemandCardCount);
        var interCardSpacing = Math.Max(0, totalCards - 1) * 10.0;

        var estimatedCardsHeight =
            (input.ExpandedActiveCardCount * estimatedPerActiveCard) +
            (Math.Max(0, input.CollapsedOrOnDemandCardCount) * 54.0) +
            interCardSpacing;

        var effectiveCardsHeight = Math.Max(Math.Max(0.0, input.MeasuredCardsHeightDips), estimatedCardsHeight);
        return Math.Clamp(input.BaseChromeHeightDips + effectiveCardsHeight, minH, maxH);
    }

    public static double ResolveDynamicHeightDips(PopupDynamicHeightInput input)
    {
        var minH = Math.Max(120.0, input.MinHeightDips);
        var maxH = Math.Max(minH, input.MaxHeightDips);
        var current = Math.Clamp(input.CurrentHeightDips, minH, maxH);
        var desired = Math.Clamp(input.MeasuredDesiredHeightDips, minH, maxH);

        if (input.IsAnyCardLoading)
        {
            // Strict grow-only during active streaming so multi-card / reasoning transitions never bounce vertically.
            if (desired <= current + input.GrowDeadbandDips)
            {
                return current;
            }

            var lookahead = Math.Max(24.0, input.LookaheadStepDips);
            var stepped = Math.Max(desired, current + lookahead);

            // If we are within half a step of MaxHeight, jump directly to MaxHeight to avoid a tiny trailing resize.
            if (maxH - stepped <= lookahead * 0.5)
            {
                stepped = maxH;
            }

            return Math.Min(maxH, stepped);
        }

        if (desired > current + input.GrowDeadbandDips)
        {
            return desired;
        }

        if (input.AllowShrink && desired < current - input.ShrinkDeadbandDips)
        {
            return desired;
        }

        return current;
    }

    public static bool ShouldAutoFollowScrollBottom(
        double verticalOffset,
        double scrollableHeight,
        double toleranceDips = DefaultScrollFollowToleranceDips)
    {
        if (scrollableHeight <= 4.0)
        {
            return false;
        }

        return (scrollableHeight - verticalOffset) <= Math.Max(4.0, toleranceDips);
    }
}
