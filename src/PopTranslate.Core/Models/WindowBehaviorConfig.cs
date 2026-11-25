namespace PopTranslate.Core.Models;

public sealed class WindowBehaviorConfig
{
    public WindowPositionMode PositionMode { get; set; } = WindowPositionMode.FollowCursor;

    public int FixedX { get; set; } = 200;

    public int FixedY { get; set; } = 200;

    public double PopupWidth { get; set; } = 440;

    public double PopupMaxHeight { get; set; } = 520;

    public bool PinByDefault { get; set; }

    public bool HideOnClickOutside { get; set; } = true;

    public bool HideOnEscape { get; set; } = true;

    public bool AutoCopyFirstResult { get; set; }

    public AppThemeMode Theme { get; set; } = AppThemeMode.System;

    public WindowBehaviorConfig Clone() => new()
    {
        PositionMode = PositionMode,
        FixedX = FixedX,
        FixedY = FixedY,
        PopupWidth = PopupWidth,
        PopupMaxHeight = PopupMaxHeight,
        PinByDefault = PinByDefault,
        HideOnClickOutside = HideOnClickOutside,
        HideOnEscape = HideOnEscape,
        AutoCopyFirstResult = AutoCopyFirstResult,
        Theme = Theme,
    };
}
