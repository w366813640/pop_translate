using System.Net;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;

namespace PopTranslate.Core.Networking;

public sealed class ProxyHttpClientFactory : IProxyHttpClientFactory, IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly ISettingsStore _settingsStore;
    private readonly ISecretProtector _secretProtector;
    private readonly Lock _cacheLock = new();
    private readonly Dictionary<string, SocketsHttpHandler> _handlers = new(StringComparer.Ordinal);
    private string _lastSettingsProxyKey;
    private long _cacheGeneration;
    private bool _disposed;

    public ProxyHttpClientFactory(ISettingsStore settingsStore, ISecretProtector secretProtector)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _secretProtector = secretProtector ?? throw new ArgumentNullException(nameof(secretProtector));
        _lastSettingsProxyKey = BuildCacheKey(_settingsStore.Current.Proxy ?? new ProxyConfig());
        _settingsStore.SettingsChanged += OnSettingsChanged;
    }

    public int CachedHandlerCount
    {
        get
        {
            lock (_cacheLock)
            {
                return _handlers.Count;
            }
        }
    }

    public long CacheGeneration => Volatile.Read(ref _cacheGeneration);

    public HttpClient CreateClient(ProxyConfig? overrideConfig = null, TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var effectiveConfig = overrideConfig ?? _settingsStore.Current.Proxy ?? new ProxyConfig();
        var cacheKey = BuildCacheKey(effectiveConfig);

        SocketsHttpHandler handler;
        lock (_cacheLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (overrideConfig is null)
            {
                _lastSettingsProxyKey = cacheKey;
            }

            if (!_handlers.TryGetValue(cacheKey, out handler!))
            {
                handler = CreateHandler(effectiveConfig);
                _handlers[cacheKey] = handler;
            }
        }

        return new HttpClient(handler, disposeHandler: false)
        {
            Timeout = timeout ?? DefaultTimeout,
        };
    }

    public SocketsHttpHandler CreateHandler(ProxyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.All,
        };

        switch (config.Mode)
        {
            case ProxyMode.System:
                handler.UseProxy = true;
                handler.Proxy = HttpClient.DefaultProxy;
                break;

            case ProxyMode.Direct:
                handler.UseProxy = false;
                handler.Proxy = null;
                break;

            case ProxyMode.Http:
                handler.UseProxy = true;
                handler.Proxy = CreateWebProxy("http", config);
                break;

            case ProxyMode.Socks5:
                handler.UseProxy = true;
                handler.Proxy = CreateWebProxy("socks5", config);
                break;

            default:
                handler.UseProxy = true;
                handler.Proxy = HttpClient.DefaultProxy;
                break;
        }

        return handler;
    }

    public void InvalidateProxyCache()
    {
        List<SocketsHttpHandler> toDispose;
        lock (_cacheLock)
        {
            toDispose = [.. _handlers.Values];
            _handlers.Clear();
            Interlocked.Increment(ref _cacheGeneration);
        }

        foreach (var handler in toDispose)
        {
            handler.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settingsStore.SettingsChanged -= OnSettingsChanged;
        InvalidateProxyCache();
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        var newProxyKey = BuildCacheKey(settings?.Proxy ?? new ProxyConfig());
        var proxyChanged = false;

        lock (_cacheLock)
        {
            if (_disposed)
            {
                return;
            }

            if (!string.Equals(_lastSettingsProxyKey, newProxyKey, StringComparison.Ordinal))
            {
                _lastSettingsProxyKey = newProxyKey;
                proxyChanged = true;
            }
        }

        if (proxyChanged)
        {
            InvalidateProxyCache();
        }
    }

    private WebProxy CreateWebProxy(string scheme, ProxyConfig config)
    {
        var cleanHost = NormalizeHost(config.Host);
        if (string.IsNullOrWhiteSpace(cleanHost))
        {
            throw new InvalidOperationException($"Proxy host must be specified when proxy mode is {config.Mode}.");
        }

        if (config.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException($"Proxy port {config.Port} is out of valid range (1-65535).");
        }

        var proxyUri = new UriBuilder(scheme, cleanHost, config.Port).Uri;
        var webProxy = new WebProxy(proxyUri)
        {
            BypassProxyOnLocal = config.BypassLocalAddresses,
        };

        if (!string.IsNullOrWhiteSpace(config.Username))
        {
            var password = _secretProtector.Unprotect(config.EncryptedPassword);
            webProxy.Credentials = new NetworkCredential(config.Username, password);
        }

        return webProxy;
    }

    private static string NormalizeHost(string? rawHost)
    {
        if (string.IsNullOrWhiteSpace(rawHost))
        {
            return string.Empty;
        }

        var trimmed = rawHost.Trim();
        if (trimmed.Contains("://", StringComparison.Ordinal) &&
            Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) &&
            !string.IsNullOrWhiteSpace(parsed.Host))
        {
            return parsed.Host;
        }

        return trimmed.TrimEnd('/');
    }

    private static string BuildCacheKey(ProxyConfig config) =>
        $"{(int)config.Mode}|{config.Host?.Trim()}|{config.Port}|{config.Username?.Trim()}|{config.EncryptedPassword}|{config.BypassLocalAddresses}";
}
