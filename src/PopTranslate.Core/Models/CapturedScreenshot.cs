namespace PopTranslate.Core.Models;

public sealed record CapturedScreenshot(
    byte[] PngBytes,
    ScreenRect CaptureBounds,
    double DpiScale,
    int PixelWidth,
    int PixelHeight,
    byte[]? RawBgraBytes = null,
    bool CopyOnly = false)
{
    public OcrImage ToOcrImage(LanguageCode? preferredLanguage = null) =>
        new(
            ImageBytes: PngBytes,
            PixelWidth: PixelWidth,
            PixelHeight: PixelHeight,
            DpiScale: DpiScale,
            MimeType: "image/png",
            PreferredLanguage: preferredLanguage,
            RawBgraBytes: RawBgraBytes);
}
