using System.Diagnostics;
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
        try
        {
            var result = await activeProvider.RecognizeAsync(image, ocrConfig, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess || !ocrConfig.AutoMergeOcrLineBreaks || string.IsNullOrWhiteSpace(result.Text))
            {
                return result;
            }

            var processed = _textPreprocessor.Preprocess(result.Text, mergeLineBreaks: true, isFromOcr: true);
            return result with { Text = processed };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new OcrResult(
                IsSuccess: false,
                Text: string.Empty,
                DetectedLanguage: null,
                ProviderKind: ocrConfig.ActiveProvider,
                ErrorMessage: ex.Message,
                Elapsed: sw.Elapsed);
        }
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
}
