using PopTranslate.App.Services;
using PopTranslate.Core.Models;
using Xunit;

namespace PopTranslate.Tests.Integration;

public sealed class SmoothPopupHeightAndCompositionTests
{
    [Fact]
    public void EstimateInitialHeightDips_FitsTightlyWithoutRedundantBlankSpace_AndScalesOnlyForLongText()
    {
        // 1. Exact screenshot scenario: 80-char source, 1 expanded card + 2 collapsed cards, maxHeight=640.
        //    Must stay tight (~340-440 DIPs), NEVER blindly stretched to 640 with 240px empty bottom void!
        var mediumHeight = PopupHeightPolicy.EstimateInitialHeightDips(new PopupInitialHeightInput(
            BaseChromeHeightDips: 110,
            MeasuredCardsHeightDips: 64,
            SourceTextLength: 80,
            ExpandedActiveCardCount: 1,
            CollapsedOrOnDemandCardCount: 2,
            IsAnyCardLoading: true,
            PopupWidthDips: 460,
            MinHeightDips: 190,
            MaxHeightDips: 640));
        Assert.InRange(mediumHeight, 330, 445);

        // 2. Short word (10 chars), 1 expanded card -> very compact (~220-290 DIPs).
        var shortHeight = PopupHeightPolicy.EstimateInitialHeightDips(new PopupInitialHeightInput(
            BaseChromeHeightDips: 110,
            MeasuredCardsHeightDips: 64,
            SourceTextLength: 10,
            ExpandedActiveCardCount: 1,
            CollapsedOrOnDemandCardCount: 0,
            IsAnyCardLoading: true,
            PopupWidthDips: 460,
            MinHeightDips: 190,
            MaxHeightDips: 640));
        Assert.InRange(shortHeight, 210, 295);

        // 3. Long article (700 chars), 1 expanded card -> scales up toward MaxHeight to avoid repeated jumps.
        var longHeight = PopupHeightPolicy.EstimateInitialHeightDips(new PopupInitialHeightInput(
            BaseChromeHeightDips: 110,
            MeasuredCardsHeightDips: 64,
            SourceTextLength: 700,
            ExpandedActiveCardCount: 1,
            CollapsedOrOnDemandCardCount: 1,
            IsAnyCardLoading: true,
            PopupWidthDips: 460,
            MinHeightDips: 190,
            MaxHeightDips: 640));
        Assert.InRange(longHeight, 520, 640);

        // 4. Completed / idle state -> exact measured fit (ZERO redundant space!).
        var exactCompleted = PopupHeightPolicy.EstimateInitialHeightDips(new PopupInitialHeightInput(
            BaseChromeHeightDips: 110,
            MeasuredCardsHeightDips: 295,
            SourceTextLength: 80,
            ExpandedActiveCardCount: 0,
            CollapsedOrOnDemandCardCount: 2,
            IsAnyCardLoading: false,
            PopupWidthDips: 460,
            MinHeightDips: 190,
            MaxHeightDips: 640));
        Assert.Equal(405.0, exactCompleted);
    }

    [Fact]
    public void ResolveDynamicHeightDips_GrowsMonotonicallyWhileStreaming_AndTightensToExactFitWhenDone()
    {
        var current = 250.0;
        var resizeCount = 0;

        // Streaming line-by-line growth from 270 -> 610 DIPs: grow-only in modest steps (no shrinking, far fewer jumps).
        for (var desired = 270.0; desired <= 610.0; desired += 20.0)
        {
            var next = PopupHeightPolicy.ResolveDynamicHeightDips(new PopupDynamicHeightInput(
                CurrentHeightDips: current,
                MeasuredDesiredHeightDips: desired,
                IsAnyCardLoading: true,
                MinHeightDips: 190,
                MaxHeightDips: 640));

            Assert.True(next >= current, $"Height shrunk during streaming: {current} -> {next}");
            Assert.True(next >= desired, $"Height {next} clipped desired {desired}");
            if (Math.Abs(next - current) >= 1.0)
            {
                resizeCount++;
                current = next;
            }
        }

        Assert.InRange(resizeCount, 1, 6);

        // Temporary mid-stream dip must NEVER shrink window.
        var midStreamDip = PopupHeightPolicy.ResolveDynamicHeightDips(new PopupDynamicHeightInput(
            CurrentHeightDips: 420,
            MeasuredDesiredHeightDips: 310,
            IsAnyCardLoading: true,
            MinHeightDips: 190,
            MaxHeightDips: 640));
        Assert.Equal(420.0, midStreamDip);

        // Once streaming completes, any leftover redundant space tightens cleanly to exact content fit!
        var tightenedAfterStream = PopupHeightPolicy.ResolveDynamicHeightDips(new PopupDynamicHeightInput(
            CurrentHeightDips: 480,
            MeasuredDesiredHeightDips: 405,
            IsAnyCardLoading: false,
            MinHeightDips: 190,
            MaxHeightDips: 640));
        Assert.Equal(405.0, tightenedAfterStream);
    }

    [Fact]
    public async Task PopupAndCardControls_HaveZeroGhostingAnimationsAndExactTightFit()
    {
        var repoRoot = FindRepoRoot();
        var popupXaml = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "src", "PopTranslate.App", "Windows", "TranslationPopupWindow.xaml"));
        var popupCs = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "src", "PopTranslate.App", "Windows", "TranslationPopupWindow.xaml.cs"));
        var cardCs = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "src", "PopTranslate.App", "Controls", "TranslationResultCardControl.xaml.cs"));

        // Zero ghosting-prone ImplicitAnimations or InsetClip hacks!
        Assert.DoesNotContain("ImplicitAnimations", cardCs, StringComparison.Ordinal);
        Assert.DoesNotContain("ElementCompositionPreview", cardCs, StringComparison.Ordinal);
        Assert.DoesNotContain("InsetClip", popupCs, StringComparison.Ordinal);
        Assert.DoesNotContain("ElementCompositionPreview", popupCs, StringComparison.Ordinal);

        // Uses tight policy + keeps borderless hidden scrollbar with Auto wheel scroll.
        Assert.Contains("PopupHeightPolicy.EstimateInitialHeightDips", popupCs, StringComparison.Ordinal);
        Assert.Contains("PopupHeightPolicy.ResolveDynamicHeightDips", popupCs, StringComparison.Ordinal);
        Assert.Contains("VerticalScrollMode=\"Auto\"", popupXaml, StringComparison.Ordinal);
        Assert.Contains("VerticalScrollBarVisibility=\"Hidden\"", popupXaml, StringComparison.Ordinal);

        // Default max height cap remains 640 DIPs.
        Assert.Equal(640.0, new WindowBehaviorConfig().PopupMaxHeight);
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
