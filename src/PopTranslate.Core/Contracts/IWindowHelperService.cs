using PopTranslate.Core.Models;

namespace PopTranslate.Core.Contracts;

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

    double GetDpiScaleForWindow(nint windowHandle);

    void ConfigureNoActivateOverlayWindow(nint windowHandle);

    void ConfigurePopupToolWindow(nint windowHandle, bool hideFromTaskbar = true);

    void SetWindowTopMost(nint windowHandle, bool topMost);

    void MoveAndResizeWindow(nint windowHandle, int x, int y, int width, int height, bool activate = false);

    void ShowWindowNoActivate(nint windowHandle);

    void ForceForegroundWindow(nint windowHandle);
}
