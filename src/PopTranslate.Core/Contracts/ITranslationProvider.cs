using PopTranslate.Core.Models;

namespace PopTranslate.Core.Contracts;

public interface ITranslationProvider
{
    ProviderKind Kind { get; }

    bool SupportsStreaming { get; }

    IAsyncEnumerable<TranslationChunk> TranslateStreamAsync(
        TranslationRequest request,
        ProviderInstanceConfig config,
        CancellationToken cancellationToken = default);

    Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        ProviderInstanceConfig config,
        CancellationToken cancellationToken = default);

    Task<ProviderHealthCheckResult> TestConnectionAsync(
        ProviderInstanceConfig config,
        CancellationToken cancellationToken = default);
}
