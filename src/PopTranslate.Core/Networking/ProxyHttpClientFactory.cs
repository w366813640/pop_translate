using System.Net;
using System.Net.Sockets;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;

namespace PopTranslate.Core.Networking;

public sealed class ProxyHttpClientFactory : IProxyHttpClientFactory, IDisposable
{
    public const int MaxCachedHandlers = 16;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RetiredHandlerGraceDelay = TimeSpan.FromSeconds(60);

    private readonly ISettingsStore _settingsStore;
    private readonly ISecretProtector _secretProtector;
    private readonly Lock _cacheLock = new();
    private readonly Dictionary<string, SocketsHttpHandler> _handlers = new(StringComparer.Ordinal);
    private readonly Queue<string> _cacheOrder = new();
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
        SocketsHttpHandler? evictedHandler = null;
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

                while (_handlers.Count >= MaxCachedHandlers && _cacheOrder.TryDequeue(out var oldestKey))
                {
                    if (_handlers.Remove(oldestKey, out var removed))
                    {
                        evictedHandler = removed;
                        break;
                    }
                }

                _handlers[cacheKey] = handler;
                _cacheOrder.Enqueue(cacheKey);
            }
        }

        if (evictedHandler is not null)
        {
            ScheduleDelayedDispose(evictedHandler);
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
            ConnectTimeout = DefaultConnectTimeout,
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
        List<SocketsHttpHandler> toRetire;
        lock (_cacheLock)
        {
            toRetire = [.. _handlers.Values];
            _handlers.Clear();
            _cacheOrder.Clear();
            Interlocked.Increment(ref _cacheGeneration);
        }

        foreach (var handler in toRetire)
        {
            ScheduleDelayedDispose(handler);
        }
    }

    public void Dispose()
    {
        List<SocketsHttpHandler> toDispose;
        lock (_cacheLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            toDispose = [.. _handlers.Values];
            _handlers.Clear();
            _cacheOrder.Clear();
            Interlocked.Increment(ref _cacheGeneration);
        }

        _settingsStore.SettingsChanged -= OnSettingsChanged;

        foreach (var handler in toDispose)
        {
            try
            {
                handler.Dispose();
            }
            catch
            {
                // Ignore disposal errors during shutdown.
            }
        }
    }

    private static void ScheduleDelayedDispose(SocketsHttpHandler handler)
    {
        _ = Task.Delay(RetiredHandlerGraceDelay).ContinueWith(
            _ =>
            {
                try
                {
                    handler.Dispose();
                }
                catch
                {
                    // Ignore disposal errors on retired handlers.
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
        var (cleanHost, effectivePort) = ExtractHostAndPort(config.Host, config.Port);
        if (string.IsNullOrWhiteSpace(cleanHost))
        {
            throw new InvalidOperationException($"Proxy host must be specified when proxy mode is {config.Mode}.");
        }

        if (effectivePort is < 1 or > 65535)
        {
            throw new InvalidOperationException($"Proxy port {effectivePort} is out of valid range (1-65535).");
        }

        Uri proxyUri;
        try
        {
            proxyUri = new UriBuilder(scheme, cleanHost, effectivePort).Uri;
        }
        catch (UriFormatException ex)
        {
            throw new InvalidOperationException($"Invalid proxy host '{config.Host}': {ex.Message}", ex);
        }

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

    private static (string Host, int Port) ExtractHostAndPort(string? rawHost, int configuredPort)
    {
        if (string.IsNullOrWhiteSpace(rawHost))
        {
            return (string.Empty, configuredPort);
        }

        var trimmed = rawHost.Trim();
        var schemeIdx = trimmed.IndexOf("://", StringComparison.Ordinal);
        var authority = schemeIdx >= 0 ? trimmed[(schemeIdx + 3)..] : trimmed;

        var slashIdx = authority.IndexOfAny(['/', '?', '#']);
        if (slashIdx >= 0)
        {
            authority = authority[..slashIdx];
        }

        var atIdx = authority.LastIndexOf('@');
        if (atIdx >= 0)
        {
            authority = authority[(atIdx + 1)..];
        }

        authority = authority.Trim();
        if (string.IsNullOrWhiteSpace(authority))
        {
            return (string.Empty, configuredPort);
        }

        // Bare IPv6 address without brackets (e.g., "::1")
        if (!authority.StartsWith('[') &&
            IPAddress.TryParse(authority, out var bareIp) &&
            bareIp.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return (authority, configuredPort);
        }

        // Bracketed IPv6 address, e.g., "[::1]" or "[::1]:7890"
        if (authority.StartsWith('['))
        {
            var closeBracket = authority.IndexOf(']');
            if (closeBracket > 1)
            {
                var ipv6Host = authority[1..closeBracket];
                var remainder = authority[(closeBracket + 1)..];
                if (remainder.Length == 0)
                {
                    return (ipv6Host, configuredPort);
                }

                if (remainder.StartsWith(':') && int.TryParse(remainder[1..], out var v6Port))
                {
                    return (ipv6Host, v6Port);
                }
            }

            throw new InvalidOperationException($"Invalid proxy host format '{rawHost}'.");
        }

        // Host or Host:Port (IPv4 or hostname)
        var colonIdx = authority.LastIndexOf(':');
        if (colonIdx > 0 && authority.IndexOf(':') == colonIdx)
        {
            var hostPart = authority[..colonIdx].Trim();
            var portPart = authority[(colonIdx + 1)..].Trim();
            if (portPart.Length > 0)
            {
                if (!int.TryParse(portPart, out var parsedPort))
                {
                    throw new InvalidOperationException($"Invalid proxy port '{portPart}' in host '{rawHost}'.");
                }

                return (hostPart, parsedPort);
            }

            return (hostPart, configuredPort);
        }

        return (authority, configuredPort);
    }

    private static string BuildCacheKey(ProxyConfig config) =>
        $"{(int)config.Mode}|{config.Host?.Trim()}|{config.Port}|{config.Username?.Trim()}|{config.EncryptedPassword}|{config.BypassLocalAddresses}";
}
