using PopTranslate.Core.Models;

namespace PopTranslate.Core.Contracts;

public interface IPolishProvider
{
    ProviderKind Kind { get; }

    bool SupportsStreaming { get; }

    IAsyncEnumerable<TranslationChunk> PolishStreamAsync(
        PolishRequest request,
        ProviderInstanceConfig config,
        CancellationToken cancellationToken = default);

    Task<TranslationResult> PolishAsync(
        PolishRequest request,
        ProviderInstanceConfig config,
        CancellationToken cancellationToken = default);
}
