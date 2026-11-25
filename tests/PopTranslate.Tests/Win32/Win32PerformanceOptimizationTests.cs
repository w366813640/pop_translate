using System.Diagnostics;
using System.Runtime.InteropServices;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using PopTranslate.Win32.Guards;
using PopTranslate.Win32.Screenshot;
using PopTranslate.Win32.Selection;
using PopTranslate.Win32.Windowing;
using Xunit;

namespace PopTranslate.Tests.Win32;

public sealed class Win32PerformanceOptimizationTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public async Task ScreenCaptureGdi_DirectNativeDibCrop_MatchesLazyBrightBgraPixelsAndDisposesCleanly()
    {
        var windowHelper = new WindowHelperService();
        var primary = windowHelper.GetMonitors().First(m => m.IsPrimary);

        var captureWidth = Math.Min(160, primary.Bounds.Width);
        var captureHeight = Math.Min(140, primary.Bounds.Height);
        var captureMonitor = primary with
        {
            Bounds = new ScreenRect(primary.Bounds.X, primary.Bounds.Y, captureWidth, captureHeight),
        };

        var captured = ScreenCaptureGdi.CaptureMonitor(captureMonitor);
        try
        {
            Assert.NotEqual(nint.Zero, captured.BrightBitsPtr);

            var cropRect = new ScreenRect(
                Math.Min(12, Math.Max(0, captureWidth - 1)),
                Math.Min(10, Math.Max(0, captureHeight - 1)),
                Math.Min(48, captureWidth),
                Math.Min(36, captureHeight));

            // Crop directly from native DIB pointer BEFORE touching BrightBgraPixels.
            var screenshot = await ScreenCaptureGdi.CropAndEncodeAsync(captured, cropRect);

            Assert.True(screenshot.PngBytes.Length > PngSignature.Length);
            Assert.Equal(PngSignature, screenshot.PngBytes[..PngSignature.Length]);
            Assert.NotNull(screenshot.RawBgraBytes);
            var rawBgra = screenshot.RawBgraBytes;
            Assert.Equal(screenshot.PixelWidth * screenshot.PixelHeight * 4, rawBgra.Length);
            for (var i = 3; i < rawBgra.Length; i += 4)
            {
                Assert.Equal(0xFF, rawBgra[i]);
            }

            // Lazy BrightBgraPixels fallback remains intact and matches native DIB crop identically.
            var lazyPixels = captured.BrightBgraPixels;
            Assert.Equal(captureWidth * captureHeight * 4, lazyPixels.Length);
            Assert.Same(lazyPixels, captured.BrightBgraPixels);

            var clampedCrop = ScreenCaptureGdi.NormalizeSelectionRect(
                new ScreenPoint(cropRect.X, cropRect.Y),
                new ScreenPoint(cropRect.Right, cropRect.Bottom),
                captured.Width,
                captured.Height);
            var expectedBgra = ScreenCaptureGdi.CropBgra32(lazyPixels, captured.Width, captured.Height, clampedCrop);
            Assert.Equal(expectedBgra, screenshot.RawBgraBytes);
        }
        finally
        {
            captured.Dispose();
        }

        // Dispose clears native pointer, rejects post-dispose access, and is idempotent.
        Assert.Equal(nint.Zero, captured.BrightBitsPtr);
        Assert.Throws<ObjectDisposedException>(() => _ = captured.BrightBgraPixels);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => ScreenCaptureGdi.CropAndEncodeAsync(captured, new ScreenRect(0, 0, 8, 8)));

        captured.Dispose();
    }

    [Fact]
    public void ScreenCaptureGdi_CaptureAllMonitors_MultipleMonitorsPreservesOrderAndSkipsEmpty()
    {
        var windowHelper = new WindowHelperService();
        var primary = windowHelper.GetMonitors().First(m => m.IsPrimary);

        var w1 = Math.Min(64, primary.Bounds.Width);
        var h1 = Math.Min(48, primary.Bounds.Height);
        var w2 = Math.Min(80, primary.Bounds.Width);
        var h2 = Math.Min(64, primary.Bounds.Height);

        var monitors = new MonitorInfo[]
        {
            primary with { DeviceName = @"\\.\DISPLAY_FIRST", Bounds = new ScreenRect(primary.Bounds.X, primary.Bounds.Y, w1, h1) },
            primary with { DeviceName = @"\\.\DISPLAY_EMPTY", Bounds = new ScreenRect(0, 0, 0, 0) },
            primary with { DeviceName = @"\\.\DISPLAY_SECOND", Bounds = new ScreenRect(primary.Bounds.X, primary.Bounds.Y, w2, h2) },
        };

        var captured = ScreenCaptureGdi.CaptureAllMonitors(monitors);
        try
        {
            Assert.Equal(2, captured.Count);
            Assert.Equal(@"\\.\DISPLAY_FIRST", captured[0].Monitor.DeviceName);
            Assert.Equal(w1, captured[0].Width);
            Assert.Equal(h1, captured[0].Height);
            Assert.NotEqual(nint.Zero, captured[0].BrightBitsPtr);

            Assert.Equal(@"\\.\DISPLAY_SECOND", captured[1].Monitor.DeviceName);
            Assert.Equal(w2, captured[1].Width);
            Assert.Equal(h2, captured[1].Height);
            Assert.NotEqual(nint.Zero, captured[1].BrightBitsPtr);
        }
        finally
        {
            foreach (var bitmap in captured)
            {
                bitmap.Dispose();
            }
        }

        Assert.All(captured, bitmap => Assert.Equal(nint.Zero, bitmap.BrightBitsPtr));
    }

    [Fact]
    public void MouseSelectionHoverMonitor_SetHoverIconVisible_TogglesStateSafely()
    {
        var windowHelper = new WindowHelperService();
        var guard = new ForegroundAppGuard();
        var selectionService = new TextSelectionService(
            new UiAutomationSelectionReader(),
            new ClipboardFallbackSelectionReader(new ClipboardBackupService()),
            windowHelper,
            guard);

        using var monitor = new MouseSelectionHoverMonitor(selectionService, guard, windowHelper);
        ISelectionHoverMonitor monitorInterface = monitor;

        Assert.False(monitor.IsHoverIconVisible);

        monitorInterface.SetHoverIconVisible(true);
        Assert.True(monitor.IsHoverIconVisible);

        monitor.SetHoverIconVisible(false);
        Assert.False(monitor.IsHoverIconVisible);

        // Verify default interface implementation works without requiring override.
        ISelectionHoverMonitor stubMonitor = new MinimalSelectionHoverMonitorStub();
        stubMonitor.SetHoverIconVisible(true);
        stubMonitor.SetHoverIconVisible(false);
    }

    [Fact]
    public void WindowHelperService_TrimProcessWorkingSet_RunsWithoutThrowing()
    {
        IWindowHelperService service = new WindowHelperService();
        service.TrimProcessWorkingSet();
        service.TrimProcessWorkingSet();

        // Verify default interface implementation works without requiring override.
        IWindowHelperService stubService = new MinimalWindowHelperStub();
        stubService.TrimProcessWorkingSet();
    }

    [Fact]
    public void ForegroundAppGuard_GetProcessNameForWindow_ReturnsAccurateProcessNameAcrossCachedRepeatCalls()
    {
        var hwnd = CreateWindowExW(
            0,
            "STATIC",
            string.Empty,
            0,
            0,
            0,
            16,
            16,
            nint.Zero,
            nint.Zero,
            nint.Zero,
            nint.Zero);
        Assert.NotEqual(nint.Zero, hwnd);

        try
        {
            var expectedProcessName = !string.IsNullOrWhiteSpace(Environment.ProcessPath)
                ? Path.GetFileName(Environment.ProcessPath)
                : $"{Process.GetCurrentProcess().ProcessName}.exe";

            var guard = new ForegroundAppGuard();
            var first = guard.GetProcessNameForWindow(hwnd);
            var second = guard.GetProcessNameForWindow(hwnd);
            var third = guard.GetProcessNameForWindow(hwnd);

            Assert.False(string.IsNullOrWhiteSpace(first));
            Assert.Equal(expectedProcessName, first, ignoreCase: true);
            Assert.Equal(first, second);
            Assert.Equal(first, third);

            Parallel.For(0, 16, _ =>
            {
                var concurrentResult = guard.GetProcessNameForWindow(hwnd);
                Assert.Equal(first, concurrentResult);
            });
        }
        finally
        {
            DestroyWindow(hwnd);
        }
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

    private sealed class MinimalSelectionHoverMonitorStub : ISelectionHoverMonitor
    {
        public bool IsRunning => false;
        public event EventHandler<HoverSelectionEventArgs>? SelectionDetected { add { } remove { } }
        public event EventHandler? DismissRequested { add { } remove { } }
        public event EventHandler<nint>? DoubleTapCopyDetected { add { } remove { } }
        public void Start(SelectionConfig config) { }
        public void UpdateConfig(SelectionConfig config) { }
        public void SetDoubleTapCopyEnabled(bool enabled) { }
        public void Stop() { }
        public void RegisterIgnoredWindow(nint windowHandle) { }
        public void UnregisterIgnoredWindow(nint windowHandle) { }
        public void Dispose() { }
    }

    private sealed class MinimalWindowHelperStub : IWindowHelperService
    {
        private static readonly MonitorInfo StubMonitor = new(
            Handle: 1,
            DeviceName: @"\\.\DISPLAY_STUB",
            Bounds: new ScreenRect(0, 0, 100, 100),
            WorkArea: new ScreenRect(0, 0, 100, 100),
            DpiX: 96,
            DpiY: 96,
            IsPrimary: true);

        public IReadOnlyList<MonitorInfo> GetMonitors() => [StubMonitor];
        public MonitorInfo GetMonitorAtPoint(ScreenPoint point) => StubMonitor;
        public ScreenPoint GetCursorPosition() => new(0, 0);
        public nint GetForegroundWindowHandle() => nint.Zero;
        public ScreenPoint CalculateClampedWindowPosition(ScreenPoint anchorPoint, int windowWidthPx, int windowHeightPx, int cursorOffsetPx = 12) => anchorPoint;
        public ScreenPoint CalculateCenteredWindowPosition(ScreenPoint referencePoint, int windowWidthPx, int windowHeightPx) => referencePoint;
        public double GetDpiScaleForWindow(nint windowHandle) => 1.0;
        public void ConfigureNoActivateOverlayWindow(nint windowHandle) { }
        public void ConfigurePopupToolWindow(nint windowHandle, bool hideFromTaskbar = true) { }
        public void SetWindowTopMost(nint windowHandle, bool topMost) { }
        public void MoveAndResizeWindow(nint windowHandle, int x, int y, int width, int height, bool activate = false) { }
        public void ShowWindowNoActivate(nint windowHandle) { }
        public void ForceForegroundWindow(nint windowHandle) { }
    }
}
