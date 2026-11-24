namespace PopTranslate.Core.Models;

public sealed record OcrImage(
    byte[] ImageBytes,
    int PixelWidth,
    int PixelHeight,
    double DpiScale = 1.0,
    string MimeType = "image/png",
    LanguageCode? PreferredLanguage = null,
    byte[]? RawBgraBytes = null);
