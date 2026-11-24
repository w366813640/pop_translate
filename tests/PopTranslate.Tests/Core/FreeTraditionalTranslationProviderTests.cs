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
    public async Task TranslateAsync_GoogleEngine_UsesDictChromeExPrimaryEndpointWithUserAgentAndParsesAutoDetectArray()
    {
        Uri? capturedUri = null;
        string? capturedUserAgent = null;
        var googleJson = """
        [
          ["你好，世界。欢迎使用 PopTranslate。", "en"]
        ]
        """;

        var factory = new StubHttpFactory((req, _) =>
        {
            capturedUri = req.RequestUri;
            capturedUserAgent = req.Headers.UserAgent.ToString();
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

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("你好，世界。欢迎使用 PopTranslate。", result.TranslatedText);
        Assert.Equal(LanguageCode.English, result.DetectedSourceLanguage);
        Assert.NotNull(capturedUri);
        Assert.Contains("clients5.google.com/translate_a/t", capturedUri!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("client=dict-chrome-ex", capturedUri.Query, StringComparison.Ordinal);
        Assert.Contains("sl=auto", capturedUri.Query, StringComparison.Ordinal);
        Assert.Contains("tl=zh-CN", capturedUri.Query, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(capturedUserAgent));
        Assert.Contains("Mozilla/5.0", capturedUserAgent!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranslateAsync_GoogleEngine_ExplicitSourceLang_ParsesStringArrayResponse()
    {
        var factory = new StubHttpFactory((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""["你好世界。\n第二行。"]""", Encoding.UTF8, "application/json"),
            }));

        var provider = new FreeTraditionalTranslationProvider(factory);
        var config = new ProviderInstanceConfig
        {
            Kind = ProviderKind.FreeTraditional,
            FreeEngine = FreeTranslatorEngine.Google,
        };

        var result = await provider.TranslateAsync(
            new TranslationRequest("Hello world.\nSecond line.", LanguageCode.English, LanguageCode.ChineseSimplified),
            config);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("你好世界。\n第二行。", result.TranslatedText);
        Assert.Equal(LanguageCode.English, result.DetectedSourceLanguage);
    }

    [Fact]
    public async Task TranslateAsync_GoogleEngine_FailsOverAcrossEndpointsOn429HtmlOrNetworkErrors()
    {
        var requestedUrls = new List<string>();
        var singleJson = """
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
            var url = req.RequestUri!.AbsoluteUri;
            requestedUrls.Add(url);

            if (url.Contains("clients5.google.com/translate_a/t", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("<html><body>We're sorry...</body></html>", Encoding.UTF8, "text/html"),
                });
            }

            if (url.Contains("translate.googleapis.com/translate_a/t", StringComparison.Ordinal))
            {
                throw new HttpRequestException("Simulated TLS handshake failure.");
            }

            if (url.Contains("translate.googleapis.com/translate_a/single", StringComparison.Ordinal) &&
                url.Contains("client=dict-chrome-ex", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html><body>Captcha sorry page</body></html>", Encoding.UTF8, "text/html"),
                });
            }

            if (url.Contains("translate.googleapis.com/translate_a/single", StringComparison.Ordinal) &&
                url.Contains("client=gtx", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(singleJson, Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var provider = new FreeTraditionalTranslationProvider(factory);
        var config = new ProviderInstanceConfig
        {
            Kind = ProviderKind.FreeTraditional,
            FreeEngine = FreeTranslatorEngine.Google,
        };

        var result = await provider.TranslateAsync(
            new TranslationRequest("Hello, world. Welcome to PopTranslate.", LanguageCode.Auto, LanguageCode.ChineseSimplified),
            config);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("你好，世界。欢迎使用 PopTranslate。", result.TranslatedText);
        Assert.Equal(LanguageCode.English, result.DetectedSourceLanguage);
        Assert.Equal(4, requestedUrls.Count);
        Assert.Contains("clients5.google.com/translate_a/t", requestedUrls[0], StringComparison.Ordinal);
        Assert.Contains("translate.googleapis.com/translate_a/t", requestedUrls[1], StringComparison.Ordinal);
        Assert.Contains("translate.googleapis.com/translate_a/single", requestedUrls[2], StringComparison.Ordinal);
        Assert.Contains("client=dict-chrome-ex", requestedUrls[2], StringComparison.Ordinal);
        Assert.Contains("translate.googleapis.com/translate_a/single", requestedUrls[3], StringComparison.Ordinal);
        Assert.Contains("client=gtx", requestedUrls[3], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranslateAsync_GoogleEngine_LongTextUsesPostFormUrlEncodedOnPrimaryEndpoint()
    {
        HttpMethod? capturedMethod = null;
        string? capturedFormBody = null;
        Uri? capturedUri = null;
        var longText = new string('A', 1600);

        var factory = new StubHttpFactory(async (req, ct) =>
        {
            capturedMethod = req.Method;
            capturedUri = req.RequestUri;
            capturedFormBody = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""[["长文本译文", "en"]]""", Encoding.UTF8, "application/json"),
            };
        });

        var provider = new FreeTraditionalTranslationProvider(factory);
        var config = new ProviderInstanceConfig
        {
            Kind = ProviderKind.FreeTraditional,
            FreeEngine = FreeTranslatorEngine.Google,
        };

        var result = await provider.TranslateAsync(
            new TranslationRequest(longText, LanguageCode.Auto, LanguageCode.ChineseSimplified),
            config);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("长文本译文", result.TranslatedText);
        Assert.Equal(HttpMethod.Post, capturedMethod);
        Assert.NotNull(capturedUri);
        Assert.DoesNotContain("&q=", capturedUri!.AbsoluteUri, StringComparison.Ordinal);
        Assert.NotNull(capturedFormBody);
        Assert.StartsWith("q=", capturedFormBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranslateAsync_GoogleEngine_CustomBaseUrl_RoutesThroughConfiguredMirror()
    {
        Uri? capturedUri = null;

        var factory = new StubHttpFactory((req, _) =>
        {
            capturedUri = req.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""[["镜像译文", "en"]]""", Encoding.UTF8, "application/json"),
            });
        });

        var provider = new FreeTraditionalTranslationProvider(factory);
        var config = new ProviderInstanceConfig
        {
            Kind = ProviderKind.FreeTraditional,
            FreeEngine = FreeTranslatorEngine.Google,
            BaseUrl = "https://mirror.example.com",
        };

        var result = await provider.TranslateAsync(
            new TranslationRequest("Mirror test", LanguageCode.Auto, LanguageCode.ChineseSimplified),
            config);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("镜像译文", result.TranslatedText);
        Assert.NotNull(capturedUri);
        Assert.StartsWith("https://mirror.example.com/translate_a/t?", capturedUri!.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranslateAsync_GoogleEngine_InvalidCustomBaseUrl_ReturnsValidationFailureWithoutNetworkCalls()
    {
        var networkCalled = false;
        var factory = new StubHttpFactory((_, _) =>
        {
            networkCalled = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var provider = new FreeTraditionalTranslationProvider(factory);
        var config = new ProviderInstanceConfig
        {
            Kind = ProviderKind.FreeTraditional,
            FreeEngine = FreeTranslatorEngine.Google,
            BaseUrl = "ftp://invalid-mirror.example.com",
        };

        var result = await provider.TranslateAsync(
            new TranslationRequest("Hello", LanguageCode.English, LanguageCode.ChineseSimplified),
            config);

        Assert.False(result.IsSuccess);
        Assert.False(networkCalled);
        Assert.Contains("Base URL", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TranslateAsync_AutoEngine_FallsBackToBingWhenAllGoogleEndpointsFail()
    {
        var requestedUrls = new List<string>();
        string? capturedAuthHeader = null;
        string? capturedBingBody = null;

        var factory = new StubHttpFactory(async (req, ct) =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            requestedUrls.Add(url);

            if (url.Contains("google.com", StringComparison.Ordinal) ||
                url.Contains("googleapis.com", StringComparison.Ordinal))
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
        Assert.Equal(6, requestedUrls.Count);
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
