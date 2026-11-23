using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;

namespace PopTranslate.Core.Translation;

public sealed class DeepLTranslationProvider : ITranslationProvider
{
    private const string FreeEndpoint = "https://api-free.deepl.com/v2/translate";
    private const string ProEndpoint = "https://api.deepl.com/v2/translate";

    private readonly IProxyHttpClientFactory _httpClientFactory;
    private readonly ISecretProtector _secretProtector;

    public DeepLTranslationProvider(
        IProxyHttpClientFactory httpClientFactory,
        ISecretProtector secretProtector)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _secretProtector = secretProtector ?? throw new ArgumentNullException(nameof(secretProtector));
    }

    public ProviderKind Kind => ProviderKind.DeepL;

    public bool SupportsStreaming => false;

    public async IAsyncEnumerable<TranslationChunk> TranslateStreamAsync(
        TranslationRequest request,
        ProviderInstanceConfig config,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await TranslateAsync(request, config, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            yield return new TranslationChunk(
                string.Empty,
                IsComplete: true,
                DetectedSourceLanguage: result.DetectedSourceLanguage,
                ErrorMessage: result.ErrorMessage);
            yield break;
        }

        if (!string.IsNullOrEmpty(result.TranslatedText))
        {
            yield return new TranslationChunk(
                result.TranslatedText,
                IsComplete: false,
                DetectedSourceLanguage: result.DetectedSourceLanguage);
        }

        yield return new TranslationChunk(
            string.Empty,
            IsComplete: true,
            DetectedSourceLanguage: result.DetectedSourceLanguage);
    }

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        ProviderInstanceConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(config);

        var sw = Stopwatch.StartNew();

        var apiKey = _secretProtector.Unprotect(config.EncryptedApiKey).Trim();
        if (string.IsNullOrEmpty(apiKey))
        {
            sw.Stop();
            return Failure(config, request, "API key is required for DeepL provider.", sw.Elapsed);
        }

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            sw.Stop();
            return new TranslationResult(
                config.Id,
                config.DisplayName,
                Kind,
                string.Empty,
                request.SourceLanguage.IsAuto ? null : request.SourceLanguage,
                request.TargetLanguage,
                IsSuccess: true,
                ErrorMessage: null,
                Elapsed: sw.Elapsed);
        }

        var endpointUrl = ResolveEndpointUrl(config, apiKey);
        if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out var endpointUri) ||
            (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
        {
            sw.Stop();
            return Failure(config, request, $"Invalid DeepL endpoint URL '{endpointUrl}'.", sw.Elapsed);
        }

        var targetLang = MapTargetLanguage(request.TargetLanguage.IsAuto ? LanguageCode.ChineseSimplified : request.TargetLanguage);
        var payloadDict = new Dictionary<string, object>
        {
            ["text"] = new[] { request.Text },
            ["target_lang"] = targetLang,
        };

        if (!request.SourceLanguage.IsAuto)
        {
            payloadDict["source_lang"] = MapSourceLanguage(request.SourceLanguage);
        }

        var timeoutSeconds = Math.Clamp(config.TimeoutSeconds <= 0 ? 20 : config.TimeoutSeconds, 5, 180);
        using var httpClient = _httpClientFactory.CreateClient(timeout: TimeSpan.FromSeconds(timeoutSeconds));
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpointUri)
        {
            Content = new StringContent(JsonSerializer.Serialize(payloadDict), Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", apiKey);

        try
        {
            using var response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                sw.Stop();
                return Failure(config, request, FormatHttpError(response.StatusCode, body), sw.Elapsed);
            }

            if (!TryParseResponse(body, out var translatedText, out var detectedSource, out var parseError))
            {
                sw.Stop();
                return Failure(config, request, parseError ?? "Failed to parse DeepL response.", sw.Elapsed);
            }

            sw.Stop();
            return new TranslationResult(
                ProviderInstanceId: config.Id,
                ProviderDisplayName: config.DisplayName,
                ProviderKind: Kind,
                TranslatedText: translatedText,
                DetectedSourceLanguage: detectedSource,
                TargetLanguage: request.TargetLanguage,
                IsSuccess: true,
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
            return Failure(config, request, $"DeepL request timed out after {timeoutSeconds}s.", sw.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            return Failure(config, request, $"Network error contacting DeepL: {ex.Message}", sw.Elapsed);
        }
    }

    public async Task<ProviderHealthCheckResult> TestConnectionAsync(
        ProviderInstanceConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var sw = Stopwatch.StartNew();
        var result = await TranslateAsync(
            new TranslationRequest("Hello", LanguageCode.English, LanguageCode.ChineseSimplified, ApplyTextPreprocessing: false),
            config,
            cancellationToken).ConfigureAwait(false);
        sw.Stop();

        if (!result.IsSuccess)
        {
            return new ProviderHealthCheckResult(false, result.ErrorMessage ?? "DeepL connection test failed.", sw.Elapsed);
        }

        return new ProviderHealthCheckResult(true, $"Connected ({result.TranslatedText.Trim()}).", sw.Elapsed);
    }

    public static string ResolveEndpointUrl(ProviderInstanceConfig config, string apiKey)
    {
        if (!string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            var custom = config.BaseUrl.Trim().TrimEnd('/');
            if (custom.EndsWith("/v2/translate", StringComparison.OrdinalIgnoreCase))
            {
                return custom;
            }

            if (custom.EndsWith("/v2", StringComparison.OrdinalIgnoreCase))
            {
                return custom + "/translate";
            }

            return custom + "/v2/translate";
        }

        var useFree = config.DeepLTier == DeepLEndpointTier.Free ||
                      (config.DeepLTier == DeepLEndpointTier.Auto && apiKey.EndsWith(":fx", StringComparison.OrdinalIgnoreCase));

        return useFree ? FreeEndpoint : ProEndpoint;
    }

    public static string MapTargetLanguage(LanguageCode code) => code.Value switch
    {
        "zh-Hans" => "ZH-HANS",
        "zh-Hant" => "ZH-HANT",
        "en" => "EN-US",
        "ja" => "JA",
        "ko" => "KO",
        "de" => "DE",
        "fr" => "FR",
        "es" => "ES",
        "ru" => "RU",
        "it" => "IT",
        "pt" => "PT-PT",
        "ar" => "AR",
        _ => code.Value.ToUpperInvariant(),
    };

    public static string MapSourceLanguage(LanguageCode code) => code.Value switch
    {
        "zh-Hans" or "zh-Hant" => "ZH",
        "en" => "EN",
        "ja" => "JA",
        "ko" => "KO",
        "de" => "DE",
        "fr" => "FR",
        "es" => "ES",
        "ru" => "RU",
        "it" => "IT",
        "pt" => "PT",
        "ar" => "AR",
        _ => code.Value.ToUpperInvariant(),
    };

    private static bool TryParseResponse(
        string body,
        out string translatedText,
        out LanguageCode? detectedSource,
        out string? errorMessage)
    {
        translatedText = string.Empty;
        detectedSource = null;
        errorMessage = null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("translations", out var translations) ||
                translations.ValueKind != JsonValueKind.Array ||
                translations.GetArrayLength() == 0)
            {
                errorMessage = "Unexpected DeepL response format: missing translations array.";
                return false;
            }

            var first = translations[0];
            if (first.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String)
            {
                translatedText = textProp.GetString() ?? string.Empty;
            }

            if (first.TryGetProperty("detected_source_language", out var langProp) && langProp.ValueKind == JsonValueKind.String)
            {
                var raw = langProp.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    detectedSource = LanguageCode.Parse(raw);
                }
            }

            return true;
        }
        catch (JsonException ex)
        {
            errorMessage = $"Failed to parse DeepL JSON response: {ex.Message}";
            return false;
        }
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
                    doc.RootElement.TryGetProperty("message", out var msgProp) &&
                    msgProp.ValueKind == JsonValueKind.String)
                {
                    detail = msgProp.GetString();
                }
            }
            catch (JsonException)
            {
                detail = body.Length > 200 ? body[..200] : body.Trim();
            }
        }

        var code = (int)statusCode;
        var summary = code switch
        {
            403 => "DeepL authentication failed (HTTP 403): check your DeepL API key and Free/Pro tier.",
            429 => "DeepL rate limit exceeded (HTTP 429): too many requests.",
            456 => "DeepL character quota exceeded (HTTP 456).",
            >= 500 => $"DeepL server error (HTTP {code}).",
            _ => $"DeepL HTTP error {code} ({statusCode}).",
        };

        return string.IsNullOrWhiteSpace(detail) ? summary : $"{summary} {detail}";
    }

    private TranslationResult Failure(
        ProviderInstanceConfig config,
        TranslationRequest request,
        string errorMessage,
        TimeSpan elapsed) =>
        new(
            ProviderInstanceId: config.Id,
            ProviderDisplayName: config.DisplayName,
            ProviderKind: Kind,
            TranslatedText: string.Empty,
            DetectedSourceLanguage: null,
            TargetLanguage: request.TargetLanguage,
            IsSuccess: false,
            ErrorMessage: errorMessage,
            Elapsed: elapsed);
}
