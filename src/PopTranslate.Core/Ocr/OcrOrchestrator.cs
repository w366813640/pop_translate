using System.Diagnostics;
using System.Text;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;

namespace PopTranslate.Core.Ocr;

public sealed class OcrOrchestrator : IOcrOrchestrator
{
    private readonly ISettingsStore _settingsStore;
    private readonly ITextPreprocessor _textPreprocessor;
    private readonly IReadOnlyDictionary<OcrProviderKind, IOcrProvider> _providers;

    public OcrOrchestrator(
        ISettingsStore settingsStore,
        ITextPreprocessor textPreprocessor,
        IEnumerable<IOcrProvider> providers)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _textPreprocessor = textPreprocessor ?? throw new ArgumentNullException(nameof(textPreprocessor));
        ArgumentNullException.ThrowIfNull(providers);

        var map = new Dictionary<OcrProviderKind, IOcrProvider>();
        foreach (var provider in providers)
        {
            if (provider is not null)
            {
                map[provider.Kind] = provider;
            }
        }

        _providers = map;
    }

    public async Task<OcrResult> RecognizeAsync(
        OcrImage image,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        var ocrConfig = _settingsStore.Current.Ocr ?? new OcrConfig();
        if (!_providers.TryGetValue(ocrConfig.ActiveProvider, out var activeProvider))
        {
            return new OcrResult(
                IsSuccess: false,
                Text: string.Empty,
                DetectedLanguage: null,
                ProviderKind: ocrConfig.ActiveProvider,
                ErrorMessage: $"OCR provider '{ocrConfig.ActiveProvider}' is not registered.",
                Elapsed: TimeSpan.Zero);
        }

        var sw = Stopwatch.StartNew();
        OcrResult result;
        try
        {
            result = await activeProvider.RecognizeAsync(image, ocrConfig, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            result = new OcrResult(
                IsSuccess: false,
                Text: string.Empty,
                DetectedLanguage: null,
                ProviderKind: ocrConfig.ActiveProvider,
                ErrorMessage: ex.Message,
                Elapsed: sw.Elapsed);
        }

        // If VisionLlm fails (network/auth/timeout) on a non-empty image and WindowsNative is registered,
        // automatically fall back to local Windows OCR so the user's screenshot capture is not lost.
        if (!result.IsSuccess &&
            ocrConfig.ActiveProvider == OcrProviderKind.VisionLlm &&
            image.ImageBytes is { Length: > 0 } &&
            _providers.TryGetValue(OcrProviderKind.WindowsNative, out var windowsFallback))
        {
            try
            {
                var fallbackResult = await windowsFallback.RecognizeAsync(image, ocrConfig, cancellationToken).ConfigureAwait(false);
                if (fallbackResult.IsSuccess)
                {
                    return PostProcessOcrResult(fallbackResult, ocrConfig);
                }

                return result with
                {
                    ErrorMessage = $"Vision LLM OCR failed ({result.ErrorMessage}); Windows OCR fallback also failed ({fallbackResult.ErrorMessage}).",
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception fallbackEx)
            {
                return result with
                {
                    ErrorMessage = $"Vision LLM OCR failed ({result.ErrorMessage}); Windows OCR fallback threw ({fallbackEx.Message}).",
                };
            }
        }

        if (!result.IsSuccess)
        {
            return result;
        }

        return PostProcessOcrResult(result, ocrConfig);
    }

    public async Task<IReadOnlyList<OcrLanguagePackInfo>> GetInstalledWindowsOcrLanguagesAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_providers.TryGetValue(OcrProviderKind.WindowsNative, out var windowsProvider))
        {
            return Array.Empty<OcrLanguagePackInfo>();
        }

        return await windowsProvider.GetAvailableLanguagesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProviderHealthCheckResult> TestActiveProviderAsync(
        OcrConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!_providers.TryGetValue(config.ActiveProvider, out var provider))
        {
            return new ProviderHealthCheckResult(
                IsSuccess: false,
                Message: $"OCR provider '{config.ActiveProvider}' is not registered.",
                Latency: TimeSpan.Zero);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            return await provider.TestAsync(config, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProviderHealthCheckResult(false, ex.Message, sw.Elapsed);
        }
    }

    private OcrResult PostProcessOcrResult(OcrResult result, OcrConfig ocrConfig)
    {
        if (string.IsNullOrWhiteSpace(result.Text))
        {
            return result;
        }

        var shouldMergeLineBreaks = ocrConfig.AutoMergeOcrLineBreaks &&
                                    (result.ProviderKind != OcrProviderKind.VisionLlm ||
                                     ocrConfig.PromptMode == OcrPromptMode.PlainText);
        if (shouldMergeLineBreaks)
        {
            var merged = _textPreprocessor.Preprocess(result.Text, mergeLineBreaks: true, isFromOcr: true);
            return result with { Text = merged };
        }

        var cleanedMultiLine = CleanSpuriousInterCjkSpacesPreservingLineBreaks(result.Text);
        return string.Equals(cleanedMultiLine, result.Text, StringComparison.Ordinal)
            ? result
            : result with { Text = cleanedMultiLine };
    }

    internal static string CleanSpuriousInterCjkSpacesPreservingLineBreaks(string text)
    {
        if (text.Length < 3 || (!text.Contains(' ') && !text.Contains('\t')))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch is ' ' or '\t')
            {
                var spaceStart = i;
                while (i < text.Length && text[i] is ' ' or '\t')
                {
                    i++;
                }

                if (sb.Length > 0 &&
                    i < text.Length &&
                    IsCjkOrCjkPunctuation(sb[^1]) &&
                    IsCjkOrCjkPunctuation(text[i]))
                {
                    // Drop spurious horizontal spaces inserted between CJK characters by OCR engines.
                    continue;
                }

                sb.Append(text, spaceStart, i - spaceStart);
                continue;
            }

            sb.Append(ch);
            i++;
        }

        return sb.ToString();
    }

    private static bool IsCjkOrCjkPunctuation(char ch) =>
        ch is (>= '\u4E00' and <= '\u9FFF')
            or (>= '\u3400' and <= '\u4DBF')
            or (>= '\uF900' and <= '\uFAFF')
            or (>= '\u3000' and <= '\u303F')
            or (>= '\u3040' and <= '\u30FF')
            or (>= '\uFF00' and <= '\uFFEF')
            or (>= '\uAC00' and <= '\uD7AF')
            or (>= '\u2014' and <= '\u2026')
            or '\u00B7';
}
