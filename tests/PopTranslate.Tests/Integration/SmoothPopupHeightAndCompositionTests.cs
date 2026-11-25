using PopTranslate.App.Services;
using PopTranslate.App.ViewModels;
using PopTranslate.Core.Configuration;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Diff;
using PopTranslate.Core.Language;
using PopTranslate.Core.Models;
using Xunit;

namespace PopTranslate.Tests.Integration;

public sealed class SmoothPopupHeightAndCompositionTests
{
    [Fact]
    public void EstimateInitialHeightDips_ScalesUpForLongTextToPreventStreamingResizes_AndKeepsShortTextCompact()
    {
        // 1. Short word with 1 expanded card -> stays compact (~240-300 DIPs), not stretched to 640.
        var shortHeight = PopupHeightPolicy.EstimateInitialHeightDips(new PopupInitialHeightInput(
            BaseChromeHeightDips: 110,
            MeasuredCardsHeightDips: 64,
            SourceTextLength: 12,
            ExpandedActiveCardCount: 1,
            CollapsedOrOnDemandCardCount: 0,
            IsAnyCardLoading: true,
            PopupWidthDips: 460,
            MinHeightDips: 190,
            MaxHeightDips: 640));
        Assert.InRange(shortHeight, 220, 320);

        // 2. Long article (650 chars) with 1 expanded card -> opens straight at/near MaxHeight (0 streaming jumps!).
        var longHeight = PopupHeightPolicy.EstimateInitialHeightDips(new PopupInitialHeightInput(
            BaseChromeHeightDips: 110,
            MeasuredCardsHeightDips: 64,
            SourceTextLength: 650,
            ExpandedActiveCardCount: 1,
            CollapsedOrOnDemandCardCount: 1,
            IsAnyCardLoading: true,
            PopupWidthDips: 460,
            MinHeightDips: 190,
            MaxHeightDips: 640));
        Assert.InRange(longHeight, 560, 640);

        // 3. Cached/completed session (not loading) -> exact measured fit, never inflated by source length estimate.
        var completedHeight = PopupHeightPolicy.EstimateInitialHeightDips(new PopupInitialHeightInput(
            BaseChromeHeightDips: 110,
            MeasuredCardsHeightDips: 190,
            SourceTextLength: 650,
            ExpandedActiveCardCount: 0,
            CollapsedOrOnDemandCardCount: 0,
            IsAnyCardLoading: false,
            PopupWidthDips: 460,
            MinHeightDips: 190,
            MaxHeightDips: 640));
        Assert.Equal(300, completedHeight);
    }

    [Fact]
    public void ResolveDynamicHeightDips_StreamingIsMonotonicGrowOnlyWithLookaheadSteps()
    {
        var current = 240.0;
        var resizeCount = 0;

        // Simulate 20 incremental line wraps (+20 DIPs per line, from 240 -> 640 DIPs) while streaming.
        for (var desired = 260.0; desired <= 640.0; desired += 20.0)
        {
            var next = PopupHeightPolicy.ResolveDynamicHeightDips(new PopupDynamicHeightInput(
                CurrentHeightDips: current,
                MeasuredDesiredHeightDips: desired,
                IsAnyCardLoading: true,
                AllowShrink: false,
                MinHeightDips: 190,
                MaxHeightDips: 640));

            // Must NEVER shrink while streaming and must always fit desired content!
            Assert.True(next >= current, $"Height shrunk during streaming: {current} -> {next}");
            Assert.True(next >= desired, $"Height {next} is smaller than desired {desired}");

            if (Math.Abs(next - current) >= 1)
            {
                resizeCount++;
                current = next;
            }
        }

        // Lookahead step buffering must compress 20 line wraps into at most 4 smooth step resizes (instead of 20 janky jumps)!
        Assert.InRange(resizeCount, 1, 4);
        Assert.Equal(640.0, current);

        // Temporary height drop during streaming (e.g., reasoning placeholder swap) MUST NOT shrink window!
        var duringDrop = PopupHeightPolicy.ResolveDynamicHeightDips(new PopupDynamicHeightInput(
            CurrentHeightDips: 420,
            MeasuredDesiredHeightDips: 310,
            IsAnyCardLoading: true,
            AllowShrink: false,
            MinHeightDips: 190,
            MaxHeightDips: 640));
        Assert.Equal(420, duringDrop);
    }

