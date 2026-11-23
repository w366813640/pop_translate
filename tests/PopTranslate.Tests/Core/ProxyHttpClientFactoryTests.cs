using System.Net;
using PopTranslate.Core.Configuration;
using PopTranslate.Core.Models;
using PopTranslate.Core.Networking;
using Xunit;

namespace PopTranslate.Tests.Core;

public sealed class ProxyHttpClientFactoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DpapiSecretProtector _protector = new();
    private readonly JsonSettingsStore _settingsStore;
    private readonly ProxyHttpClientFactory _factory;

    public ProxyHttpClientFactoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PopTranslateProxyTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _settingsStore = new JsonSettingsStore(_protector, Path.Combine(_tempDir, "settings.json"));
        _factory = new ProxyHttpClientFactory(_settingsStore, _protector);
    }

    public void Dispose()
    {
        _factory.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors.
        }
    }

    [Fact]
    public void CreateHandler_SystemMode_UsesDefaultSystemProxy()
    {
        using var handler = _factory.CreateHandler(new ProxyConfig { Mode = ProxyMode.System });

        Assert.True(handler.UseProxy);
        Assert.Same(HttpClient.DefaultProxy, handler.Proxy);
        Assert.Equal(TimeSpan.FromMinutes(2), handler.PooledConnectionLifetime);
    }

    [Fact]
    public void CreateHandler_DirectMode_DisablesProxy()
    {
        using var handler = _factory.CreateHandler(new ProxyConfig { Mode = ProxyMode.Direct });

        Assert.False(handler.UseProxy);
        Assert.Null(handler.Proxy);
    }

    [Fact]
    public void CreateHandler_HttpModeWithAuth_ConfiguresWebProxyAndDecryptsPassword()
    {
        var config = new ProxyConfig
        {
            Mode = ProxyMode.Http,
            Host = "127.0.0.1",
            Port = 7890,
            Username = "alice",
            EncryptedPassword = _protector.Protect("s3cr3t-pass"),
            BypassLocalAddresses = true,
        };

        using var handler = _factory.CreateHandler(config);

        Assert.True(handler.UseProxy);
        var webProxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.Equal(new Uri("http://127.0.0.1:7890/"), webProxy.Address);
        Assert.True(webProxy.BypassProxyOnLocal);

        var cred = Assert.IsType<NetworkCredential>(webProxy.Credentials);
        Assert.Equal("alice", cred.UserName);
        Assert.Equal("s3cr3t-pass", cred.Password);
    }

    [Fact]
    public void CreateHandler_Socks5Mode_ConfiguresSocks5Uri()
    {
        var config = new ProxyConfig
        {
            Mode = ProxyMode.Socks5,
            Host = "socks5://127.0.0.1",
            Port = 1080,
            BypassLocalAddresses = false,
        };

        using var handler = _factory.CreateHandler(config);

        Assert.True(handler.UseProxy);
        var webProxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.Equal(new Uri("socks5://127.0.0.1:1080/"), webProxy.Address);
        Assert.False(webProxy.BypassProxyOnLocal);
        Assert.Null(webProxy.Credentials);
    }

    [Fact]
    public async Task CreateClient_CachesHandlersAndInvalidatesOnSettingsChangedOrExplicitCall()
    {
        using (var client1 = _factory.CreateClient(timeout: TimeSpan.FromSeconds(15)))
        {
            Assert.Equal(TimeSpan.FromSeconds(15), client1.Timeout);
            Assert.Equal(1, _factory.CachedHandlerCount);
        }

        // Creating another client with same config reuses cached handler
        using (var client2 = _factory.CreateClient())
        {
            Assert.Equal(1, _factory.CachedHandlerCount);
        }

        var generationBefore = _factory.CacheGeneration;
        _factory.InvalidateProxyCache();
        Assert.Equal(0, _factory.CachedHandlerCount);
        Assert.Equal(generationBefore + 1, _factory.CacheGeneration);

        using (var client3 = _factory.CreateClient())
        {
            Assert.Equal(1, _factory.CachedHandlerCount);
        }

        // Saving updated proxy settings invalidates cache automatically via SettingsChanged
        var updated = AppSettings.CreateDefault();
        updated.Proxy.Mode = ProxyMode.Direct;
        await _settingsStore.SaveAsync(updated);

        Assert.Equal(0, _factory.CachedHandlerCount);
        Assert.True(_factory.CacheGeneration > generationBefore + 1);
    }

    [Fact]
    public async Task SettingsChanged_UnrelatedSettingChange_DoesNotInvalidateCachedHandlers()
    {
        using (var client = _factory.CreateClient())
        {
            Assert.NotNull(client);
            Assert.Equal(1, _factory.CachedHandlerCount);
        }

        var generationBefore = _factory.CacheGeneration;

        // Save unrelated window/selection settings without changing Proxy config
        var unrelated = _settingsStore.Current.Clone();
        unrelated.Window.PopupWidth = 520;
        unrelated.Selection.EnableHoverIcon = false;
        await _settingsStore.SaveAsync(unrelated);

        Assert.Equal(1, _factory.CachedHandlerCount);
        Assert.Equal(generationBefore, _factory.CacheGeneration);

        // Now change Proxy config -> MUST invalidate cached handlers
        var proxyChanged = _settingsStore.Current.Clone();
        proxyChanged.Proxy.Mode = ProxyMode.Http;
        proxyChanged.Proxy.Host = "127.0.0.1";
        proxyChanged.Proxy.Port = 8888;
        await _settingsStore.SaveAsync(proxyChanged);

        Assert.Equal(0, _factory.CachedHandlerCount);
        Assert.Equal(generationBefore + 1, _factory.CacheGeneration);
    }

    [Fact]
    public async Task SettingsChanged_AfterLoadAsyncWithCustomProxy_UnrelatedSaveDoesNotInvalidate()
    {
        var path = Path.Combine(_tempDir, "loaded-proxy-settings.json");
        var seedStore = new JsonSettingsStore(_protector, path);
        var seed = AppSettings.CreateDefault();
        seed.Proxy.Mode = ProxyMode.Http;
        seed.Proxy.Host = "127.0.0.1";
        seed.Proxy.Port = 8080;
        await seedStore.SaveAsync(seed);

        var loadedStore = new JsonSettingsStore(_protector, path);
        using var factory = new ProxyHttpClientFactory(loadedStore, _protector);
        await loadedStore.LoadAsync();

        using (var client = factory.CreateClient())
        {
            Assert.NotNull(client);
            Assert.Equal(1, factory.CachedHandlerCount);
        }

        var generationBefore = factory.CacheGeneration;

        var unrelated = loadedStore.Current.Clone();
        unrelated.Window.FixedX = 400;
        await loadedStore.SaveAsync(unrelated);

        Assert.Equal(1, factory.CachedHandlerCount);
        Assert.Equal(generationBefore, factory.CacheGeneration);
    }

    [Fact]
    public void CreateHandler_InvalidHostOrPort_ThrowsInvalidOperationException()
    {
        Assert.Throws<InvalidOperationException>(() =>
            _factory.CreateHandler(new ProxyConfig { Mode = ProxyMode.Http, Host = "", Port = 7890 }));

        Assert.Throws<InvalidOperationException>(() =>
            _factory.CreateHandler(new ProxyConfig { Mode = ProxyMode.Socks5, Host = "127.0.0.1", Port = 70000 }));
    }
}
