using PopTranslate.Core.Models;

namespace PopTranslate.Core.Contracts;

public interface ITranslationOrchestrator
{
    IAsyncEnumerable<ProviderStreamUpdate> TranslateParallelAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default);

    Task<TranslationBatchResult> TranslateAllAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default);

    Task<ProviderHealthCheckResult> TestProviderAsync(
        ProviderInstanceConfig config,
        CancellationToken cancellationToken = default);
}
