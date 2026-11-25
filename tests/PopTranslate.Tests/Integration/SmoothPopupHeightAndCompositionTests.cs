using PopTranslate.App.Services;
using PopTranslate.Core.Models;
using Xunit;

namespace PopTranslate.Tests.Integration;

public sealed class SmoothPopupHeightAndCompositionTests
{
    [Fact]
    public void ResolvePanelHeightDips_UsesRaycastTwoStateFixedGeometry_NeverResizesDuringStreaming()
    {
        // 1. Compact idle input state (no cards, no OCR, no guard, no banner) -> fixed compact height (230 DIPs).
        var idleHeight = PopupHeightPolicy.ResolvePanelHeightDips(
            hasExpandedContent: false,
            configuredPanelHeightDips: 520,
            minHeightDips: 190,
            maxHeightDips: 1000);
        Assert.Equal(PopupHeightPolicy.CompactIdleHeightDips, idleHeight);

        // 2. Results / streaming state -> ONE fixed rock-solid panel height (zero token-by-token resizing!).
        var streamingStartHeight = PopupHeightPolicy.ResolvePanelHeightDips(
            hasExpandedContent: true,
            configuredPanelHeightDips: 520,
            minHeightDips: 190,
            maxHeightDips: 1000);
        var streamingMidHeight = PopupHeightPolicy.ResolvePanelHeightDips(
            hasExpandedContent: true,
            configuredPanelHeightDips: 520,
            minHeightDips: 190,
            maxHeightDips: 1000);
        var streamingCompletedHeight = PopupHeightPolicy.ResolvePanelHeightDips(
            hasExpandedContent: true,
            configuredPanelHeightDips: 520,
            minHeightDips: 190,
            maxHeightDips: 1000);

        Assert.Equal(520.0, streamingStartHeight);
        Assert.Equal(streamingStartHeight, streamingMidHeight);
        Assert.Equal(streamingStartHeight, streamingCompletedHeight);

        // 3. Respects user-customized panel height within valid bounds (320..1000).
        Assert.Equal(680.0, PopupHeightPolicy.ResolvePanelHeightDips(true, 680, 190, 1000));
        Assert.Equal(320.0, PopupHeightPolicy.ResolvePanelHeightDips(true, 200, 190, 1000));
        Assert.Equal(1000.0, PopupHeightPolicy.ResolvePanelHeightDips(true, 1400, 190, 1000));
    }

    [Fact]
    public void WindowBehaviorConfig_DefaultPopupHeight_UsesComfortableRaycastPanelHeight()
    {
        var config = new WindowBehaviorConfig();
        Assert.Equal(520.0, config.PopupMaxHeight);
    }

    [Fact]
    public async Task PopupAndCardControls_ContainZeroGhostingOffsetOrClipAnimations_AndUseAutoScrollbar()
    {
        var repoRoot = FindRepoRoot();
        var popupXaml = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "src", "PopTranslate.App", "Windows", "TranslationPopupWindow.xaml"));
        var popupCs = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "src", "PopTranslate.App", "Windows", "TranslationPopupWindow.xaml.cs"));
        var cardCs = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "src", "PopTranslate.App", "Controls", "TranslationResultCardControl.xaml.cs"));

        // Must NOT have ghosting-prone ImplicitAnimations[Offset] or InsetClip reveal hacks!
        Assert.DoesNotContain("ImplicitAnimations", cardCs, StringComparison.Ordinal);
        Assert.DoesNotContain("ElementCompositionPreview", cardCs, StringComparison.Ordinal);
        Assert.DoesNotContain("InsetClip", popupCs, StringComparison.Ordinal);
        Assert.DoesNotContain("ElementCompositionPreview", popupCs, StringComparison.Ordinal);

        // Popup uses clean Raycast fixed-panel policy with borderless wheel-scrollable CardsScrollViewer.
        Assert.Contains("PopupHeightPolicy.ResolvePanelHeightDips", popupCs, StringComparison.Ordinal);
        Assert.Contains("VerticalScrollMode=\"Auto\"", popupXaml, StringComparison.Ordinal);
        Assert.Contains("VerticalScrollBarVisibility=\"Hidden\"", popupXaml, StringComparison.Ordinal);
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
