namespace PopTranslate.Core.Models;

public sealed class HotkeyPressedEventArgs : EventArgs
{
    public HotkeyPressedEventArgs(
        HotkeyAction action,
        HotkeyGesture gesture,
        ScreenPoint cursorPosition,
        nint foregroundWindowHandle)
    {
        Action = action;
        Gesture = gesture;
        CursorPosition = cursorPosition;
        ForegroundWindowHandle = foregroundWindowHandle;
    }

    public HotkeyAction Action { get; }

    public HotkeyGesture Gesture { get; }

    public ScreenPoint CursorPosition { get; }

    public nint ForegroundWindowHandle { get; }
}
