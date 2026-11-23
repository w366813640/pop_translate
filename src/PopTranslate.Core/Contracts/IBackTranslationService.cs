using PopTranslate.Core.Models;

namespace PopTranslate.Core.Contracts;

public interface IBackTranslationService
{
    IAsyncEnumerable<ProviderStreamUpdate> BackTranslateStreamAsync(
        BackTranslationRequest request,
        CancellationToken cancellationToken = default);

    Task<TranslationResult> BackTranslateAsync(
        BackTranslationRequest request,
        CancellationToken cancellationToken = default);
}
