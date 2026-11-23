using System.Net;
using System.Text;
using System.Text.Json;
using PopTranslate.Core.Configuration;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using PopTranslate.Core.Translation;
using Xunit;

namespace PopTranslate.Tests.Core;

public sealed class DeepLTranslationProviderTests
{
    private readonly DpapiSecretProtector _protector = new();

    [Fact]
    public async Task TranslateAsync_FreeKeyAutoTier_SelectsFreeEndpointAndParsesResponse()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;

        var factory = new StubHttpFactory(async (req, ct) =>
        {
            capturedRequest = req;
            capturedBody = await req.Content!.ReadAsStringAsync(ct);

            var responseJson = """
            {
              "translations": [
                {
                  "detected_source_language": "EN",
                  "text": "你好，世界"
                }
              ]
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        });

        var provider = new DeepLTranslationProvider(factory, _protector);
        var config = new ProviderInstanceConfig
        {
            Id = "deepl-free",
            DisplayName = "DeepL Free",
            Kind = ProviderKind.DeepL,
            DeepLTier = DeepLEndpointTier.Auto,
            EncryptedApiKey = _protector.Protect("12345678-abcd-ef00-1234-56789abcdef0:fx"),
        };

        var result = await provider.TranslateAsync(
            new TranslationRequest("Hello world", LanguageCode.Auto, LanguageCode.ChineseSimplified),
            config);

        Assert.True(result.IsSuccess);
        Assert.Equal("你好，世界", result.TranslatedText);
        Assert.Equal(LanguageCode.English, result.DetectedSourceLanguage);
        Assert.NotNull(capturedRequest);
        Assert.Equal("https://api-free.deepl.com/v2/translate", capturedRequest!.RequestUri!.AbsoluteUri);
        Assert.Equal("DeepL-Auth-Key", capturedRequest.Headers.Authorization?.Scheme);
        Assert.Equal("12345678-abcd-ef00-1234-56789abcdef0:fx", capturedRequest.Headers.Authorization?.Parameter);

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("Hello world", doc.RootElement.GetProperty("text")[0].GetString());
        Assert.Equal("ZH-HANS", doc.RootElement.GetProperty("target_lang").GetString());
        Assert.False(doc.RootElement.TryGetProperty("source_lang", out _));
    }

    [Fact]
    public async Task TranslateAsync_ProKeyAutoTier_SelectsProEndpointAndSendsSourceLang()
    {
        Uri? capturedUri = null;
        string? capturedBody = null;

        var factory = new StubHttpFactory(async (req, ct) =>
        {
            capturedUri = req.RequestUri;
            capturedBody = await req.Content!.ReadAsStringAsync(ct);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"translations":[{"detected_source_language":"ZH","text":"Hello world"}]}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        });

        var provider = new DeepLTranslationProvider(factory, _protector);
        var config = new ProviderInstanceConfig
        {
            Kind = ProviderKind.DeepL,
            DeepLTier = DeepLEndpointTier.Auto,
            EncryptedApiKey = _protector.Protect("pro-key-without-fx-suffix"),
        };

        var result = await provider.TranslateAsync(
            new TranslationRequest("你好世界", LanguageCode.ChineseSimplified, LanguageCode.English),
            config);

        Assert.True(result.IsSuccess);
        Assert.Equal("https://api.deepl.com/v2/translate", capturedUri!.AbsoluteUri);
        Assert.Equal("Hello world", result.TranslatedText);

        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("EN-US", doc.RootElement.GetProperty("target_lang").GetString());
        Assert.Equal("ZH", doc.RootElement.GetProperty("source_lang").GetString());
    }

    [Fact]
    public async Task TranslateAsync_MissingApiKey_ReturnsValidationErrorWithoutHttpCall()
    {
        var called = false;
        var factory = new StubHttpFactory((_, _) =>
        {
            called = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var provider = new DeepLTranslationProvider(factory, _protector);
        var config = new ProviderInstanceConfig
        {
            Kind = ProviderKind.DeepL,
            EncryptedApiKey = string.Empty,
        };

        var result = await provider.TranslateAsync(
            new TranslationRequest("Hello", LanguageCode.English, LanguageCode.ChineseSimplified),
            config);

        Assert.False(result.IsSuccess);
        Assert.False(called);
        Assert.Contains("API key", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TranslateAsync_Http456QuotaExceeded_ReturnsActionableError()
    {
        var factory = new StubHttpFactory((_, _) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)456)
            {
                Content = new StringContent("""{"message":"Quota exceeded"}""", Encoding.UTF8, "application/json"),
            }));

        var provider = new DeepLTranslationProvider(factory, _protector);
        var config = new ProviderInstanceConfig
        {
            Kind = ProviderKind.DeepL,
            EncryptedApiKey = _protector.Protect("key:fx"),
        };

        var result = await provider.TranslateAsync(
            new TranslationRequest("Hello", LanguageCode.English, LanguageCode.ChineseSimplified),
            config);

        Assert.False(result.IsSuccess);
        Assert.Contains("456", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("quota", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("zh-Hans", "ZH-HANS")]
    [InlineData("zh-Hant", "ZH-HANT")]
    [InlineData("en", "EN-US")]
    [InlineData("ja", "JA")]
    [InlineData("pt", "PT-PT")]
    public void MapTargetLanguage_MapsDeepLCodes(string code, string expected)
    {
        Assert.Equal(expected, DeepLTranslationProvider.MapTargetLanguage(LanguageCode.Parse(code)));
    }

    private sealed class StubHttpFactory(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : IProxyHttpClientFactory
    {
        public HttpClient CreateClient(ProxyConfig? overrideConfig = null, TimeSpan? timeout = null) =>
            new(new DelegateHandler(responder)) { Timeout = timeout ?? TimeSpan.FromSeconds(20) };

        public void InvalidateProxyCache()
        {
        }
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            responder(request, cancellationToken);
    }
}
