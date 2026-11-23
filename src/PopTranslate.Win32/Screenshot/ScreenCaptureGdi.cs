using System.Runtime.InteropServices;
using PopTranslate.Core.Models;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace PopTranslate.Win32.Screenshot;

public sealed class CapturedMonitorBitmap : IDisposable
{
    private bool _disposed;

    internal CapturedMonitorBitmap(
        MonitorInfo monitor,
        int width,
        int height,
        HDC brightDc,
        HBITMAP brightBitmap,
        HGDIOBJ brightOldObject,
        HDC dimmedDc,
        HBITMAP dimmedBitmap,
        HGDIOBJ dimmedOldObject,
        byte[] brightBgraPixels)
    {
        Monitor = monitor;
        Width = width;
        Height = height;
        BrightDc = brightDc;
        BrightBitmap = brightBitmap;
        BrightOldObject = brightOldObject;
        DimmedDc = dimmedDc;
        DimmedBitmap = dimmedBitmap;
        DimmedOldObject = dimmedOldObject;
        BrightBgraPixels = brightBgraPixels;
    }

    public MonitorInfo Monitor { get; }

    public int Width { get; }

    public int Height { get; }

    public byte[] BrightBgraPixels { get; }

    internal HDC BrightDc { get; private set; }

    internal HBITMAP BrightBitmap { get; private set; }

    internal HGDIOBJ BrightOldObject { get; private set; }

    internal HDC DimmedDc { get; private set; }

    internal HBITMAP DimmedBitmap { get; private set; }

    internal HGDIOBJ DimmedOldObject { get; private set; }

    public unsafe void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!DimmedDc.IsNull)
        {
            if (!DimmedOldObject.IsNull)
            {
                PInvoke.SelectObject(DimmedDc, DimmedOldObject);
                DimmedOldObject = HGDIOBJ.Null;
            }

            PInvoke.DeleteDC(DimmedDc);
            DimmedDc = HDC.Null;
        }

        if (!DimmedBitmap.IsNull)
        {
            PInvoke.DeleteObject(new HGDIOBJ(DimmedBitmap.Value));
            DimmedBitmap = HBITMAP.Null;
        }

        if (!BrightDc.IsNull)
        {
            if (!BrightOldObject.IsNull)
            {
                PInvoke.SelectObject(BrightDc, BrightOldObject);
                BrightOldObject = HGDIOBJ.Null;
            }

            PInvoke.DeleteDC(BrightDc);
            BrightDc = HDC.Null;
        }

        if (!BrightBitmap.IsNull)
        {
            PInvoke.DeleteObject(new HGDIOBJ(BrightBitmap.Value));
            BrightBitmap = HBITMAP.Null;
        }
    }
}

public static class ScreenCaptureGdi
{
    private const uint BiRgb = 0;

    public static IReadOnlyList<CapturedMonitorBitmap> CaptureAllMonitors(IReadOnlyList<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        var captured = new List<CapturedMonitorBitmap>(monitors.Count);
        try
        {
            foreach (var monitor in monitors)
            {
                if (monitor.Bounds.IsEmpty)
                {
                    continue;
                }

                captured.Add(CaptureMonitor(monitor));
            }

            return captured;
        }
        catch
        {
            foreach (var item in captured)
            {
                item.Dispose();
            }

            throw;
        }
    }

