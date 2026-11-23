using PopTranslate.Core.Models;

namespace PopTranslate.Core.Contracts;

public interface IPolishOrchestrator
{
    IAsyncEnumerable<ProviderStreamUpdate> PolishParallelAsync(
        PolishRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TranslationResult>> PolishAllAsync(
        PolishRequest request,
        CancellationToken cancellationToken = default);

    IReadOnlyList<ProviderInstanceConfig> GetEligibleProviders();
}
