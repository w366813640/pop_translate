using PopTranslate.Core.Models;

namespace PopTranslate.Core.Contracts;

public sealed record NativeContextMenuItem(
    int Id,
    string Text = "",
    bool IsSeparator = false,
    bool IsChecked = false,
    bool IsEnabled = true);

public interface IWindowHelperService
{
    IReadOnlyList<MonitorInfo> GetMonitors();

    MonitorInfo GetMonitorAtPoint(ScreenPoint point);

    ScreenPoint GetCursorPosition();

    nint GetForegroundWindowHandle();

    ScreenPoint CalculateClampedWindowPosition(
        ScreenPoint anchorPoint,
        int windowWidthPx,
        int windowHeightPx,
        int cursorOffsetPx = 12);

    ScreenPoint CalculateCenteredWindowPosition(
        ScreenPoint referencePoint,
        int windowWidthPx,
        int windowHeightPx);

    double GetDpiScaleForWindow(nint windowHandle);

    void ConfigureNoActivateOverlayWindow(nint windowHandle);

    void ConfigurePopupToolWindow(nint windowHandle, bool hideFromTaskbar = true);

    void SetWindowTopMost(nint windowHandle, bool topMost);

    void MoveAndResizeWindow(nint windowHandle, int x, int y, int width, int height, bool activate = false);

    void ShowWindowNoActivate(nint windowHandle);

    void ForceForegroundWindow(nint windowHandle);

    int ShowNativeContextMenu(
        nint ownerWindowHandle,
        ScreenPoint screenPoint,
        IReadOnlyList<NativeContextMenuItem> items) => 0;

    void TrimProcessWorkingSet() { }
}
