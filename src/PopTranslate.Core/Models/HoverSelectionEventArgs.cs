namespace PopTranslate.Core.Models;

public sealed class HoverSelectionEventArgs : EventArgs
{
    public HoverSelectionEventArgs(
        string? previewText,
        bool hasUiaText,
        ScreenPoint anchorPoint,
        nint sourceWindowHandle,
        string? sourceProcessName = null)
    {
        PreviewText = previewText;
        HasUiaText = hasUiaText;
        AnchorPoint = anchorPoint;
        SourceWindowHandle = sourceWindowHandle;
        SourceProcessName = sourceProcessName;
    }

    public string? PreviewText { get; }

    public bool HasUiaText { get; }

    public ScreenPoint AnchorPoint { get; }

    public nint SourceWindowHandle { get; }

    public string? SourceProcessName { get; }
}
