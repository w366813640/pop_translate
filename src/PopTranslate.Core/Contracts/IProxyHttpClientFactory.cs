using PopTranslate.Core.Models;

namespace PopTranslate.Core.Contracts;

public interface IProxyHttpClientFactory
{
    HttpClient CreateClient(ProxyConfig? overrideConfig = null, TimeSpan? timeout = null);

    void InvalidateProxyCache();
}
