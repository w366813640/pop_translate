using System.Net;
using System.Text;
using System.Text.Json;
using PopTranslate.Core.Configuration;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using PopTranslate.Core.Translation;
using Xunit;

namespace PopTranslate.Tests.Core;

public sealed class OpenAiCompatibleTranslationProviderTests
{
    private readonly DpapiSecretProtector _protector = new();

    [Fact]
    public async Task TranslateStreamAsync_ParsesSseChunksAndSendsExpectedHeadersAndPayload()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;

        var ssePayload =
            ": keep-alive\n\n" +
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"，世界\"}}]}\n\n" +
            "data: [DONE]\n\n";

        var factory = new StubProxyHttpClientFactory(async (req, ct) =>
        {
            capturedRequest = req;
            capturedBody = await req.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ssePayload, Encoding.UTF8, "text/event-stream"),
            };
        });

        var provider = new OpenAiCompatibleTranslationProvider(factory, _protector);
        var config = new ProviderInstanceConfig
        {
            Id = "openai-1",
            DisplayName = "My LLM",
            Kind = ProviderKind.OpenAiCompatible,
            BaseUrl = "https://api.example.com/v1/",
            Model = "gpt-4o-mini",
            EncryptedApiKey = _protector.Protect("sk-secret-123"),
            SystemPrompt = "Translate from {source} to {target}.",
            Temperature = 0.3,
        };

        var request = new TranslationRequest("Hello world", LanguageCode.English, LanguageCode.ChineseSimplified);
        var chunks = new List<TranslationChunk>();
        await foreach (var chunk in provider.TranslateStreamAsync(request, config))
        {
            chunks.Add(chunk);
        }

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("https://api.example.com/v1/chat/completions", capturedRequest.RequestUri!.ToString());
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization?.Scheme);
        Assert.Equal("sk-secret-123", capturedRequest.Headers.Authorization?.Parameter);

        Assert.NotNull(capturedBody);
        using var bodyDoc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("gpt-4o-mini", bodyDoc.RootElement.GetProperty("model").GetString());
        Assert.True(bodyDoc.RootElement.GetProperty("stream").GetBoolean());
        var messages = bodyDoc.RootElement.GetProperty("messages");
        Assert.Equal("Translate from English to Chinese (Simplified).", messages[0].GetProperty("content").GetString());
        Assert.Equal("Hello world", messages[1].GetProperty("content").GetString());

        Assert.Equal(["你好", "，世界"], chunks.Where(c => !string.IsNullOrEmpty(c.DeltaText)).Select(c => c.DeltaText).ToList());
        Assert.True(chunks[^1].IsComplete);
        Assert.Null(chunks[^1].ErrorMessage);
    }

    [Fact]
    public async Task TranslateAsync_NonStreamingJsonFallback_ReturnsTranslatedText()
    {
        var jsonResponse = """
        {
          "choices": [
            {
              "message": {
                "role": "assistant",
                "content": "Translated fallback result"
              }
            }
          ]
        }
        """;

        var factory = new StubProxyHttpClientFactory((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json"),
            }));

        var provider = new OpenAiCompatibleTranslationProvider(factory, _protector);
        var config = new ProviderInstanceConfig
        {
            Id = "local-ollama",
            DisplayName = "Local Ollama",
            Kind = ProviderKind.OpenAiCompatible,
            BaseUrl = "http://localhost:11434/v1/chat/completions",
            Model = "qwen2.5",
            EncryptedApiKey = string.Empty,
        };

        var result = await provider.TranslateAsync(
            new TranslationRequest("测试", LanguageCode.ChineseSimplified, LanguageCode.English),
            config);

        Assert.True(result.IsSuccess);
        Assert.Equal("Translated fallback result", result.TranslatedText);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task TranslateAsync_Http401Unauthorized_ReturnsActionableErrorMessage()
    {
        var errorBody = """{"error":{"message":"Incorrect API key provided"}}""";
        var factory = new StubProxyHttpClientFactory((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(errorBody, Encoding.UTF8, "application/json"),
            }));

        var provider = new OpenAiCompatibleTranslationProvider(factory, _protector);
        var config = new ProviderInstanceConfig
        {
            Id = "openai-bad",
            DisplayName = "OpenAI",
            Kind = ProviderKind.OpenAiCompatible,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o-mini",
            EncryptedApiKey = _protector.Protect("sk-bad"),
        };

        var result = await provider.TranslateAsync(
            new TranslationRequest("Hello", LanguageCode.English, LanguageCode.ChineseSimplified),
            config);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("401", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("Incorrect API key provided", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranslateAsync_MissingBaseUrlOrModel_ReturnsValidationErrorWithoutHttpCall()
    {
        var called = false;
        var factory = new StubProxyHttpClientFactory((_, _) =>
        {
            called = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var provider = new OpenAiCompatibleTranslationProvider(factory, _protector);
        var missingUrl = new ProviderInstanceConfig
        {
            Kind = ProviderKind.OpenAiCompatible,
            BaseUrl = "",
            Model = "gpt-4o-mini",
        };

        var res1 = await provider.TranslateAsync(
            new TranslationRequest("Hi", LanguageCode.English, LanguageCode.ChineseSimplified),
            missingUrl);

        Assert.False(res1.IsSuccess);
        Assert.False(called);
        Assert.Contains("Base URL", res1.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var missingModel = new ProviderInstanceConfig
        {
            Kind = ProviderKind.OpenAiCompatible,
            BaseUrl = "https://api.openai.com/v1",
            Model = "",
        };

        var res2 = await provider.TranslateAsync(
            new TranslationRequest("Hi", LanguageCode.English, LanguageCode.ChineseSimplified),
            missingModel);

        Assert.False(res2.IsSuccess);
        Assert.False(called);
        Assert.Contains("Model", res2.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestConnectionAsync_ReportsSuccessAndLatency()
    {
        var factory = new StubProxyHttpClientFactory((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}\n\ndata: [DONE]\n\n",
                    Encoding.UTF8,
                    "text/event-stream"),
            }));

        var provider = new OpenAiCompatibleTranslationProvider(factory, _protector);
        var config = new ProviderInstanceConfig
        {
            Kind = ProviderKind.OpenAiCompatible,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o-mini",
            EncryptedApiKey = _protector.Protect("sk-ok"),
        };

        var health = await provider.TestConnectionAsync(config);

        Assert.True(health.IsSuccess);
        Assert.True(health.Latency >= TimeSpan.Zero);
    }

    private sealed class StubProxyHttpClientFactory(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : IProxyHttpClientFactory
    {
        public HttpClient CreateClient(ProxyConfig? overrideConfig = null, TimeSpan? timeout = null) =>
            new(new DelegateHandler(responder))
            {
                Timeout = timeout ?? TimeSpan.FromSeconds(30),
            };

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
