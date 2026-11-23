using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Language;
using PopTranslate.Core.Models;
using PopTranslate.Core.Translation;

namespace PopTranslate.Core.Ocr;

public sealed class VisionLlmOcrProvider : IOcrProvider
{
    private static readonly byte[] OneByOneTransparentPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    private readonly IProxyHttpClientFactory _httpClientFactory;
    private readonly ISecretProtector _secretProtector;
    private readonly ILanguageDetector _languageDetector;

    public VisionLlmOcrProvider(
        IProxyHttpClientFactory httpClientFactory,
        ISecretProtector secretProtector,
        ILanguageDetector? languageDetector = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _secretProtector = secretProtector ?? throw new ArgumentNullException(nameof(secretProtector));
        _languageDetector = languageDetector ?? new LocalLanguageDetector();
    }

    public OcrProviderKind Kind => OcrProviderKind.VisionLlm;

    public string DisplayName => "OpenAI-Compatible Vision LLM OCR";

    public async Task<OcrResult> RecognizeAsync(
        OcrImage image,
        OcrConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(config);

        var sw = Stopwatch.StartNew();

        if (image.ImageBytes is null || image.ImageBytes.Length == 0)
        {
            sw.Stop();
            return Failure("OCR image bytes cannot be empty.", sw.Elapsed);
        }

        if (string.IsNullOrWhiteSpace(config.VisionBaseUrl))
        {
            sw.Stop();
            return Failure("Vision LLM Base URL is required.", sw.Elapsed);
        }

        var endpointUrl = OpenAiCompatibleTranslationProvider.BuildChatCompletionsUrl(config.VisionBaseUrl);
        if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out var endpointUri) ||
            (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
        {
            sw.Stop();
            return Failure($"Invalid Vision LLM Base URL '{config.VisionBaseUrl}'.", sw.Elapsed);
        }

        if (string.IsNullOrWhiteSpace(config.VisionModel))
        {
            sw.Stop();
            return Failure("Vision LLM Model name is required.", sw.Elapsed);
        }

        var mimeType = string.IsNullOrWhiteSpace(image.MimeType) ? "image/png" : image.MimeType.Trim();
        var dataUri = $"data:{mimeType};base64,{Convert.ToBase64String(image.ImageBytes)}";
        var prompt = string.IsNullOrWhiteSpace(config.VisionPrompt)
            ? OcrConfig.DefaultVisionOcrPrompt
            : config.VisionPrompt;

        var payload = new
        {
            model = config.VisionModel.Trim(),
            temperature = 0.0,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = prompt },
                        new
                        {
                            type = "image_url",
                            image_url = new { url = dataUri },
                        },
                    },
                },
            },
        };

        var timeoutSeconds = Math.Clamp(config.TimeoutSeconds <= 0 ? 45 : config.TimeoutSeconds, 5, 300);
        using var httpClient = _httpClientFactory.CreateClient(timeout: TimeSpan.FromSeconds(timeoutSeconds));
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpointUri)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

        var apiKey = _secretProtector.Unprotect(config.EncryptedVisionApiKey).Trim();
        if (!string.IsNullOrEmpty(apiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        try
        {
            using var response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                sw.Stop();
                return Failure(FormatHttpError(response.StatusCode, body), sw.Elapsed);
            }

            if (!TryExtractContent(body, out var rawText, out var parseError))
            {
                sw.Stop();
                return Failure(parseError ?? "Failed to parse Vision LLM OCR response.", sw.Elapsed);
            }

            var cleaned = StripOuterMarkdownFence(rawText).Trim();
            LanguageCode? detected = null;
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                var detection = _languageDetector.Detect(cleaned);
                if (!detection.Language.IsAuto)
                {
                    detected = detection.Language;
                }
            }

            sw.Stop();
            return new OcrResult(
                IsSuccess: true,
                Text: cleaned,
                DetectedLanguage: detected,
                ProviderKind: Kind,
                ErrorMessage: null,
                Elapsed: sw.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return Failure($"Vision LLM OCR request timed out after {timeoutSeconds}s.", sw.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            return Failure($"Network error contacting Vision LLM OCR endpoint: {ex.Message}", sw.Elapsed);
        }
    }

    public Task<IReadOnlyList<OcrLanguagePackInfo>> GetAvailableLanguagesAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OcrLanguagePackInfo> languages =
        [
            new OcrLanguagePackInfo("multilingual", "Vision LLM (Multilingual)", LanguageCode.Auto),
        ];
        return Task.FromResult(languages);
    }

    public async Task<ProviderHealthCheckResult> TestAsync(
        OcrConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var sw = Stopwatch.StartNew();
        var probeImage = new OcrImage(OneByOneTransparentPng, 1, 1, DpiScale: 1.0, MimeType: "image/png");
        var probeConfig = config.Clone();
        probeConfig.VisionPrompt = "Reply with empty text if no text is visible.";

        var result = await RecognizeAsync(probeImage, probeConfig, cancellationToken).ConfigureAwait(false);
        sw.Stop();

        if (!result.IsSuccess)
        {
            return new ProviderHealthCheckResult(false, result.ErrorMessage ?? "Vision LLM OCR test failed.", sw.Elapsed);
        }

        return new ProviderHealthCheckResult(true, "Vision LLM OCR endpoint connected successfully.", sw.Elapsed);
    }

    private static bool TryExtractContent(string jsonBody, out string text, out string? errorMessage)
    {
        text = string.Empty;
        errorMessage = null;

        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("error", out var errorObj))
            {
                if (errorObj.ValueKind == JsonValueKind.String)
                {
                    errorMessage = errorObj.GetString();
                    return false;
                }

                if (errorObj.ValueKind == JsonValueKind.Object &&
                    errorObj.TryGetProperty("message", out var errMsg) &&
                    errMsg.ValueKind == JsonValueKind.String)
                {
                    errorMessage = errMsg.GetString();
                    return false;
                }
            }

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.Object &&
                message.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.String)
                {
                    text = content.GetString() ?? string.Empty;
                    return true;
                }

                if (content.ValueKind == JsonValueKind.Array)
                {
                    var sb = new StringBuilder();
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.ValueKind == JsonValueKind.Object &&
                            part.TryGetProperty("text", out var partText) &&
                            partText.ValueKind == JsonValueKind.String)
                        {
                            sb.Append(partText.GetString());
                        }
                    }

                    text = sb.ToString();
                    return true;
                }
            }

            errorMessage = "Unexpected Vision LLM OCR response: missing choices[0].message.content.";
            return false;
        }
        catch (JsonException ex)
        {
            errorMessage = $"Failed to parse Vision LLM OCR response JSON: {ex.Message}";
            return false;
        }
    }

    private static string StripOuterMarkdownFence(string raw)
    {
        var normalized = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (!normalized.StartsWith("```", StringComparison.Ordinal) ||
            !normalized.EndsWith("```", StringComparison.Ordinal) ||
            normalized.Length < 6)
        {
            return raw;
        }

        var firstNewLine = normalized.IndexOf('\n');
        var lastFence = normalized.LastIndexOf("```", StringComparison.Ordinal);
        if (firstNewLine < 0 || lastFence <= firstNewLine)
        {
            return raw;
        }

        return normalized[(firstNewLine + 1)..lastFence].Trim('\n');
    }

    private static string FormatHttpError(HttpStatusCode statusCode, string body)
    {
        string? detail = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("error", out var err) &&
                    err.ValueKind == JsonValueKind.Object &&
                    err.TryGetProperty("message", out var msg) &&
                    msg.ValueKind == JsonValueKind.String)
                {
                    detail = msg.GetString();
                }
            }
            catch (JsonException)
            {
                detail = body.Length > 200 ? body[..200] : body.Trim();
            }
        }

        var code = (int)statusCode;
        var summary = statusCode switch
        {
            HttpStatusCode.Unauthorized => $"Vision LLM authentication failed (HTTP {code}): check your API key.",
            HttpStatusCode.Forbidden => $"Vision LLM access denied (HTTP {code}).",
            HttpStatusCode.NotFound => $"Vision LLM endpoint or model not found (HTTP {code}).",
            HttpStatusCode.TooManyRequests => $"Vision LLM rate limit exceeded (HTTP {code}).",
            >= HttpStatusCode.InternalServerError => $"Vision LLM server error (HTTP {code}).",
            _ => $"Vision LLM HTTP error {code} ({statusCode}).",
        };

        return string.IsNullOrWhiteSpace(detail) ? summary : $"{summary} {detail}";
    }

    private OcrResult Failure(string errorMessage, TimeSpan elapsed) =>
        new(
            IsSuccess: false,
            Text: string.Empty,
            DetectedLanguage: null,
            ProviderKind: Kind,
            ErrorMessage: errorMessage,
            Elapsed: elapsed);
}
