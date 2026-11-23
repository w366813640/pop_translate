using System.Net;
using System.Text;
using System.Text.Json;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using PopTranslate.Core.Translation;
using Xunit;

namespace PopTranslate.Tests.Core;

public sealed class FreeTraditionalTranslationProviderTests
{
    [Fact]
    public async Task TranslateAsync_GoogleEngine_ParsesMultiSegmentResponseAndMapsChineseCodes()
    {
        Uri? capturedUri = null;
        var googleJson = """
        [
          [
            ["你好，世界。", "Hello, world.", null, null, 10],
            ["欢迎使用 PopTranslate。", "Welcome to PopTranslate.", null, null, 10]
          ],
          null,
          "en"
        ]
        """;

        var factory = new StubHttpFactory((req, _) =>
        {
            capturedUri = req.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(googleJson, Encoding.UTF8, "application/json"),
            });
        });

        var provider = new FreeTraditionalTranslationProvider(factory);
        var config = new ProviderInstanceConfig
        {
            Id = "free-1",
            DisplayName = "Free Translate",
            Kind = ProviderKind.FreeTraditional,
            FreeEngine = FreeTranslatorEngine.Google,
        };

        var result = await provider.TranslateAsync(
            new TranslationRequest("Hello, world. Welcome to PopTranslate.", LanguageCode.Auto, LanguageCode.ChineseSimplified),
            config);

        Assert.True(result.IsSuccess);
        Assert.Equal("你好，世界。欢迎使用 PopTranslate。", result.TranslatedText);
        Assert.Equal(LanguageCode.English, result.DetectedSourceLanguage);
        Assert.NotNull(capturedUri);
        Assert.Contains("translate.googleapis.com/translate_a/single", capturedUri!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("sl=auto", capturedUri.Query, StringComparison.Ordinal);
        Assert.Contains("tl=zh-CN", capturedUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranslateAsync_AutoEngine_FallsBackToBingWhenGoogleFails()
    {
        var requestedUrls = new List<string>();
        string? capturedAuthHeader = null;
        string? capturedBingBody = null;

        var factory = new StubHttpFactory(async (req, ct) =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            requestedUrls.Add(url);

            if (url.Contains("translate.googleapis.com", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("Too Many Requests", Encoding.UTF8, "text/plain"),
                };
            }

            if (url.Contains("edge.microsoft.com/translate/auth", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("mock-bing-jwt-token", Encoding.UTF8, "text/plain"),
                };
            }

            if (url.Contains("api-edge.cognitive.microsofttranslator.com/translate", StringComparison.Ordinal))
            {
                capturedAuthHeader = req.Headers.Authorization?.ToString();
                capturedBingBody = await req.Content!.ReadAsStringAsync(ct);

                var bingJson = """
                [
                  {
                    "detectedLanguage": { "language": "zh-Hans", "score": 1.0 },
                    "translations": [
                      { "text": "Good morning", "to": "en" }
                    ]
                  }
                ]
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(bingJson, Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var provider = new FreeTraditionalTranslationProvider(factory);
        var config = new ProviderInstanceConfig
        {
            Id = "free-auto",
            DisplayName = "Free Auto",
            Kind = ProviderKind.FreeTraditional,
            FreeEngine = FreeTranslatorEngine.Auto,
        };

        var result = await provider.TranslateAsync(
            new TranslationRequest("早上好", LanguageCode.Auto, LanguageCode.English),
            config);

        Assert.True(result.IsSuccess);
        Assert.Equal("Good morning", result.TranslatedText);
        Assert.Equal(LanguageCode.ChineseSimplified, result.DetectedSourceLanguage);
        Assert.Equal("Bearer mock-bing-jwt-token", capturedAuthHeader);
        Assert.NotNull(capturedBingBody);

        using var doc = JsonDocument.Parse(capturedBingBody!);
        Assert.Equal("早上好", doc.RootElement[0].GetProperty("Text").GetString());
        Assert.Equal(3, requestedUrls.Count);
    }

    [Fact]
    public async Task TranslateAsync_BingEngine_CachesAuthTokenAcrossRequests()
    {
        var authCalls = 0;

        var factory = new StubHttpFactory((req, _) =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("edge.microsoft.com/translate/auth", StringComparison.Ordinal))
            {
                authCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("cached-token-xyz", Encoding.UTF8, "text/plain"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """[{"translations":[{"text":"Bonjour","to":"fr"}]}]""",
                    Encoding.UTF8,
                    "application/json"),
            });
        });

        var provider = new FreeTraditionalTranslationProvider(factory);
        var config = new ProviderInstanceConfig
        {
            Kind = ProviderKind.FreeTraditional,
            FreeEngine = FreeTranslatorEngine.Bing,
        };

        var r1 = await provider.TranslateAsync(new TranslationRequest("Hello", LanguageCode.English, LanguageCode.French), config);
        var r2 = await provider.TranslateAsync(new TranslationRequest("Hello again", LanguageCode.English, LanguageCode.French), config);

        Assert.True(r1.IsSuccess);
        Assert.True(r2.IsSuccess);
        Assert.Equal(1, authCalls);
    }

    [Fact]
    public async Task TranslateAsync_AutoEngine_BothGoogleAndBingFail_ReturnsCombinedError()
    {
        var factory = new StubHttpFactory((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        var provider = new FreeTraditionalTranslationProvider(factory);
        var config = new ProviderInstanceConfig
        {
            Kind = ProviderKind.FreeTraditional,
            FreeEngine = FreeTranslatorEngine.Auto,
        };

        var result = await provider.TranslateAsync(
            new TranslationRequest("Hello", LanguageCode.English, LanguageCode.ChineseSimplified),
            config);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("Google", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Bing", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("zh-Hant", "zh-TW")]
    [InlineData("auto", "auto")]
    [InlineData("en", "en")]
    [InlineData("ja", "ja")]
    public void MapGoogleLanguageCode_MapsExpectedCodes(string input, string expected)
    {
        Assert.Equal(expected, FreeTraditionalTranslationProvider.MapGoogleLanguageCode(LanguageCode.Parse(input)));
    }

    private sealed class StubHttpFactory(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : IProxyHttpClientFactory
    {
        public HttpClient CreateClient(ProxyConfig? overrideConfig = null, TimeSpan? timeout = null) =>
            new(new DelegateHandler(responder)) { Timeout = timeout ?? TimeSpan.FromSeconds(15) };

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
