using System.Drawing;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PopTranslate.Win32.Windowing;

public sealed class WindowHelperService : IWindowHelperService
{
    private const uint MonitorInfoPrimaryFlag = 0x00000001;
    private const double DpiScaleComparisonEpsilon = 0.005;
    private static readonly HWND HwndTopMost = new(-1);
    private static readonly HWND HwndNoTopMost = new(-2);

    public unsafe IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();

        MONITORENUMPROC callback = (hMonitor, _, _, _) =>
        {
            if (TryBuildMonitorInfo(hMonitor, out var info))
            {
                monitors.Add(info);
            }

            return true;
        };

        PInvoke.EnumDisplayMonitors(HDC.Null, (RECT*)null, callback, new LPARAM(0));

        if (monitors.Count == 0)
        {
            var fallback = BuildFallbackPrimaryMonitor();
            monitors.Add(fallback);
        }

        return monitors;
    }

    public MonitorInfo GetMonitorAtPoint(ScreenPoint point)
    {
        var hMonitor = PInvoke.MonitorFromPoint(
            new Point(point.X, point.Y),
            MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);

        if (hMonitor != HMONITOR.Null && TryBuildMonitorInfo(hMonitor, out var info))
        {
            return info;
        }

        var monitors = GetMonitors();
        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    public ScreenPoint GetCursorPosition()
    {
        return PInvoke.GetCursorPos(out var pt)
            ? new ScreenPoint(pt.X, pt.Y)
            : new ScreenPoint(0, 0);
    }

    public nint GetForegroundWindowHandle() => (nint)PInvoke.GetForegroundWindow();

    public ScreenPoint CalculateClampedWindowPosition(
        ScreenPoint anchorPoint,
        int windowWidthPx,
        int windowHeightPx,
        int cursorOffsetPx = 12)
    {
        var monitor = GetMonitorAtPoint(anchorPoint);
        return ClampPositionToWorkArea(anchorPoint, windowWidthPx, windowHeightPx, monitor.WorkArea, cursorOffsetPx);
    }

    public static ScreenPoint ClampPositionToWorkArea(
        ScreenPoint anchorPoint,
        int width,
        int height,
        ScreenRect workArea,
        int offsetPx)
    {
        var safeWidth = Math.Max(0, width);
        var safeHeight = Math.Max(0, height);

        var maxX = Math.Max(workArea.X, workArea.Right - safeWidth);
        var x = anchorPoint.X + offsetPx;
        if (x + safeWidth > workArea.Right)
        {
            var flippedX = anchorPoint.X - safeWidth - offsetPx;
            x = flippedX >= workArea.X ? flippedX : maxX;
        }

        x = Math.Clamp(x, workArea.X, maxX);

        var maxY = Math.Max(workArea.Y, workArea.Bottom - safeHeight);
        var y = anchorPoint.Y + offsetPx;
        if (y + safeHeight > workArea.Bottom)
        {
            var flippedY = anchorPoint.Y - safeHeight - offsetPx;
            y = flippedY >= workArea.Y ? flippedY : maxY;
        }

        y = Math.Clamp(y, workArea.Y, maxY);

        return new ScreenPoint(x, y);
    }

    public double GetDpiScaleForWindow(nint windowHandle)
    {
        if (windowHandle != nint.Zero)
        {
            var hwnd = new HWND(windowHandle);
            if (PInvoke.IsWindow(hwnd))
            {
                var dpi = PInvoke.GetDpiForWindow(hwnd);
                if (dpi >= 96)
                {
                    return dpi / 96.0;
                }
            }
        }

        var cursorMonitor = GetMonitorAtPoint(GetCursorPosition());
        return cursorMonitor.DpiScale;
    }

    public void ConfigureNoActivateOverlayWindow(nint windowHandle)
    {
        if (windowHandle == nint.Zero)
        {
            return;
        }

        var hwnd = new HWND(windowHandle);
        if (!PInvoke.IsWindow(hwnd))
        {
            return;
        }

        var currentExStyle = PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        var newExStyle =
            (currentExStyle & ~(nint)WINDOW_EX_STYLE.WS_EX_APPWINDOW) |
            (nint)(WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_TOPMOST);

        PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, newExStyle);
        PInvoke.SetWindowPos(
            hwnd,
            HwndTopMost,
            0,
            0,
            0,
            0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
            SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
    }

    public void ConfigurePopupToolWindow(nint windowHandle, bool hideFromTaskbar = true)
    {
        if (windowHandle == nint.Zero)
        {
            return;
        }

        var hwnd = new HWND(windowHandle);
        if (!PInvoke.IsWindow(hwnd))
        {
            return;
        }

        var currentExStyle = PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        nint newExStyle;
        if (hideFromTaskbar)
        {
            newExStyle =
                (currentExStyle & ~(nint)WINDOW_EX_STYLE.WS_EX_APPWINDOW) |
                (nint)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
        }
        else
        {
            newExStyle =
                (currentExStyle & ~(nint)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) |
                (nint)WINDOW_EX_STYLE.WS_EX_APPWINDOW;
        }

        PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, newExStyle);
        PInvoke.SetWindowPos(
            hwnd,
            HWND.Null,
            0,
            0,
            0,
            0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
            SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
    }

    public void SetWindowTopMost(nint windowHandle, bool topMost)
    {
        if (windowHandle == nint.Zero)
        {
            return;
        }

        var hwnd = new HWND(windowHandle);
        if (!PInvoke.IsWindow(hwnd))
        {
            return;
        }

        PInvoke.SetWindowPos(
            hwnd,
            topMost ? HwndTopMost : HwndNoTopMost,
            0,
            0,
            0,
            0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    public void MoveAndResizeWindow(nint windowHandle, int x, int y, int width, int height, bool activate = false)
    {
        if (windowHandle == nint.Zero)
        {
            return;
        }

        var hwnd = new HWND(windowHandle);
        if (!PInvoke.IsWindow(hwnd))
        {
            return;
        }

        var safeWidth = Math.Max(1, width);
        var safeHeight = Math.Max(1, height);

        var initialScale = GetDpiScaleForWindow(windowHandle);
        var targetMonitor = GetMonitorAtPoint(new ScreenPoint(x, y));
        var crossingDpiBoundary = Math.Abs(targetMonitor.DpiScale - initialScale) > DpiScaleComparisonEpsilon;

        if (crossingDpiBoundary)
        {
            // Move window origin onto target monitor first without resizing so synchronous WM_DPICHANGED
            // completes before we apply the caller's target-DPI physical pixel dimensions.
            PInvoke.SetWindowPos(
                hwnd,
                HWND.Null,
                x,
                y,
                0,
                0,
                SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        }

        var scaleBeforeResize = GetDpiScaleForWindow(windowHandle);
        var flags = SET_WINDOW_POS_FLAGS.SWP_NOZORDER;
        if (!activate)
        {
            flags |= SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE;
        }

        PInvoke.SetWindowPos(hwnd, HWND.Null, x, y, safeWidth, safeHeight, flags);

        if (crossingDpiBoundary &&
            Math.Abs(GetDpiScaleForWindow(windowHandle) - scaleBeforeResize) > DpiScaleComparisonEpsilon)
        {
            // If an oversized prior frame kept >50% area on the old monitor until the resize completed,
            // re-apply exact target pixel bounds after the deferred WM_DPICHANGED fires.
            PInvoke.SetWindowPos(hwnd, HWND.Null, x, y, safeWidth, safeHeight, flags);
        }
    }

    public void ShowWindowNoActivate(nint windowHandle)
    {
        if (windowHandle == nint.Zero)
        {
            return;
        }

        var hwnd = new HWND(windowHandle);
        if (!PInvoke.IsWindow(hwnd))
        {
            return;
        }

        PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
    }

    public unsafe void ForceForegroundWindow(nint windowHandle)
    {
        if (windowHandle == nint.Zero)
        {
            return;
        }

        var hwnd = new HWND(windowHandle);
        if (!PInvoke.IsWindow(hwnd))
        {
            return;
        }

        PInvoke.ShowWindow(hwnd, PInvoke.IsIconic(hwnd) ? SHOW_WINDOW_CMD.SW_RESTORE : SHOW_WINDOW_CMD.SW_SHOW);
        if (PInvoke.SetForegroundWindow(hwnd))
        {
            return;
        }

        var foregroundHwnd = PInvoke.GetForegroundWindow();
        uint currentThreadId = PInvoke.GetCurrentThreadId();
        uint targetThreadId = PInvoke.GetWindowThreadProcessId(hwnd, null);
        uint foregroundThreadId = foregroundHwnd != HWND.Null
            ? PInvoke.GetWindowThreadProcessId(foregroundHwnd, null)
            : 0;

        bool attachedToForeground = false;
        bool attachedToTarget = false;
        try
        {
            if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
            {
                attachedToForeground = PInvoke.AttachThreadInput(currentThreadId, foregroundThreadId, true);
            }

            if (targetThreadId != 0 && targetThreadId != currentThreadId && targetThreadId != foregroundThreadId)
            {
                attachedToTarget = PInvoke.AttachThreadInput(currentThreadId, targetThreadId, true);
            }

            PInvoke.BringWindowToTop(hwnd);
            PInvoke.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attachedToTarget)
            {
                PInvoke.AttachThreadInput(currentThreadId, targetThreadId, false);
            }

            if (attachedToForeground)
            {
                PInvoke.AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }
        }
    }

    private static unsafe bool TryBuildMonitorInfo(HMONITOR hMonitor, out MonitorInfo monitorInfo)
    {
        var miEx = new MONITORINFOEXW
        {
            monitorInfo = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFOEXW) },
        };

        if (!PInvoke.GetMonitorInfo(hMonitor, (MONITORINFO*)&miEx))
        {
            monitorInfo = default!;
            return false;
        }

        uint dpiX = 96;
        uint dpiY = 96;
        if (PInvoke.GetDpiForMonitor(hMonitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var rawDpiX, out var rawDpiY).Succeeded)
        {
            if (rawDpiX > 0) dpiX = rawDpiX;
            if (rawDpiY > 0) dpiY = rawDpiY;
        }

        var rcMonitor = miEx.monitorInfo.rcMonitor;
        var rcWork = miEx.monitorInfo.rcWork;
        var bounds = new ScreenRect(
            rcMonitor.left,
            rcMonitor.top,
            rcMonitor.right - rcMonitor.left,
            rcMonitor.bottom - rcMonitor.top);
        var workArea = new ScreenRect(
            rcWork.left,
            rcWork.top,
            rcWork.right - rcWork.left,
            rcWork.bottom - rcWork.top);

        var deviceName = miEx.szDevice.ToString();
        var isPrimary = (miEx.monitorInfo.dwFlags & MonitorInfoPrimaryFlag) != 0;

        monitorInfo = new MonitorInfo(
            Handle: (nint)hMonitor.Value,
            DeviceName: deviceName,
            Bounds: bounds,
            WorkArea: workArea,
            DpiX: dpiX,
            DpiY: dpiY,
            IsPrimary: isPrimary);

        return true;
    }

    private static MonitorInfo BuildFallbackPrimaryMonitor()
    {
        var width = Math.Max(1, PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSCREEN));
        var height = Math.Max(1, PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSCREEN));
        var rect = new ScreenRect(0, 0, width, height);
        return new MonitorInfo(
            Handle: nint.Zero,
            DeviceName: @"\\.\DISPLAY1",
            Bounds: rect,
            WorkArea: rect,
            DpiX: 96,
            DpiY: 96,
            IsPrimary: true);
    }
}
