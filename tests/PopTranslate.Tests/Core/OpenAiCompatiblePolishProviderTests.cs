using System.Net;
using System.Text;
using System.Text.Json;
using PopTranslate.Core.Configuration;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using PopTranslate.Core.Polish;
using Xunit;

namespace PopTranslate.Tests.Core;

public sealed class OpenAiCompatiblePolishProviderTests
{
    private readonly DpapiSecretProtector _protector = new();

    [Fact]
    public void PolishPromptTemplates_AllSixStyles_ProduceDistinctTargetedPrompts()
    {
        var general = PolishPromptTemplates.ResolveSystemPrompt(PolishStyle.General, customPrompt: null);
        var academic = PolishPromptTemplates.ResolveSystemPrompt(PolishStyle.Academic, customPrompt: null);
        var business = PolishPromptTemplates.ResolveSystemPrompt(PolishStyle.Business, customPrompt: null);
        var casual = PolishPromptTemplates.ResolveSystemPrompt(PolishStyle.Casual, customPrompt: null);
        var concise = PolishPromptTemplates.ResolveSystemPrompt(PolishStyle.Concise, customPrompt: null);
        var custom = PolishPromptTemplates.ResolveSystemPrompt(PolishStyle.Custom, customPrompt: "  Rewrite like a technical spec.  ");
        var customFallback = PolishPromptTemplates.ResolveSystemPrompt(PolishStyle.Custom, customPrompt: "   ");

        var allSix = new[] { general, academic, business, casual, concise, custom };
        Assert.Equal(6, allSix.Distinct(StringComparer.Ordinal).Count());

        foreach (var preset in new[] { general, academic, business, casual, concise })
        {
            Assert.Contains("original language", preset, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ONLY", preset, StringComparison.Ordinal);
        }

        Assert.Contains("academic", academic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("business", business, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("casual", casual, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("concise", concise, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Rewrite like a technical spec.", custom, StringComparison.Ordinal);
        Assert.Contains("original language", custom, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(PolishConfig.DefaultCustomPrompt, customFallback, StringComparison.Ordinal);

        // Preferred language suffix appended to presets and custom prompts
        var academicEnglish = PolishPromptTemplates.ResolveSystemPrompt(PolishStyle.Academic, null, LanguageCode.English);
        Assert.EndsWith("Keep the output strictly in English.", academicEnglish, StringComparison.Ordinal);

        var customWithLang = PolishPromptTemplates.ResolveSystemPrompt(PolishStyle.Custom, "Custom only", LanguageCode.English);
        Assert.StartsWith("Custom only", customWithLang, StringComparison.Ordinal);
        Assert.Contains("English", customWithLang, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PolishStreamAsync_SseStreaming_EmitsIncrementalChunksAndSendsDecryptedBearerAndStylePrompt()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;

        var ssePayload =
            ": keep-alive\n\n" +
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"This report \"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"is concise and clear.\"}}]}\n\n" +
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

        var provider = new OpenAiCompatiblePolishProvider(factory, _protector);
        Assert.Equal(ProviderKind.OpenAiCompatible, provider.Kind);
        Assert.True(provider.SupportsStreaming);

        var config = new ProviderInstanceConfig
        {
            Id = "llm-polish-1",
            DisplayName = "Polish LLM",
            Kind = ProviderKind.OpenAiCompatible,
            BaseUrl = "https://api.example.com/v1/",
            Model = "gpt-4o-mini",
            EncryptedApiKey = _protector.Protect("sk-polish-secret-789"),
            Temperature = 0.25,
        };

        var request = new PolishRequest(
            Text: "This report is kinda long and not very clear.",
            Style: PolishStyle.Concise,
            PreferredLanguage: LanguageCode.English);

        var chunks = new List<TranslationChunk>();
        await foreach (var chunk in provider.PolishStreamAsync(request, config))
        {
            chunks.Add(chunk);
        }

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("https://api.example.com/v1/chat/completions", capturedRequest.RequestUri!.ToString());
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization?.Scheme);
        Assert.Equal("sk-polish-secret-789", capturedRequest.Headers.Authorization?.Parameter);

        Assert.NotNull(capturedBody);
        using var bodyDoc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("gpt-4o-mini", bodyDoc.RootElement.GetProperty("model").GetString());
        Assert.Equal(0.25, bodyDoc.RootElement.GetProperty("temperature").GetDouble(), precision: 4);
        Assert.True(bodyDoc.RootElement.GetProperty("stream").GetBoolean());

        var messages = bodyDoc.RootElement.GetProperty("messages");
        var sentSystem = messages[0].GetProperty("content").GetString() ?? string.Empty;
        Assert.StartsWith(
            PolishPromptTemplates.ResolveSystemPrompt(PolishStyle.Concise, null, LanguageCode.English),
            sentSystem,
            StringComparison.Ordinal);
        Assert.Contains("<source_text>", sentSystem, StringComparison.Ordinal);
        Assert.Equal($"<source_text>\n{request.Text}\n</source_text>", messages[1].GetProperty("content").GetString());

        Assert.Equal(
            ["This report ", "is concise and clear."],
            chunks.Where(c => !string.IsNullOrEmpty(c.DeltaText)).Select(c => c.DeltaText).ToList());
        Assert.True(chunks[^1].IsComplete);
        Assert.Null(chunks[^1].ErrorMessage);
    }

    [Fact]
    public async Task PolishAsync_CustomStyleWithCustomPrompt_SendsCustomPromptAndHandlesJsonFallback()
    {
        string? capturedBody = null;
        var jsonResponse = """
        {
          "choices": [
            {
              "message": {
                "role": "assistant",
                "content": "尊敬的客户，感谢您的来信。"
              }
            }
          ]
        }
        """;

        var factory = new StubProxyHttpClientFactory(async (req, ct) =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json"),
            };
        });

        var provider = new OpenAiCompatiblePolishProvider(factory, _protector);
        var config = new ProviderInstanceConfig
        {
            Id = "local-llm",
            DisplayName = "Local LLM",
            Kind = ProviderKind.OpenAiCompatible,
            BaseUrl = "http://localhost:11434/v1/chat/completions",
            Model = "qwen2.5",
            EncryptedApiKey = string.Empty,
        };

        var result = await provider.PolishAsync(
            new PolishRequest(
                Text: "客户你好，谢谢发邮件。",
                Style: PolishStyle.Custom,
                CustomPrompt: "请改写成正式礼貌的中文客服回复，只输出改写结果。"),
            config);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("尊敬的客户，感谢您的来信。", result.TranslatedText);
        Assert.Null(result.ErrorMessage);

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        var sentCustomSystem = doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString() ?? string.Empty;
        Assert.StartsWith("请改写成正式礼貌的中文客服回复，只输出改写结果。", sentCustomSystem, StringComparison.Ordinal);
        Assert.Contains("<source_text>", sentCustomSystem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "401", "Invalid API key")]
    [InlineData(HttpStatusCode.TooManyRequests, "429", "Rate limit hit")]
    [InlineData(HttpStatusCode.InternalServerError, "500", "Backend crashed")]
    public async Task PolishAsync_HttpErrors_ReturnFormattedErrorMessages(
        HttpStatusCode statusCode,
        string expectedCode,
        string upstreamDetail)
    {
        var errorBody = JsonSerializer.Serialize(new { error = new { message = upstreamDetail } });
        var factory = new StubProxyHttpClientFactory((_, _) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(errorBody, Encoding.UTF8, "application/json"),
            }));

        var provider = new OpenAiCompatiblePolishProvider(factory, _protector);
        var config = new ProviderInstanceConfig
        {
            Id = "llm-err",
            DisplayName = "OpenAI",
            Kind = ProviderKind.OpenAiCompatible,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o-mini",
            EncryptedApiKey = _protector.Protect("sk-test"),
        };

        var result = await provider.PolishAsync(new PolishRequest("Polish me"), config);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains(expectedCode, result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains(upstreamDetail, result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PolishAsync_InvalidBaseUrlOrMissingModel_FailsWithoutHttpRequest()
    {
        var called = false;
        var factory = new StubProxyHttpClientFactory((_, _) =>
        {
            called = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var provider = new OpenAiCompatiblePolishProvider(factory, _protector);

        var badUrlRes = await provider.PolishAsync(
            new PolishRequest("Test"),
            new ProviderInstanceConfig
            {
                Kind = ProviderKind.OpenAiCompatible,
                BaseUrl = "ftp://invalid-scheme.local",
                Model = "gpt-4o-mini",
            });
        Assert.False(badUrlRes.IsSuccess);
        Assert.False(called);
        Assert.Contains("Base URL", badUrlRes.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var missingModelRes = await provider.PolishAsync(
            new PolishRequest("Test"),
            new ProviderInstanceConfig
            {
                Kind = ProviderKind.OpenAiCompatible,
                BaseUrl = "https://api.openai.com/v1",
                Model = "   ",
            });
        Assert.False(missingModelRes.IsSuccess);
        Assert.False(called);
        Assert.Contains("Model", missingModelRes.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PolishStreamAsync_Cancellation_CancelsImmediately()
    {
        var factory = new StubProxyHttpClientFactory(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var provider = new OpenAiCompatiblePolishProvider(factory, _protector);
        var config = new ProviderInstanceConfig
        {
            Kind = ProviderKind.OpenAiCompatible,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o-mini",
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in provider.PolishStreamAsync(new PolishRequest("Cancel me"), config, cts.Token))
            {
            }
        });
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
