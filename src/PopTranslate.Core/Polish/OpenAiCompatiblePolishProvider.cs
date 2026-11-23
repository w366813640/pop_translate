using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;

namespace PopTranslate.Core.Polish;

public sealed class OpenAiCompatiblePolishProvider : IPolishProvider
{
    private readonly IProxyHttpClientFactory _httpClientFactory;
    private readonly ISecretProtector _secretProtector;

    public OpenAiCompatiblePolishProvider(
        IProxyHttpClientFactory httpClientFactory,
        ISecretProtector secretProtector)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _secretProtector = secretProtector ?? throw new ArgumentNullException(nameof(secretProtector));
    }

    public ProviderKind Kind => ProviderKind.OpenAiCompatible;

    public bool SupportsStreaming => true;

    public async IAsyncEnumerable<TranslationChunk> PolishStreamAsync(
        PolishRequest request,
        ProviderInstanceConfig config,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(config);

        var validationError = ValidateConfig(config, out var endpointUri);
        if (validationError is not null)
        {
            yield return new TranslationChunk(string.Empty, IsComplete: true, ErrorMessage: validationError);
            yield break;
        }

        var detectedLanguage = request.PreferredLanguage is { IsAuto: false } preferred
            ? preferred
            : (LanguageCode?)null;

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            yield return new TranslationChunk(string.Empty, IsComplete: true, DetectedSourceLanguage: detectedLanguage);
            yield break;
        }

        var timeoutSeconds = Math.Clamp(config.TimeoutSeconds <= 0 ? 30 : config.TimeoutSeconds, 5, 300);
        using var httpClient = _httpClientFactory.CreateClient(timeout: TimeSpan.FromSeconds(timeoutSeconds));
        using var httpRequest = BuildHttpRequest(request, config, endpointUri!, stream: true);

        HttpResponseMessage? response = null;
        string? transportError = null;
        try
        {
            response = await httpClient
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            transportError = $"Request timed out after {timeoutSeconds}s.";
        }
        catch (HttpRequestException ex)
        {
            transportError = $"Network error contacting {endpointUri!.Host}: {ex.Message}";
        }

        if (transportError is not null || response is null)
        {
            yield return new TranslationChunk(
                string.Empty,
                IsComplete: true,
                DetectedSourceLanguage: detectedLanguage,
                ErrorMessage: transportError ?? "No response received from provider.");
            yield break;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                var message = FormatHttpError(response.StatusCode, body);
                yield return new TranslationChunk(string.Empty, IsComplete: true, DetectedSourceLanguage: detectedLanguage, ErrorMessage: message);
                yield break;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                var jsonBody = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                if (TryExtractNonStreamingContent(jsonBody, out var content, out var jsonError))
                {
                    if (!string.IsNullOrEmpty(content))
                    {
                        yield return new TranslationChunk(content, IsComplete: false, DetectedSourceLanguage: detectedLanguage);
                    }

                    yield return new TranslationChunk(string.Empty, IsComplete: true, DetectedSourceLanguage: detectedLanguage);
                }
                else
                {
                    yield return new TranslationChunk(
                        string.Empty,
                        IsComplete: true,
                        DetectedSourceLanguage: detectedLanguage,
                        ErrorMessage: jsonError ?? "Empty response from OpenAI-compatible API.");
                }

                yield break;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var firstNonEmptyHandled = false;
            var fallbackJsonBuffer = new StringBuilder();
            var isFallbackJson = false;
            string? streamError = null;

            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    streamError = $"Stream timed out after {timeoutSeconds}s.";
                    break;
                }
                catch (IOException ex)
                {
                    streamError = $"Connection interrupted while reading stream: {ex.Message}";
                    break;
                }

                if (line is null)
                {
                    break;
                }

                if (isFallbackJson)
                {
                    fallbackJsonBuffer.AppendLine(line);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line) || line.StartsWith(':'))
                {
                    continue;
                }

                if (!firstNonEmptyHandled)
                {
                    firstNonEmptyHandled = true;
                    var trimmedFirst = line.TrimStart();
                    if (trimmedFirst.StartsWith('{'))
                    {
                        isFallbackJson = true;
                        fallbackJsonBuffer.AppendLine(line);
                        continue;
                    }
                }

                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var data = line["data:".Length..].Trim();
                if (data.Length == 0)
                {
                    continue;
                }

                if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
                {
                    break;
                }

                if (!TryParseSseFrame(data, out var delta, out var frameError))
                {
                    if (frameError is not null)
                    {
                        streamError = frameError;
                        break;
                    }

                    continue;
                }

                if (!string.IsNullOrEmpty(delta))
                {
                    yield return new TranslationChunk(delta, IsComplete: false, DetectedSourceLanguage: detectedLanguage);
                }
            }

            if (streamError is not null)
            {
                yield return new TranslationChunk(string.Empty, IsComplete: true, DetectedSourceLanguage: detectedLanguage, ErrorMessage: streamError);
                yield break;
            }

            if (isFallbackJson)
            {
                if (TryExtractNonStreamingContent(fallbackJsonBuffer.ToString(), out var fallbackText, out var fallbackError))
                {
                    if (!string.IsNullOrEmpty(fallbackText))
                    {
                        yield return new TranslationChunk(fallbackText, IsComplete: false, DetectedSourceLanguage: detectedLanguage);
                    }

                    yield return new TranslationChunk(string.Empty, IsComplete: true, DetectedSourceLanguage: detectedLanguage);
                }
                else
                {
                    yield return new TranslationChunk(
                        string.Empty,
                        IsComplete: true,
                        DetectedSourceLanguage: detectedLanguage,
                        ErrorMessage: fallbackError ?? "Failed to parse JSON response.");
                }

                yield break;
            }

            yield return new TranslationChunk(string.Empty, IsComplete: true, DetectedSourceLanguage: detectedLanguage);
        }
    }

    public async Task<TranslationResult> PolishAsync(
        PolishRequest request,
        ProviderInstanceConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(config);

        var sw = Stopwatch.StartNew();
        var sb = new StringBuilder();
        LanguageCode? detected = request.PreferredLanguage is { IsAuto: false } preferred ? preferred : null;
        string? error = null;

        await foreach (var chunk in PolishStreamAsync(request, config, cancellationToken).ConfigureAwait(false))
        {
            if (chunk.DetectedSourceLanguage is { IsAuto: false } chunkDetected)
            {
                detected = chunkDetected;
            }

            if (!string.IsNullOrEmpty(chunk.DeltaText))
            {
                sb.Append(chunk.DeltaText);
            }

            if (!string.IsNullOrWhiteSpace(chunk.ErrorMessage))
            {
                error = chunk.ErrorMessage;
                break;
            }
        }

        sw.Stop();
        var targetLanguage = request.PreferredLanguage is { IsAuto: false } lang
            ? lang
            : (detected ?? LanguageCode.Auto);

        return new TranslationResult(
            ProviderInstanceId: config.Id,
            ProviderDisplayName: config.DisplayName,
            ProviderKind: Kind,
            TranslatedText: sb.ToString(),
            DetectedSourceLanguage: detected,
            TargetLanguage: targetLanguage,
            IsSuccess: error is null,
            ErrorMessage: error,
            Elapsed: sw.Elapsed);
    }

    private static string BuildChatCompletionsUrl(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return trimmed + "/chat/completions";
    }

    private static string? ValidateConfig(ProviderInstanceConfig config, out Uri? endpointUri)
    {
        endpointUri = null;
        if (string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            return "Base URL is required for OpenAI-compatible provider.";
        }

        var urlString = BuildChatCompletionsUrl(config.BaseUrl);
        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return $"Invalid Base URL '{config.BaseUrl}'. Expected an http:// or https:// URL.";
        }

        if (string.IsNullOrWhiteSpace(config.Model))
        {
            return "Model name is required for OpenAI-compatible provider.";
        }

        endpointUri = uri;
        return null;
    }

    private HttpRequestMessage BuildHttpRequest(
        PolishRequest request,
        ProviderInstanceConfig config,
        Uri endpointUri,
        bool stream)
    {
        var systemPrompt = PolishPromptTemplates.ResolveSystemPrompt(
            request.Style,
            request.CustomPrompt,
            request.PreferredLanguage);

        var payload = new
        {
            model = config.Model.Trim(),
            temperature = config.Temperature,
            stream,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = request.Text },
            },
        };

        var json = JsonSerializer.Serialize(payload);
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpointUri)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var apiKey = _secretProtector.Unprotect(config.EncryptedApiKey).Trim();
        if (!string.IsNullOrEmpty(apiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return httpRequest;
    }

    private static bool TryParseSseFrame(string jsonPayload, out string delta, out string? errorMessage)
    {
        delta = string.Empty;
        errorMessage = null;

        try
        {
            using var doc = JsonDocument.Parse(jsonPayload);
            var root = doc.RootElement;

            if (TryExtractApiErrorMessage(root, out var apiError))
            {
                errorMessage = apiError;
                return false;
            }

            if (root.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0)
            {
                var firstChoice = choices[0];
                if (firstChoice.TryGetProperty("delta", out var deltaObj) &&
                    deltaObj.ValueKind == JsonValueKind.Object &&
                    deltaObj.TryGetProperty("content", out var contentProp) &&
                    contentProp.ValueKind == JsonValueKind.String)
                {
                    delta = contentProp.GetString() ?? string.Empty;
                    return true;
                }

                if (firstChoice.TryGetProperty("message", out var messageObj) &&
                    messageObj.ValueKind == JsonValueKind.Object &&
                    messageObj.TryGetProperty("content", out var msgContent) &&
                    msgContent.ValueKind == JsonValueKind.String)
                {
                    delta = msgContent.GetString() ?? string.Empty;
                    return true;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryExtractNonStreamingContent(string jsonBody, out string content, out string? errorMessage)
    {
        content = string.Empty;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(jsonBody))
        {
            errorMessage = "Empty response from OpenAI-compatible API.";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            var root = doc.RootElement;

            if (TryExtractApiErrorMessage(root, out var apiError))
            {
                errorMessage = apiError;
                return false;
            }

            if (root.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("message", out var msg) &&
                    msg.ValueKind == JsonValueKind.Object &&
                    msg.TryGetProperty("content", out var msgContent) &&
                    msgContent.ValueKind == JsonValueKind.String)
                {
                    content = msgContent.GetString() ?? string.Empty;
                    return true;
                }

                if (first.TryGetProperty("delta", out var delta) &&
                    delta.ValueKind == JsonValueKind.Object &&
                    delta.TryGetProperty("content", out var deltaContent) &&
                    deltaContent.ValueKind == JsonValueKind.String)
                {
                    content = deltaContent.GetString() ?? string.Empty;
                    return true;
                }

                if (first.TryGetProperty("text", out var textProp) &&
                    textProp.ValueKind == JsonValueKind.String)
                {
                    content = textProp.GetString() ?? string.Empty;
                    return true;
                }
            }

            errorMessage = "Unexpected OpenAI-compatible response format: missing choices content.";
            return false;
        }
        catch (JsonException ex)
        {
            errorMessage = $"Failed to parse OpenAI-compatible response JSON: {ex.Message}";
            return false;
        }
    }

    private static bool TryExtractApiErrorMessage(JsonElement root, out string? message)
    {
        message = null;
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("error", out var errorProp))
        {
            if (errorProp.ValueKind == JsonValueKind.String)
            {
                message = errorProp.GetString();
                return !string.IsNullOrWhiteSpace(message);
            }

            if (errorProp.ValueKind == JsonValueKind.Object &&
                errorProp.TryGetProperty("message", out var msgProp) &&
                msgProp.ValueKind == JsonValueKind.String)
            {
                message = msgProp.GetString();
                return !string.IsNullOrWhiteSpace(message);
            }
        }

        return false;
    }

    private static string FormatHttpError(HttpStatusCode statusCode, string body)
    {
        string? detail = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                TryExtractApiErrorMessage(doc.RootElement, out detail);
            }
            catch (JsonException)
            {
                detail = body.Length > 200 ? body[..200] : body.Trim();
            }
        }

        var code = (int)statusCode;
        var summary = statusCode switch
        {
            HttpStatusCode.Unauthorized => $"Authentication failed (HTTP {code}): check your API key.",
            HttpStatusCode.Forbidden => $"Access denied (HTTP {code}): verify API key permissions.",
            HttpStatusCode.NotFound => $"Endpoint or model not found (HTTP {code}): verify Base URL and Model name.",
            HttpStatusCode.TooManyRequests => $"Rate limit or quota exceeded (HTTP {code}).",
            >= HttpStatusCode.InternalServerError => $"Upstream provider server error (HTTP {code}).",
            _ => $"HTTP error {code} ({statusCode}).",
        };

        return string.IsNullOrWhiteSpace(detail) ? summary : $"{summary} {detail}";
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }
}
