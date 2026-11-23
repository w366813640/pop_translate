using System.Net;
using System.Text;
using System.Text.Json;
using PopTranslate.Core.Configuration;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Language;
using PopTranslate.Core.Models;
using PopTranslate.Core.Ocr;
using Xunit;

namespace PopTranslate.Tests.Core;

public sealed class VisionLlmOcrProviderTests
{
    private readonly DpapiSecretProtector _protector = new();
    private readonly LocalLanguageDetector _detector = new();

    [Fact]
    public async Task RecognizeAsync_SendsMultimodalBase64DataUriAndExtractsPlainText()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;

        var factory = new StubHttpFactory(async (req, ct) =>
        {
            capturedRequest = req;
            capturedBody = await req.Content!.ReadAsStringAsync(ct);

            var responseJson = """
            {
              "choices": [
                {
                  "message": {
                    "role": "assistant",
                    "content": "```\nHello from screenshot\nLine 2\n```"
                  }
                }
              ]
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        });

        var provider = new VisionLlmOcrProvider(factory, _protector, _detector);
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var image = new OcrImage(pngBytes, 100, 50, DpiScale: 1.5, MimeType: "image/png");
        var config = new OcrConfig
        {
            ActiveProvider = OcrProviderKind.VisionLlm,
            VisionBaseUrl = "https://api.example.com/v1",
            VisionModel = "gpt-4o-mini",
            EncryptedVisionApiKey = _protector.Protect("sk-vision-secret"),
            VisionPrompt = "Extract visible text only.",
        };

        var result = await provider.RecognizeAsync(image, config);

        Assert.True(result.IsSuccess);
        Assert.Equal(OcrProviderKind.VisionLlm, result.ProviderKind);
        Assert.Equal("Hello from screenshot\nLine 2", result.Text);
        Assert.Equal(LanguageCode.English, result.DetectedLanguage);
        Assert.Null(result.ErrorMessage);

        Assert.NotNull(capturedRequest);
        Assert.Equal("https://api.example.com/v1/chat/completions", capturedRequest!.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization?.Scheme);
        Assert.Equal("sk-vision-secret", capturedRequest.Headers.Authorization?.Parameter);

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("gpt-4o-mini", doc.RootElement.GetProperty("model").GetString());
        var content = doc.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("Extract visible text only.", content[0].GetProperty("text").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        var expectedDataUri = "data:image/png;base64," + Convert.ToBase64String(pngBytes);
        Assert.Equal(expectedDataUri, content[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public async Task RecognizeAsync_MissingUrlOrEmptyImage_ReturnsValidationFailure()
    {
        var factory = new StubHttpFactory((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        var provider = new VisionLlmOcrProvider(factory, _protector, _detector);

        var emptyImgResult = await provider.RecognizeAsync(
            new OcrImage([], 0, 0),
            new OcrConfig { VisionBaseUrl = "https://api.openai.com/v1", VisionModel = "gpt-4o-mini" });

        Assert.False(emptyImgResult.IsSuccess);
        Assert.Contains("empty", emptyImgResult.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var missingUrlResult = await provider.RecognizeAsync(
            new OcrImage([1, 2, 3], 10, 10),
            new OcrConfig { VisionBaseUrl = "", VisionModel = "gpt-4o-mini" });

        Assert.False(missingUrlResult.IsSuccess);
        Assert.Contains("Base URL", missingUrlResult.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestAsync_SendsVisionProbeAndReturnsHealthResult()
    {
        var factory = new StubHttpFactory((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"content":""}}]}""",
                    Encoding.UTF8,
                    "application/json"),
            }));

        var provider = new VisionLlmOcrProvider(factory, _protector, _detector);
        var health = await provider.TestAsync(new OcrConfig
        {
            VisionBaseUrl = "https://api.openai.com/v1",
            VisionModel = "gpt-4o-mini",
            EncryptedVisionApiKey = _protector.Protect("sk-test"),
        });

        Assert.True(health.IsSuccess);
        Assert.True(health.Latency >= TimeSpan.Zero);
    }

    private sealed class StubHttpFactory(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : IProxyHttpClientFactory
    {
        public HttpClient CreateClient(ProxyConfig? overrideConfig = null, TimeSpan? timeout = null) =>
            new(new DelegateHandler(responder)) { Timeout = timeout ?? TimeSpan.FromSeconds(30) };

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
