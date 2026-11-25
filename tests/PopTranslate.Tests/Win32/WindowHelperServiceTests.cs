using System.Runtime.InteropServices;
using PopTranslate.Core.Models;
using PopTranslate.Win32.Windowing;
using Xunit;

namespace PopTranslate.Tests.Win32;

public sealed class WindowHelperServiceTests
{
    private readonly WindowHelperService _service = new();

    [Fact]
    public void ClampPositionToWorkArea_InsideBounds_OffsetsFromAnchor()
    {
        var workArea = new ScreenRect(0, 0, 1920, 1040);
        var pos = WindowHelperService.ClampPositionToWorkArea(
            new ScreenPoint(300, 200),
            width: 440,
            height: 320,
            workArea: workArea,
            offsetPx: 12);

        Assert.Equal(new ScreenPoint(312, 212), pos);
    }

    [Fact]
    public void ClampPositionToWorkArea_RightEdgeOverflow_FlipsLeftAndStaysInWorkArea()
    {
        var workArea = new ScreenRect(0, 0, 1920, 1040);
        var pos = WindowHelperService.ClampPositionToWorkArea(
            new ScreenPoint(1800, 200),
            width: 440,
            height: 320,
            workArea: workArea,
            offsetPx: 12);

        Assert.Equal(1800 - 440 - 12, pos.X);
        Assert.Equal(212, pos.Y);
        Assert.True(pos.X >= workArea.X);
        Assert.True(pos.X + 440 <= workArea.Right);
    }

    [Fact]
    public void ClampPositionToWorkArea_BottomEdgeOverflow_FlipsAboveAnchor()
    {
        var workArea = new ScreenRect(0, 0, 1920, 1040);
        var pos = WindowHelperService.ClampPositionToWorkArea(
            new ScreenPoint(300, 960),
            width: 440,
            height: 320,
            workArea: workArea,
            offsetPx: 12);

        Assert.Equal(312, pos.X);
        Assert.Equal(960 - 320 - 12, pos.Y);
        Assert.True(pos.Y >= workArea.Y);
        Assert.True(pos.Y + 320 <= workArea.Bottom);
    }

    [Fact]
    public void ClampPositionToWorkArea_NegativeLeftSecondaryMonitor_ClampsWithinMonitor()
    {
        var leftMonitorWorkArea = new ScreenRect(-1920, 0, 1920, 1040);
        var pos = WindowHelperService.ClampPositionToWorkArea(
            new ScreenPoint(-80, 920),
            width: 440,
            height: 320,
            workArea: leftMonitorWorkArea,
            offsetPx: 12);

        Assert.True(pos.X >= leftMonitorWorkArea.X);
        Assert.True(pos.X + 440 <= leftMonitorWorkArea.Right);
        Assert.True(pos.Y >= leftMonitorWorkArea.Y);
        Assert.True(pos.Y + 320 <= leftMonitorWorkArea.Bottom);
    }

    [Fact]
    public void ClampPositionToWorkArea_OversizedWindow_ClampsTopLeftToWorkAreaOrigin()
    {
        var workArea = new ScreenRect(100, 50, 800, 600);
        var pos = WindowHelperService.ClampPositionToWorkArea(
            new ScreenPoint(500, 400),
            width: 1200,
            height: 900,
            workArea: workArea,
            offsetPx: 12);

        Assert.Equal(new ScreenPoint(100, 50), pos);
    }

    [Fact]
    public void GetMonitors_LiveSystem_ReturnsAtLeastOneValidPrimaryMonitor()
    {
        var monitors = _service.GetMonitors();

        Assert.NotEmpty(monitors);
        Assert.Contains(monitors, m => m.IsPrimary);
        foreach (var monitor in monitors)
        {
            Assert.False(monitor.Bounds.IsEmpty);
            Assert.False(monitor.WorkArea.IsEmpty);
            Assert.True(monitor.DpiX >= 96);
            Assert.True(monitor.DpiY >= 96);
            Assert.True(monitor.DpiScale >= 1.0);
        }
    }

