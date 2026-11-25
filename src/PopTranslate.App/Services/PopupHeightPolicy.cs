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
    double MinHeightDips,
    double MaxHeightDips,
    double LookaheadStepDips = 72.0,
    double DeadbandDips = 6.0);

/// <summary>
/// Tight content-fit height policy for WinUI 3 Desktop Acrylic popup without any ghosting animations:
/// (1) Estimates a realistic initial height from source length and expanded card count (zero giant empty voids),
/// (2) Grows monotonically in modest ~3-line (72 DIPs) steps during active streaming,
/// (3) Tightens cleanly to exact measured content height once streaming completes or cards toggle.
/// </summary>
public static class PopupHeightPolicy
{
    public const double DefaultLookaheadStepDips = 72.0;

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
        var charsPerLine = Math.Max(20.0, effectiveTextWidth / 12.0);
        var sourceLen = Math.Max(1, input.SourceTextLength);

        var estimatedLines = sourceLen <= 25
            ? 2.0
            : Math.Ceiling(sourceLen / charsPerLine) + 1.0;

        // Card header + vertical padding (~74 DIPs) + estimated wrapped lines (~22 DIPs/line).
        var estimatedPerActiveCard = Math.Clamp(74.0 + estimatedLines * 22.0, 114.0, maxH);
        var collapsedCount = Math.Max(0, input.CollapsedOrOnDemandCardCount);
        var totalCards = input.ExpandedActiveCardCount + collapsedCount;
        var interCardSpacing = Math.Max(0, totalCards - 1) * 10.0;

        var estimatedCardsHeight =
            (input.ExpandedActiveCardCount * estimatedPerActiveCard) +
            (collapsedCount * 52.0) +
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
            // Strict grow-only while streaming so cards never clip or bounce vertically mid-generation.
            if (desired <= current)
            {
                return current;
            }

            var step = Math.Max(24.0, input.LookaheadStepDips);
            var stepped = Math.Max(desired, current + step);
            if (maxH - stepped <= step * 0.5)
            {
                stepped = maxH;
            }

            return Math.Min(maxH, stepped);
        }

        // Completed / idle / manual collapse-expand: tighten cleanly to exact content fit (zero empty bottom void).
        return Math.Abs(desired - current) > input.DeadbandDips
            ? desired
            : current;
    }
}