    [Fact]
    public void ResolveDynamicHeightDips_AfterStreamSettlesOnlyOnMeaningfulShrinkOrExplicitToggle()
    {
        // Minor leftover slack (< settle threshold) keeps height rock-steady instead of snapping back slightly.
        var slightSlack = PopupHeightPolicy.ResolveDynamicHeightDips(new PopupDynamicHeightInput(
            CurrentHeightDips: 380,
            MeasuredDesiredHeightDips: 350,
            IsAnyCardLoading: false,
            AllowShrink: true,
            MinHeightDips: 190,
            MaxHeightDips: 640,
            ShrinkDeadbandDips: 48));
        Assert.Equal(380, slightSlack);

        // Substantial shrink (e.g., user collapses a tall card or large pre-reserved gap) shrinks cleanly to exact fit.
        var collapsedCard = PopupHeightPolicy.ResolveDynamicHeightDips(new PopupDynamicHeightInput(
            CurrentHeightDips: 540,
            MeasuredDesiredHeightDips: 260,
            IsAnyCardLoading: false,
            AllowShrink: true,
            MinHeightDips: 190,
            MaxHeightDips: 640,
            ShrinkDeadbandDips: 6));
        Assert.Equal(260, collapsedCard);
    }

    [Fact]
    public void ShouldAutoFollowScrollBottom_FollowsNearBottomAndPausesWhenUserScrollsUp()
    {
        // User at exact bottom or within 44 DIPs -> auto-follow enabled.
        Assert.True(PopupHeightPolicy.ShouldAutoFollowScrollBottom(verticalOffset: 200, scrollableHeight: 200));
        Assert.True(PopupHeightPolicy.ShouldAutoFollowScrollBottom(verticalOffset: 170, scrollableHeight: 200));

        // User scrolled up to read earlier paragraphs -> pause auto-follow so we never fight the user!
        Assert.False(PopupHeightPolicy.ShouldAutoFollowScrollBottom(verticalOffset: 80, scrollableHeight: 200));
        Assert.False(PopupHeightPolicy.ShouldAutoFollowScrollBottom(verticalOffset: 0, scrollableHeight: 200));

        // Not scrollable yet -> no-op.
        Assert.False(PopupHeightPolicy.ShouldAutoFollowScrollBottom(verticalOffset: 0, scrollableHeight: 0));
    }

    [Fact]
    public async Task PopupAndCardControls_WireHeightPolicyAndCompositionTransitions()
    {
        var repoRoot = FindRepoRoot();
        var popupCs = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "src", "PopTranslate.App", "Windows", "TranslationPopupWindow.xaml.cs"));
        var cardCs = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "src", "PopTranslate.App", "Controls", "TranslationResultCardControl.xaml.cs"));

        Assert.Contains("PopupHeightPolicy.EstimateInitialHeightDips", popupCs, StringComparison.Ordinal);
        Assert.Contains("PopupHeightPolicy.ResolveDynamicHeightDips", popupCs, StringComparison.Ordinal);
        Assert.Contains("PopupHeightPolicy.ShouldAutoFollowScrollBottom", popupCs, StringComparison.Ordinal);
        Assert.Contains("ElementCompositionPreview", popupCs, StringComparison.Ordinal);
        Assert.Contains("ElementCompositionPreview", cardCs, StringComparison.Ordinal);

        // ViewModel exposes IsAnyCardLoading cleanly for height policy decisions.
        Assert.NotNull(typeof(TranslationPopupViewModel).GetProperty(nameof(TranslationPopupViewModel.IsAnyCardLoading)));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PopTranslate.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Unable to locate PopTranslate.sln.");
    }
}
