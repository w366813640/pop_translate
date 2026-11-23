namespace PopTranslate.Core.Models;

public enum SelectionTriggerSource
{
    Hotkey = 0,
    HoverIcon = 1,
    ManualInput = 2,
    ScreenshotOcr = 3,
}

public enum SelectionCaptureMethod
{
    None = 0,
    UiAutomation = 1,
    ClipboardFallback = 2,
}