    [Fact]
    public void GetMonitorAtPoint_AndCalculateClampedPosition_LiveSystem_StaysInsideWorkArea()
    {
        var primary = _service.GetMonitors().First(m => m.IsPrimary);
        var center = new ScreenPoint(
            primary.WorkArea.X + primary.WorkArea.Width / 2,
            primary.WorkArea.Y + primary.WorkArea.Height / 2);

        var found = _service.GetMonitorAtPoint(center);
        Assert.Equal(primary.Handle, found.Handle);

        var clamped = _service.CalculateClampedWindowPosition(
            new ScreenPoint(primary.WorkArea.Right - 10, primary.WorkArea.Bottom - 10),
            windowWidthPx: 440,
            windowHeightPx: 300,
            cursorOffsetPx: 12);

        Assert.True(clamped.X >= primary.WorkArea.X);
        Assert.True(clamped.Y >= primary.WorkArea.Y);
        Assert.True(clamped.X + 440 <= primary.WorkArea.Right || 440 > primary.WorkArea.Width);
        Assert.True(clamped.Y + 300 <= primary.WorkArea.Bottom || 300 > primary.WorkArea.Height);

        var centered = _service.CalculateCenteredWindowPosition(
            new ScreenPoint(primary.WorkArea.Right - 10, primary.WorkArea.Bottom - 10),
            windowWidthPx: 440,
            windowHeightPx: 300);

        var expectedCentered = WindowHelperService.CenterPositionInWorkArea(440, 300, primary.WorkArea);
        Assert.Equal(expectedCentered, centered);
    }

    [Fact]
    public void GetDpiScaleForWindow_ZeroHandle_ReturnsDefaultScale()
    {
        var scale = _service.GetDpiScaleForWindow(nint.Zero);
        Assert.True(scale >= 1.0);
    }

    [Fact]
    public void ConfigureNoActivateOverlayWindow_StripsOverlappedFrameAndAllowsCompactSquareSize()
    {
        const uint wsOverlappedWindow = 0x00CF0000;
        const uint wsPopup = 0x80000000;
        const uint wsCaption = 0x00C00000;
        const uint wsThickFrame = 0x00040000;
        const uint wsBorder = 0x00800000;
        const uint wsDlgFrame = 0x00400000;
        const uint wsExAppWindow = 0x00040000;
        const uint wsExToolWindow = 0x00000080;
        const uint wsExNoActivate = 0x08000000;
        const uint wsExTopMost = 0x00000008;
        const int gwlStyle = -16;
        const int gwlExStyle = -20;

        var hwnd = CreateWindowExW(
            wsExAppWindow,
            "STATIC",
            string.Empty,
            wsOverlappedWindow,
            100,
            100,
            300,
            300,
            nint.Zero,
            nint.Zero,
            nint.Zero,
            nint.Zero);
        Assert.NotEqual(nint.Zero, hwnd);

        try
        {
            _service.ConfigureNoActivateOverlayWindow(hwnd);
            _service.MoveAndResizeWindow(hwnd, 120, 120, 32, 32, activate: false);

            Assert.True(GetWindowRect(hwnd, out var rect));
            Assert.Equal(32, rect.Right - rect.Left);
            Assert.Equal(32, rect.Bottom - rect.Top);

            var style = unchecked((uint)GetWindowLongPtrW(hwnd, gwlStyle));
            Assert.True((style & wsPopup) != 0, "Overlay window must use WS_POPUP so Win32 does not clamp width to SM_CXMINTRACK.");
            Assert.Equal(0u, style & (wsCaption | wsThickFrame | wsBorder | wsDlgFrame));

            var exStyle = unchecked((uint)GetWindowLongPtrW(hwnd, gwlExStyle));
            Assert.Equal(0u, exStyle & wsExAppWindow);
            Assert.True((exStyle & wsExToolWindow) != 0);
            Assert.True((exStyle & wsExNoActivate) != 0);
            Assert.True((exStyle & wsExTopMost) != 0);
        }
        finally
        {
            DestroyWindow(hwnd);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetWindowLongPtrW(nint hwnd, int index);
}
