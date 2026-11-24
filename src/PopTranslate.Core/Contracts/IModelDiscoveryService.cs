using PopTranslate.Core.Models;

namespace PopTranslate.Core.Contracts;

public interface IModelDiscoveryService
{
    Task<IReadOnlyList<string>> FetchModelsAsync(string baseUrl, string? plaintextApiKey, ProxyConfig? proxyOverride = null, CancellationToken cancellationToken = default);
}
