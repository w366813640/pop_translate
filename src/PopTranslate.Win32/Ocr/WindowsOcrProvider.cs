using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using CoreOcrResult = PopTranslate.Core.Models.OcrResult;

namespace PopTranslate.Win32.Ocr;

public sealed class WindowsOcrProvider : IOcrProvider
{
    public OcrProviderKind Kind => OcrProviderKind.WindowsNative;

    public string DisplayName => "Windows 11 Native OCR";

    public Task<IReadOnlyList<OcrLanguagePackInfo>> GetAvailableLanguagesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<OcrLanguagePackInfo> result = OcrEngine.AvailableRecognizerLanguages
            .Select(lang => new OcrLanguagePackInfo(
                LanguageTag: lang.LanguageTag,
                DisplayName: lang.DisplayName,
                MappedLanguage: LanguageCode.FromBcp47(lang.LanguageTag)))
            .ToList();

        return Task.FromResult(result);
    }

    public async Task<CoreOcrResult> RecognizeAsync(
        OcrImage image,
        OcrConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(config);

        var sw = Stopwatch.StartNew();

        var hasRawBgra = image.RawBgraBytes is { Length: > 0 } && image.PixelWidth > 0 && image.PixelHeight > 0;
        var hasEncodedBytes = image.ImageBytes is { Length: > 0 };

        if (!hasRawBgra && !hasEncodedBytes)
        {
            return new CoreOcrResult(
                IsSuccess: false,
                Text: string.Empty,
                DetectedLanguage: null,
                ProviderKind: Kind,
                ErrorMessage: "OCR image payload is empty.",
                Elapsed: sw.Elapsed);
        }

        try
        {
            var engine = ResolveOcrEngine(image.PreferredLanguage, config.PreferredWindowsOcrLanguageTag);
            if (engine is null)
            {
                return new CoreOcrResult(
                    IsSuccess: false,
                    Text: string.Empty,
                    DetectedLanguage: null,
                    ProviderKind: Kind,
                    ErrorMessage: "No Windows OCR language packs are installed. Install an OCR language pack in Windows Settings > Time & language > Language & region.",
                    Elapsed: sw.Elapsed);
            }

            using var softwareBitmap = await DecodeToSoftwareBitmapAsync(image, cancellationToken)
                .ConfigureAwait(false);

            var ocrResult = await engine.RecognizeAsync(softwareBitmap)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            var lines = new List<string>(ocrResult.Lines.Count);
            foreach (var line in ocrResult.Lines)
            {
                var cleaned = CleanOcrLineText(line.Text);
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    lines.Add(cleaned);
                }
            }

            var text = string.Join('\n', lines);
            var detectedLanguage = LanguageCode.FromBcp47(engine.RecognizerLanguage.LanguageTag);

            return new CoreOcrResult(
                IsSuccess: true,
                Text: text,
                DetectedLanguage: detectedLanguage,
                ProviderKind: Kind,
                ErrorMessage: null,
                Elapsed: sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CoreOcrResult(
                IsSuccess: false,
                Text: string.Empty,
                DetectedLanguage: null,
                ProviderKind: Kind,
                ErrorMessage: $"Windows Native OCR failed: {ex.Message}",
                Elapsed: sw.Elapsed);
        }
    }

    public async Task<ProviderHealthCheckResult> TestAsync(
        OcrConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var sw = Stopwatch.StartNew();
        try
        {
            var languages = await GetAvailableLanguagesAsync(cancellationToken).ConfigureAwait(false);
            if (languages.Count == 0)
            {
                return new ProviderHealthCheckResult(
                    IsSuccess: false,
                    Message: "No Windows OCR language packs are installed on this system.",
                    Latency: sw.Elapsed);
            }

            var engine = ResolveOcrEngine(null, config.PreferredWindowsOcrLanguageTag);
            if (engine is null)
            {
                return new ProviderHealthCheckResult(
                    IsSuccess: false,
                    Message: "Unable to initialize Windows OCR engine with installed language packs.",
                    Latency: sw.Elapsed);
            }

            var tags = string.Join(", ", languages.Select(l => l.LanguageTag));
            return new ProviderHealthCheckResult(
                IsSuccess: true,
                Message: $"Ready using '{engine.RecognizerLanguage.LanguageTag}' (installed packs: {tags}).",
                Latency: sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProviderHealthCheckResult(
                IsSuccess: false,
                Message: $"Windows Native OCR check failed: {ex.Message}",
                Latency: sw.Elapsed);
        }
    }

    public static string CleanOcrLineText(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        var trimmed = line.Trim();
        var sb = new StringBuilder(trimmed.Length);

        for (var i = 0; i < trimmed.Length; i++)
        {
            var current = trimmed[i];
            if (current == ' ')
            {
                var runEnd = i + 1;
                while (runEnd < trimmed.Length && trimmed[runEnd] == ' ')
                {
                    runEnd++;
                }

                if (sb.Length > 0 && runEnd < trimmed.Length)
                {
                    var prev = sb[^1];
                    var next = trimmed[runEnd];
                    if (IsCjkNoSpaceChar(prev) && IsCjkNoSpaceChar(next))
                    {
                        i = runEnd - 1;
                        continue;
                    }
                }
            }

            sb.Append(current);
        }

        return sb.ToString();
    }

    private static OcrEngine? ResolveOcrEngine(LanguageCode? preferredLanguage, string? preferredLanguageTag)
    {
        var available = OcrEngine.AvailableRecognizerLanguages;
        if (available.Count == 0)
        {
            return null;
        }

        // 1. Explicit per-image LanguageCode preference
        if (preferredLanguage is { IsAuto: false } imageLang)
        {
            var matched = available.FirstOrDefault(l =>
                LanguageCode.FromBcp47(l.LanguageTag) == imageLang);
            if (matched is not null)
            {
                var engine = OcrEngine.TryCreateFromLanguage(matched);
                if (engine is not null)
                {
                    return engine;
                }
            }
        }

        // 2. Configured Windows BCP-47 language tag preference (e.g. "zh-Hans-CN", "en-US")
        if (!string.IsNullOrWhiteSpace(preferredLanguageTag))
        {
            var trimmedTag = preferredLanguageTag.Trim();
            var exactMatch = available.FirstOrDefault(l =>
                string.Equals(l.LanguageTag, trimmedTag, StringComparison.OrdinalIgnoreCase));

            if (exactMatch is not null)
            {
                var engine = OcrEngine.TryCreateFromLanguage(exactMatch);
                if (engine is not null)
                {
                    return engine;
                }
            }

            var mappedCode = LanguageCode.FromBcp47(trimmedTag);
            if (!mappedCode.IsAuto)
            {
                var familyMatch = available.FirstOrDefault(l =>
                    LanguageCode.FromBcp47(l.LanguageTag) == mappedCode);
                if (familyMatch is not null)
                {
                    var engine = OcrEngine.TryCreateFromLanguage(familyMatch);
                    if (engine is not null)
                    {
                        return engine;
                    }
                }
            }

            try
            {
                var candidate = new Language(trimmedTag);
                if (OcrEngine.IsLanguageSupported(candidate))
                {
                    var engine = OcrEngine.TryCreateFromLanguage(candidate);
                    if (engine is not null)
                    {
                        return engine;
                    }
                }
            }
            catch
            {
                // Invalid or uninstalled BCP-47 tag — gracefully fall back below.
            }
        }

        // 3. Auto mode: check user profile language first; if non-CJK (e.g. en-US Windows UI),
        // prefer an installed zh-Hans / zh-Hant / ja recognizer since Windows CJK OCR models
        // natively recognize both CJK and Latin scripts accurately!
        var profileEngine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (profileEngine is not null && IsCjkRecognizerLanguage(profileEngine.RecognizerLanguage.LanguageTag))
        {
            return profileEngine;
        }

        LanguageCode[] smartCjkFallbacks =
        [
            LanguageCode.ChineseSimplified,
            LanguageCode.ChineseTraditional,
            LanguageCode.Japanese,
        ];

        foreach (var targetCjk in smartCjkFallbacks)
        {
            Language? cjkLang = null;
            foreach (var candidate in available)
            {
                if (LanguageCode.FromBcp47(candidate.LanguageTag) == targetCjk)
                {
                    cjkLang = candidate;
                    break;
                }
            }

            if (cjkLang is not null)
            {
                var cjkEngine = OcrEngine.TryCreateFromLanguage(cjkLang);
                if (cjkEngine is not null)
                {
                    return cjkEngine;
                }
            }
        }

        if (profileEngine is not null)
        {
            return profileEngine;
        }

        // 4. First available installed OCR language pack
        foreach (var lang in available)
        {
            var engine = OcrEngine.TryCreateFromLanguage(lang);
            if (engine is not null)
            {
                return engine;
            }
        }

        return null;
    }

    private static async Task<SoftwareBitmap> DecodeToSoftwareBitmapAsync(
        OcrImage image,
        CancellationToken cancellationToken)
    {
        var maxDim = (int)OcrEngine.MaxImageDimension;

        // Fast direct BGRA32 path when CapturedScreenshot supplies RawBgraBytes (zero PNG decode overhead).
        if (image.RawBgraBytes is { Length: > 0 } rawBgra &&
            image.PixelWidth > 0 &&
            image.PixelHeight > 0 &&
            image.PixelWidth <= maxDim &&
            image.PixelHeight <= maxDim)
        {
            var expectedRawBytes = checked(image.PixelWidth * image.PixelHeight * 4);
            if (rawBgra.Length >= expectedRawBytes)
            {
                return SoftwareBitmap.CreateCopyFromBuffer(
                    rawBgra.AsBuffer(0, expectedRawBytes),
                    BitmapPixelFormat.Bgra8,
                    image.PixelWidth,
                    image.PixelHeight,
                    BitmapAlphaMode.Premultiplied);
            }
        }

        if (IsRawBgra32MimeType(image.MimeType))
        {
            if (image.PixelWidth <= 0 || image.PixelHeight <= 0)
            {
                throw new ArgumentException("Raw BGRA32 OcrImage must specify positive PixelWidth and PixelHeight.");
            }

            var expectedBytes = checked(image.PixelWidth * image.PixelHeight * 4);
            if (image.ImageBytes.Length < expectedBytes)
            {
                throw new ArgumentException("Raw BGRA32 buffer is smaller than PixelWidth * PixelHeight * 4.");
            }

            return SoftwareBitmap.CreateCopyFromBuffer(
                image.ImageBytes.AsBuffer(0, expectedBytes),
                BitmapPixelFormat.Bgra8,
                image.PixelWidth,
                image.PixelHeight,
                BitmapAlphaMode.Premultiplied);
        }

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(image.ImageBytes);
            await writer.StoreAsync().AsTask(cancellationToken).ConfigureAwait(false);
            writer.DetachStream();
        }

        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);

        var maxDimUint = OcrEngine.MaxImageDimension;
        var srcWidth = decoder.PixelWidth;
        var srcHeight = decoder.PixelHeight;

        if (srcWidth <= maxDimUint && srcHeight <= maxDimUint)
        {
            return await decoder
                .GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
        }

        var scale = Math.Min((double)maxDimUint / srcWidth, (double)maxDimUint / srcHeight);
        var transform = new BitmapTransform
        {
            ScaledWidth = Math.Max(1u, (uint)Math.Floor(srcWidth * scale)),
            ScaledHeight = Math.Max(1u, (uint)Math.Floor(srcHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };

        var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        var scaledBytes = pixelData.DetachPixelData();
        return SoftwareBitmap.CreateCopyFromBuffer(
            scaledBytes.AsBuffer(),
            BitmapPixelFormat.Bgra8,
            (int)transform.ScaledWidth,
            (int)transform.ScaledHeight,
            BitmapAlphaMode.Premultiplied);
    }

    private static bool IsCjkRecognizerLanguage(string? languageTag)
    {
        var mapped = LanguageCode.FromBcp47(languageTag);
        return mapped.IsChinese || mapped == LanguageCode.Japanese || mapped == LanguageCode.Korean;
    }

    private static bool IsRawBgra32MimeType(string? mimeType) =>
        string.Equals(mimeType, "raw/bgra32", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mimeType, "image/x-bgra32", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mimeType, "bgra32", StringComparison.OrdinalIgnoreCase);

    private static bool IsCjkNoSpaceChar(char c) =>
        c == '\u00B7' ||                      // Middle dot (·)
        (c >= '\u2000' && c <= '\u206F') ||   // General Punctuation (“ ” ‘ ’ — …)
        (c >= '\u3000' && c <= '\u303F') ||   // CJK Symbols and Punctuation
        (c >= '\u3040' && c <= '\u309F') ||   // Hiragana
        (c >= '\u30A0' && c <= '\u30FF') ||   // Katakana
        (c >= '\u3400' && c <= '\u4DBF') ||   // CJK Unified Ideographs Extension A
        (c >= '\u4E00' && c <= '\u9FFF') ||   // CJK Unified Ideographs
        (c >= '\uF900' && c <= '\uFAFF') ||   // CJK Compatibility Ideographs
        (c >= '\uFF00' && c <= '\uFFEF');     // Fullwidth Forms
}
