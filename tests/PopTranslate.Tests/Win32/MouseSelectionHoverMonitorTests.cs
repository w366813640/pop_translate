using PopTranslate.Core.Models;
using PopTranslate.Win32.Guards;
using PopTranslate.Win32.Selection;
using PopTranslate.Win32.Windowing;
using Xunit;

namespace PopTranslate.Tests.Win32;

public sealed class MouseSelectionHoverMonitorTests
{
    [Fact]
    public void IsDragSelectionGesture_ExceedsDistanceAndDuration_ReturnsTrue()
    {
        var result = MouseSelectionHoverMonitor.IsDragSelectionGesture(
            downPoint: new ScreenPoint(100, 100),
            downTimestampMs: 1000,
            upPoint: new ScreenPoint(140, 104),
            upTimestampMs: 1180,
            minDragDistancePixels: 12);

        Assert.True(result);
    }

    [Fact]
    public void IsDragSelectionGesture_ShortDistanceOrTooFast_ReturnsFalse()
    {
        Assert.False(MouseSelectionHoverMonitor.IsDragSelectionGesture(
            new ScreenPoint(100, 100), 1000,
            new ScreenPoint(105, 102), 1200,
            minDragDistancePixels: 12));

        Assert.False(MouseSelectionHoverMonitor.IsDragSelectionGesture(
            new ScreenPoint(100, 100), 1000,
            new ScreenPoint(150, 100), 1015,
            minDragDistancePixels: 12));

        Assert.False(MouseSelectionHoverMonitor.IsDragSelectionGesture(
            new ScreenPoint(100, 100), 1000,
            new ScreenPoint(150, 100), 15000,
            minDragDistancePixels: 12));
    }

    [Fact]
    public void IsDoubleClickSelectionGesture_WithinTimeAndRadius_ReturnsTrue()
    {
        var result = MouseSelectionHoverMonitor.IsDoubleClickSelectionGesture(
            firstUpPoint: new ScreenPoint(500, 300),
            firstUpTimestampMs: 2000,
            secondUpPoint: new ScreenPoint(503, 302),
            secondUpTimestampMs: 2250,
            maxDoubleClickTimeMs: 500);

        Assert.True(result);
    }

    [Fact]
    public void IsDoubleClickSelectionGesture_TooSlowOrTooFar_ReturnsFalse()
    {
        Assert.False(MouseSelectionHoverMonitor.IsDoubleClickSelectionGesture(
            new ScreenPoint(500, 300), 2000,
            new ScreenPoint(501, 300), 2700,
            maxDoubleClickTimeMs: 500));

        Assert.False(MouseSelectionHoverMonitor.IsDoubleClickSelectionGesture(
            new ScreenPoint(500, 300), 2000,
            new ScreenPoint(520, 300), 2150,
            maxDoubleClickTimeMs: 500));
    }

    [Theory]
    [InlineData("Chrome_WidgetWin_1", true)]
    [InlineData("MozillaWindowClass", true)]
    [InlineData("AcrobatSDIWindow", true)]
    [InlineData("SUMATRA_PDF_FRAME", true)]
    [InlineData("Shell_TrayWnd", false)]
    [InlineData(null, false)]
    public void IsKnownSelectableCanvasClass_ClassifiesDocumentSurfaces(string? className, bool expected)
    {
        Assert.Equal(expected, MouseSelectionHoverMonitor.IsKnownSelectableCanvasClass(className));
    }

    [Fact]
    public void IgnoredWindowRegistration_TracksAndRemovesHandlesSafely()
    {
        using var monitor = CreateMonitor();

        monitor.RegisterIgnoredWindow(nint.Zero);
        Assert.False(monitor.IsWindowIgnored(nint.Zero));

        nint fakeHwnd = 0x12345;
        monitor.RegisterIgnoredWindow(fakeHwnd);
        Assert.True(monitor.IsWindowIgnored(fakeHwnd));

        monitor.UnregisterIgnoredWindow(fakeHwnd);
        Assert.False(monitor.IsWindowIgnored(fakeHwnd));
    }

    [Fact]
    public void StartUpdateStopDispose_LifecycleRunsCleanly()
    {
        using var monitor = CreateMonitor();
        Assert.False(monitor.IsRunning);

        var config = new SelectionConfig { EnableHoverIcon = true };
        monitor.Start(config);
        Assert.True(monitor.IsRunning);

        monitor.UpdateConfig(new SelectionConfig { EnableHoverIcon = false, MinDragDistancePixels = 20 });
        Assert.True(monitor.IsRunning);

        monitor.Stop();
        Assert.False(monitor.IsRunning);

        // Idempotent second stop and dispose.
        monitor.Stop();
        monitor.Dispose();
    }

    private static MouseSelectionHoverMonitor CreateMonitor()
    {
        var windowHelper = new WindowHelperService();
        var guard = new ForegroundAppGuard();
        var selectionService = new TextSelectionService(
            new UiAutomationSelectionReader(),
            new ClipboardFallbackSelectionReader(new ClipboardBackupService()),
            windowHelper,
            guard);
        return new MouseSelectionHoverMonitor(selectionService, guard, windowHelper);
    }
}
