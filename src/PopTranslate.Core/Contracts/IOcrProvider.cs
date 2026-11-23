using PopTranslate.Core.Models;

namespace PopTranslate.Core.Contracts;

public interface IOcrProvider
{
    OcrProviderKind Kind { get; }

    string DisplayName { get; }

    Task<OcrResult> RecognizeAsync(
        OcrImage image,
        OcrConfig config,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OcrLanguagePackInfo>> GetAvailableLanguagesAsync(
        CancellationToken cancellationToken = default);

    Task<ProviderHealthCheckResult> TestAsync(
        OcrConfig config,
        CancellationToken cancellationToken = default);
}
