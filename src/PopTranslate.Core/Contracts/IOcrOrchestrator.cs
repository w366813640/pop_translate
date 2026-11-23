using PopTranslate.Core.Models;

namespace PopTranslate.Core.Contracts;

public interface IOcrOrchestrator
{
    Task<OcrResult> RecognizeAsync(
        OcrImage image,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OcrLanguagePackInfo>> GetInstalledWindowsOcrLanguagesAsync(
        CancellationToken cancellationToken = default);

    Task<ProviderHealthCheckResult> TestActiveProviderAsync(
        OcrConfig config,
        CancellationToken cancellationToken = default);
}
