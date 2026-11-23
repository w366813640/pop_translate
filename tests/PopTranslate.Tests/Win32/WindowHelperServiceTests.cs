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
    }

    [Fact]
    public void GetDpiScaleForWindow_ZeroHandle_ReturnsDefaultScale()
    {
        var scale = _service.GetDpiScaleForWindow(nint.Zero);
        Assert.True(scale >= 1.0);
    }
}
