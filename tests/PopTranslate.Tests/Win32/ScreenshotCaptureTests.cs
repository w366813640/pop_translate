using PopTranslate.Core.Models;
using PopTranslate.Win32.Screenshot;
using PopTranslate.Win32.Windowing;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace PopTranslate.Tests.Win32;

public sealed class ScreenshotCaptureTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void NormalizeSelectionRect_HandlesReverseDragAndClampsBounds()
    {
        var rect = ScreenCaptureGdi.NormalizeSelectionRect(
            start: new ScreenPoint(250, 180),
            end: new ScreenPoint(-20, 40),
            maxWidth: 200,
            maxHeight: 150);

        Assert.Equal(new ScreenRect(0, 40, 200, 110), rect);
    }

    [Fact]
    public void CropBgra32_ExtractsExactSubRegionAndSetsOpaqueAlpha()
    {
        // Build a 4x4 synthetic BGRA32 image where each pixel encodes (B=x, G=y, R=x+y, A=0).
        const int width = 4;
        const int height = 4;
        var source = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var idx = (y * width + x) * 4;
                source[idx] = (byte)x;
                source[idx + 1] = (byte)y;
                source[idx + 2] = (byte)(x + y);
                source[idx + 3] = 0x00;
            }
        }

        var cropped = ScreenCaptureGdi.CropBgra32(source, width, height, new ScreenRect(1, 2, 2, 2));

        Assert.Equal(2 * 2 * 4, cropped.Length);
        // Top-left of crop corresponds to (x=1, y=2)
        Assert.Equal(1, cropped[0]);
        Assert.Equal(2, cropped[1]);
        Assert.Equal(3, cropped[2]);
        Assert.Equal(0xFF, cropped[3]);
        // Bottom-right of crop corresponds to (x=2, y=3)
        var last = (1 * 2 + 1) * 4;
        Assert.Equal(2, cropped[last]);
        Assert.Equal(3, cropped[last + 1]);
        Assert.Equal(5, cropped[last + 2]);
        Assert.Equal(0xFF, cropped[last + 3]);
    }

    [Fact]
    public async Task EncodeBgra32ToPngAsync_ProducesValidPngMatchingDimensions()
    {
        const int width = 32;
        const int height = 16;
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 212;     // B
            pixels[i + 1] = 120; // G
            pixels[i + 2] = 0;   // R
            pixels[i + 3] = 255; // A
        }

        var pngBytes = await ScreenCaptureGdi.EncodeBgra32ToPngAsync(pixels, width, height, dpiX: 144, dpiY: 144);

        Assert.True(pngBytes.Length > PngSignature.Length);
        Assert.Equal(PngSignature, pngBytes[..PngSignature.Length]);

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(pngBytes);
            await writer.StoreAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        Assert.Equal((uint)width, decoder.PixelWidth);
        Assert.Equal((uint)height, decoder.PixelHeight);
    }

    [Fact]
    public async Task CaptureMonitor_LivePrimaryMonitor_CapturesAndCropsToValidPng()
    {
        var windowHelper = new WindowHelperService();
        var primary = windowHelper.GetMonitors().First(m => m.IsPrimary);

        using var captured = ScreenCaptureGdi.CaptureMonitor(primary);
        Assert.Equal(primary.Bounds.Width, captured.Width);
        Assert.Equal(primary.Bounds.Height, captured.Height);
        Assert.Equal(primary.Bounds.Width * primary.Bounds.Height * 4, captured.BrightBgraPixels.Length);

        var cropRect = new ScreenRect(0, 0, Math.Min(32, captured.Width), Math.Min(24, captured.Height));
        var screenshot = await ScreenCaptureGdi.CropAndEncodeAsync(captured, cropRect);

        Assert.Equal(cropRect.Width, screenshot.PixelWidth);
        Assert.Equal(cropRect.Height, screenshot.PixelHeight);
        Assert.Equal(primary.Bounds.X, screenshot.CaptureBounds.X);
        Assert.Equal(primary.Bounds.Y, screenshot.CaptureBounds.Y);
        Assert.Equal(PngSignature, screenshot.PngBytes[..PngSignature.Length]);
    }

    [Fact]
    public async Task ScreenshotCaptureService_PreCancelledToken_ReturnsNullWithoutRemainingActive()
    {
        var service = new ScreenshotCaptureService(new WindowHelperService());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await service.CaptureRegionAsync(cts.Token);

        Assert.Null(result);
        Assert.False(service.IsCaptureActive);
    }

    [Fact]
    public async Task ScreenshotCaptureService_InitialOverlayShow_RoutesInitialWmPaintToOverlayWindow()
    {
        var fakeWindowHelper = new TinyMonitorWindowHelper();
        var service = new ScreenshotCaptureService(fakeWindowHelper);

        var captureTask = service.CaptureRegionAsync();
        try
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(600);
            while (service.LastSessionPaintCount == 0 && DateTime.UtcNow < deadline && !captureTask.IsCompleted)
            {
                await Task.Delay(15);
            }
        }
        finally
        {
            service.CancelActiveCapture();
        }

        var result = await captureTask;

        Assert.Null(result);
        Assert.False(service.IsCaptureActive);
        Assert.True(service.LastSessionPaintCount >= 1, "Initial WM_PAINT was dropped instead of being handled by FrozenMonitorOverlayWindow.");
    }

    private sealed class TinyMonitorWindowHelper : PopTranslate.Core.Contracts.IWindowHelperService
    {
        private static readonly MonitorInfo TinyMonitor = new(
            Handle: 1,
            DeviceName: @"\\.\DISPLAY_TEST",
            Bounds: new ScreenRect(0, 0, 16, 16),
            WorkArea: new ScreenRect(0, 0, 16, 16),
            DpiX: 96,
            DpiY: 96,
            IsPrimary: true);

        public IReadOnlyList<MonitorInfo> GetMonitors() => [TinyMonitor];
        public MonitorInfo GetMonitorAtPoint(ScreenPoint point) => TinyMonitor;
        public ScreenPoint GetCursorPosition() => new(4, 4);
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