    public static unsafe CapturedMonitorBitmap CaptureMonitor(MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        var width = monitor.Bounds.Width;
        var height = monitor.Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(monitor), "Monitor bounds must be non-empty.");
        }

        var screenDc = PInvoke.GetDC(HWND.Null);
        if (screenDc.IsNull)
        {
            throw new InvalidOperationException("GetDC(NULL) failed while capturing screen.");
        }

        HDC brightDc = HDC.Null;
        HBITMAP brightBitmap = HBITMAP.Null;
        HGDIOBJ brightOldObj = HGDIOBJ.Null;
        HDC dimmedDc = HDC.Null;
        HBITMAP dimmedBitmap = HBITMAP.Null;
        HGDIOBJ dimmedOldObj = HGDIOBJ.Null;

        try
        {
            brightDc = PInvoke.CreateCompatibleDC(screenDc);
            if (brightDc.IsNull)
            {
                throw new InvalidOperationException("CreateCompatibleDC failed for bright capture buffer.");
            }

            var bmi = CreateTopDownBgra32BitmapInfo(width, height);
            void* brightBitsPtr = null;
            brightBitmap = PInvoke.CreateDIBSection(
                brightDc,
                &bmi,
                DIB_USAGE.DIB_RGB_COLORS,
                &brightBitsPtr,
                HANDLE.Null,
                0);

            if (brightBitmap.IsNull || brightBitsPtr is null)
            {
                throw new InvalidOperationException("CreateDIBSection failed for bright capture buffer.");
            }

            brightOldObj = PInvoke.SelectObject(brightDc, new HGDIOBJ(brightBitmap.Value));

            var copyFlags = ROP_CODE.SRCCOPY | ROP_CODE.CAPTUREBLT;
            if (!PInvoke.BitBlt(
                    brightDc,
                    0,
                    0,
                    width,
                    height,
                    screenDc,
                    monitor.Bounds.X,
                    monitor.Bounds.Y,
                    copyFlags))
            {
                // Fallback without CAPTUREBLT if desktop composition rejects it.
                PInvoke.BitBlt(
                    brightDc,
                    0,
                    0,
                    width,
                    height,
                    screenDc,
                    monitor.Bounds.X,
                    monitor.Bounds.Y,
                    ROP_CODE.SRCCOPY);
            }

            var byteCount = checked(width * height * 4);
            var brightPixels = new byte[byteCount];
            Marshal.Copy((nint)brightBitsPtr, brightPixels, 0, byteCount);

            // Create pre-dimmed DIB (~55% brightness) for tear-free 120 FPS overlay WM_PAINT.
            dimmedDc = PInvoke.CreateCompatibleDC(screenDc);
            if (dimmedDc.IsNull)
            {
                throw new InvalidOperationException("CreateCompatibleDC failed for dimmed overlay buffer.");
            }

            void* dimmedBitsPtr = null;
            dimmedBitmap = PInvoke.CreateDIBSection(
                dimmedDc,
                &bmi,
                DIB_USAGE.DIB_RGB_COLORS,
                &dimmedBitsPtr,
                HANDLE.Null,
                0);

            if (dimmedBitmap.IsNull || dimmedBitsPtr is null)
            {
                throw new InvalidOperationException("CreateDIBSection failed for dimmed overlay buffer.");
            }

            dimmedOldObj = PInvoke.SelectObject(dimmedDc, new HGDIOBJ(dimmedBitmap.Value));

            var dimmedSpan = new Span<byte>(dimmedBitsPtr, byteCount);
            for (var i = 0; i < byteCount; i += 4)
            {
                brightPixels[i + 3] = 0xFF;
                dimmedSpan[i] = (byte)(brightPixels[i] * 55 / 100);
                dimmedSpan[i + 1] = (byte)(brightPixels[i + 1] * 55 / 100);
                dimmedSpan[i + 2] = (byte)(brightPixels[i + 2] * 55 / 100);
                dimmedSpan[i + 3] = 0xFF;
            }

            var result = new CapturedMonitorBitmap(
                monitor,
                width,
                height,
                brightDc,
                brightBitmap,
                brightOldObj,
                dimmedDc,
                dimmedBitmap,
                dimmedOldObj,
                brightPixels);

            // Ownership transferred to CapturedMonitorBitmap.
            brightDc = HDC.Null;
            brightBitmap = HBITMAP.Null;
            brightOldObj = HGDIOBJ.Null;
            dimmedDc = HDC.Null;
            dimmedBitmap = HBITMAP.Null;
            dimmedOldObj = HGDIOBJ.Null;

            return result;
        }
        finally
        {
            if (!dimmedDc.IsNull)
            {
                if (!dimmedOldObj.IsNull) PInvoke.SelectObject(dimmedDc, dimmedOldObj);
                PInvoke.DeleteDC(dimmedDc);
            }

            if (!dimmedBitmap.IsNull)
            {
                PInvoke.DeleteObject(new HGDIOBJ(dimmedBitmap.Value));
            }

            if (!brightDc.IsNull)
            {
                if (!brightOldObj.IsNull) PInvoke.SelectObject(brightDc, brightOldObj);
                PInvoke.DeleteDC(brightDc);
            }

            if (!brightBitmap.IsNull)
            {
                PInvoke.DeleteObject(new HGDIOBJ(brightBitmap.Value));
            }

            PInvoke.ReleaseDC(HWND.Null, screenDc);
        }
    }

    public static ScreenRect NormalizeSelectionRect(
        ScreenPoint start,
        ScreenPoint end,
        int maxWidth,
        int maxHeight)
    {
        var clampedMaxW = Math.Max(0, maxWidth);
        var clampedMaxH = Math.Max(0, maxHeight);

        var left = Math.Clamp(Math.Min(start.X, end.X), 0, clampedMaxW);
        var top = Math.Clamp(Math.Min(start.Y, end.Y), 0, clampedMaxH);
        var right = Math.Clamp(Math.Max(start.X, end.X), 0, clampedMaxW);
        var bottom = Math.Clamp(Math.Max(start.Y, end.Y), 0, clampedMaxH);

        return new ScreenRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    public static byte[] CropBgra32(
        ReadOnlySpan<byte> sourcePixels,
        int sourceWidth,
        int sourceHeight,
        ScreenRect cropRect)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "Source dimensions must be positive.");
        }

        if (sourcePixels.Length < checked(sourceWidth * sourceHeight * 4))
        {
            throw new ArgumentException("Source pixel buffer is smaller than width * height * 4.", nameof(sourcePixels));
        }

        var left = Math.Clamp(cropRect.X, 0, sourceWidth);
        var top = Math.Clamp(cropRect.Y, 0, sourceHeight);
        var right = Math.Clamp(cropRect.Right, left, sourceWidth);
        var bottom = Math.Clamp(cropRect.Bottom, top, sourceHeight);

        var cropWidth = right - left;
        var cropHeight = bottom - top;
        if (cropWidth <= 0 || cropHeight <= 0)
        {
            return [];
        }

        var output = new byte[checked(cropWidth * cropHeight * 4)];
        var sourceStride = sourceWidth * 4;
        var destStride = cropWidth * 4;

        for (var row = 0; row < cropHeight; row++)
        {
            var srcOffset = (top + row) * sourceStride + left * 4;
            var dstOffset = row * destStride;
            sourcePixels.Slice(srcOffset, destStride).CopyTo(output.AsSpan(dstOffset, destStride));

            for (var col = 3; col < destStride; col += 4)
            {
                output[dstOffset + col] = 0xFF;
            }
        }

        return output;
    }

    public static async Task<byte[]> EncodeBgra32ToPngAsync(
        byte[] bgraPixels,
        int width,
        int height,
        double dpiX = 96.0,
        double dpiY = 96.0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bgraPixels);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        }

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            (uint)width,
            (uint)height,
            dpiX > 0 ? dpiX : 96.0,
            dpiY > 0 ? dpiY : 96.0,
            bgraPixels);

        await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);

        var size = (uint)stream.Size;
        var pngBytes = new byte[size];
        stream.Seek(0);

        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync(size).AsTask(cancellationToken).ConfigureAwait(false);
        reader.ReadBytes(pngBytes);
        return pngBytes;
    }

    public static async Task<CapturedScreenshot> CropAndEncodeAsync(
        CapturedMonitorBitmap monitorBitmap,
        ScreenRect localSelectionRect,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(monitorBitmap);

        var clamped = NormalizeSelectionRect(
            new ScreenPoint(localSelectionRect.X, localSelectionRect.Y),
            new ScreenPoint(localSelectionRect.Right, localSelectionRect.Bottom),
            monitorBitmap.Width,
            monitorBitmap.Height);

        if (clamped.IsEmpty)
        {
            throw new ArgumentException("Selection rectangle is empty.", nameof(localSelectionRect));
        }

        var croppedBgra = CropBgra32(
            monitorBitmap.BrightBgraPixels,
            monitorBitmap.Width,
            monitorBitmap.Height,
            clamped);

        var pngBytes = await EncodeBgra32ToPngAsync(
                croppedBgra,
                clamped.Width,
                clamped.Height,
                monitorBitmap.Monitor.DpiX,
                monitorBitmap.Monitor.DpiY,
                cancellationToken)
            .ConfigureAwait(false);

        var globalBounds = new ScreenRect(
            monitorBitmap.Monitor.Bounds.X + clamped.X,
            monitorBitmap.Monitor.Bounds.Y + clamped.Y,
            clamped.Width,
            clamped.Height);

        return new CapturedScreenshot(
            PngBytes: pngBytes,
            CaptureBounds: globalBounds,
            DpiScale: monitorBitmap.Monitor.DpiScale,
            PixelWidth: clamped.Width,
            PixelHeight: clamped.Height);
    }

    private static unsafe BITMAPINFO CreateTopDownBgra32BitmapInfo(int width, int height)
    {
        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        bmi.bmiHeader.biWidth = width;
        bmi.bmiHeader.biHeight = -height; // Negative height requests top-down DIB
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = BiRgb;
        return bmi;
    }
}
