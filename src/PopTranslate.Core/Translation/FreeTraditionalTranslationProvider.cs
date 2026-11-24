using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;

namespace PopTranslate.Core.Translation;

public sealed class FreeTraditionalTranslationProvider : ITranslationProvider
{
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private const string GoogleClients5TranslateEndpoint = "https://clients5.google.com/translate_a/t";
    private const string GoogleApisTranslateEndpoint = "https://translate.googleapis.com/translate_a/t";
    private const string GoogleApisSingleEndpoint = "https://translate.googleapis.com/translate_a/single";
    private const string BingAuthEndpoint = "https://edge.microsoft.com/translate/auth";
    private const string BingTranslateEndpoint = "https://api-edge.cognitive.microsofttranslator.com/translate";
    private static readonly TimeSpan BingTokenTtl = TimeSpan.FromMinutes(8);

    private static readonly GoogleEndpointSpec[] DefaultGoogleEndpoints =
    [
        new(GoogleClients5TranslateEndpoint, Client: "dict-chrome-ex", IncludeDtParam: false),
        new(GoogleApisTranslateEndpoint, Client: "dict-chrome-ex", IncludeDtParam: false),
        new(GoogleApisSingleEndpoint, Client: "dict-chrome-ex", IncludeDtParam: true),
        new(GoogleApisSingleEndpoint, Client: "gtx", IncludeDtParam: true),
    ];

    private readonly IProxyHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _bingTokenLock = new(1, 1);
    private string? _cachedBingToken;
    private DateTimeOffset _cachedBingTokenExpiry = DateTimeOffset.MinValue;

    public FreeTraditionalTranslationProvider(IProxyHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    public ProviderKind Kind => ProviderKind.FreeTraditional;

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

        var timeoutSeconds = Math.Clamp(config.TimeoutSeconds <= 0 ? 15 : config.TimeoutSeconds, 5, 120);
        var totalTimeout = TimeSpan.FromSeconds(timeoutSeconds);

        HttpClient? httpClient = null;
        try
        {
            httpClient = _httpClientFactory.CreateClient(timeout: totalTimeout);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TranslationResult(
                ProviderInstanceId: config.Id,
                ProviderDisplayName: config.DisplayName,
                ProviderKind: Kind,
                TranslatedText: string.Empty,
                DetectedSourceLanguage: null,
                TargetLanguage: request.TargetLanguage,
                IsSuccess: false,
                ErrorMessage: $"Failed to initialize HTTP client: {ex.Message}",
                Elapsed: sw.Elapsed);
        }

        using (httpClient)
        {
            EngineOutcome outcome = config.FreeEngine switch
            {
                FreeTranslatorEngine.Google => await TranslateWithGoogleAsync(httpClient, request, config, totalTimeout, cancellationToken).ConfigureAwait(false),
                FreeTranslatorEngine.Bing => await TranslateWithBingAsync(httpClient, request, cancellationToken).ConfigureAwait(false),
                _ => await TranslateWithAutoFallbackAsync(httpClient, request, config, totalTimeout, cancellationToken).ConfigureAwait(false),
            };

            sw.Stop();
            return new TranslationResult(
                ProviderInstanceId: config.Id,
                ProviderDisplayName: config.DisplayName,
                ProviderKind: Kind,
                TranslatedText: outcome.Text,
                DetectedSourceLanguage: outcome.DetectedLanguage,
                TargetLanguage: request.TargetLanguage,
                IsSuccess: outcome.IsSuccess,
                ErrorMessage: outcome.ErrorMessage,
                Elapsed: sw.Elapsed);
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
            return new ProviderHealthCheckResult(false, result.ErrorMessage ?? "Free translation test failed.", sw.Elapsed);
        }

        return new ProviderHealthCheckResult(true, $"Connected ({result.TranslatedText.Trim()}).", sw.Elapsed);
    }

    public static string MapGoogleLanguageCode(LanguageCode code)
    {
        if (code.IsAuto)
        {
            return "auto";
        }

        return code.Value switch
        {
            "zh-Hans" => "zh-CN",
            "zh-Hant" => "zh-TW",
            _ => code.Value,
        };
    }

    public static string MapBingLanguageCode(LanguageCode code)
    {
        if (code.IsAuto)
        {
            return string.Empty;
        }

        return code.Value switch
        {
            "zh-Hans" => "zh-Hans",
            "zh-Hant" => "zh-Hant",
            _ => code.Value,
        };
    }

    private async Task<EngineOutcome> TranslateWithAutoFallbackAsync(
        HttpClient httpClient,
        TranslationRequest request,
        ProviderInstanceConfig config,
        TimeSpan totalTimeout,
        CancellationToken cancellationToken)
    {
        var google = await TranslateWithGoogleAsync(httpClient, request, config, totalTimeout, cancellationToken).ConfigureAwait(false);
        if (google.IsSuccess)
        {
            return google;
        }

        var bing = await TranslateWithBingAsync(httpClient, request, cancellationToken).ConfigureAwait(false);
        if (bing.IsSuccess)
        {
            return bing;
        }

        return EngineOutcome.Failure($"Google failed ({google.ErrorMessage}); Bing fallback failed ({bing.ErrorMessage}).");
    }

    private static async Task<EngineOutcome> TranslateWithGoogleAsync(
        HttpClient httpClient,
        TranslationRequest request,
        ProviderInstanceConfig config,
        TimeSpan totalTimeout,
        CancellationToken cancellationToken)
    {
        if (!TryResolveGoogleEndpoints(config.BaseUrl, out var endpoints, out var validationError))
        {
            return EngineOutcome.Failure(validationError!);
        }

        var sl = MapGoogleLanguageCode(request.SourceLanguage);
        var tl = MapGoogleLanguageCode(request.TargetLanguage.IsAuto ? LanguageCode.ChineseSimplified : request.TargetLanguage);
        var deadline = Stopwatch.StartNew();
        string? lastError = null;

        for (var i = 0; i < endpoints.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remaining = totalTimeout - deadline.Elapsed;
            if (remaining <= TimeSpan.FromMilliseconds(250))
            {
                return EngineOutcome.Failure(lastError ?? "Google Translate request timed out.");
            }

            var isLast = i == endpoints.Count - 1;
            var attemptTimeout = isLast
                ? remaining
                : TimeSpan.FromSeconds(Math.Min(remaining.TotalSeconds, Math.Clamp(totalTimeout.TotalSeconds / 2.0, 4.0, 8.0)));

            var outcome = await TryTranslateWithGoogleEndpointAsync(
                httpClient,
                endpoints[i],
                sl,
                tl,
                request,
                attemptTimeout,
                cancellationToken).ConfigureAwait(false);

            if (outcome.IsSuccess)
            {
                return outcome;
            }

            lastError = outcome.ErrorMessage;
        }

        return EngineOutcome.Failure(lastError ?? "Google Translate failed across all endpoints.");
    }

    private static async Task<EngineOutcome> TryTranslateWithGoogleEndpointAsync(
        HttpClient httpClient,
        GoogleEndpointSpec endpoint,
        string sourceLang,
        string targetLang,
        TranslationRequest request,
        TimeSpan attemptTimeout,
        CancellationToken cancellationToken)
    {
        var queryBuilder = new StringBuilder();
        queryBuilder.Append("client=").Append(Uri.EscapeDataString(endpoint.Client));
        queryBuilder.Append("&sl=").Append(Uri.EscapeDataString(sourceLang));
        queryBuilder.Append("&tl=").Append(Uri.EscapeDataString(targetLang));
        if (endpoint.IncludeDtParam)
        {
            queryBuilder.Append("&dt=t");
        }

        var separator = endpoint.BaseEndpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var useGet = request.Text.Length <= 1500 && Encoding.UTF8.GetByteCount(request.Text) <= 1800;
        var requestUrl = useGet
            ? $"{endpoint.BaseEndpoint}{separator}{queryBuilder}&q={Uri.EscapeDataString(request.Text)}"
            : $"{endpoint.BaseEndpoint}{separator}{queryBuilder}";

        using var httpRequest = useGet
            ? new HttpRequestMessage(HttpMethod.Get, requestUrl)
            : new HttpRequestMessage(HttpMethod.Post, requestUrl)
            {
                Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("q", request.Text)]),
            };

        httpRequest.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        httpRequest.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");

        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCts.CancelAfter(attemptTimeout);

        try
        {
            using var response = await httpClient.SendAsync(httpRequest, attemptCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return EngineOutcome.Failure($"Google Translate HTTP {(int)response.StatusCode}");
            }

            var body = await response.Content.ReadAsStringAsync(attemptCts.Token).ConfigureAwait(false);
            return ParseGoogleResponse(body, request.SourceLanguage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return EngineOutcome.Failure("Google Translate request timed out.");
        }
        catch (HttpRequestException ex)
        {
            return EngineOutcome.Failure($"Google Translate network error: {ex.Message}");
        }
    }

    private static bool TryResolveGoogleEndpoints(
        string? customBaseUrl,
        out IReadOnlyList<GoogleEndpointSpec> endpoints,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(customBaseUrl))
        {
            endpoints = DefaultGoogleEndpoints;
            error = null;
            return true;
        }

        var trimmed = customBaseUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            endpoints = [];
            error = $"Invalid Google Base URL '{customBaseUrl}'. Expected an http:// or https:// URL.";
            return false;
        }

        var normalized = trimmed.TrimEnd('/');
        if (uri.AbsolutePath.Contains("/translate_a/t", StringComparison.OrdinalIgnoreCase))
        {
            endpoints = [new GoogleEndpointSpec(normalized, Client: "dict-chrome-ex", IncludeDtParam: false)];
        }
        else if (uri.AbsolutePath.Contains("/translate_a/single", StringComparison.OrdinalIgnoreCase))
        {
            endpoints =
            [
                new GoogleEndpointSpec(normalized, Client: "dict-chrome-ex", IncludeDtParam: true),
                new GoogleEndpointSpec(normalized, Client: "gtx", IncludeDtParam: true),
            ];
        }
        else
        {
            endpoints =
            [
                new GoogleEndpointSpec($"{normalized}/translate_a/t", Client: "dict-chrome-ex", IncludeDtParam: false),
                new GoogleEndpointSpec($"{normalized}/translate_a/single", Client: "dict-chrome-ex", IncludeDtParam: true),
                new GoogleEndpointSpec($"{normalized}/translate_a/single", Client: "gtx", IncludeDtParam: true),
            ];
        }

        error = null;
        return true;
    }

    private static EngineOutcome ParseGoogleResponse(string json, LanguageCode fallbackSourceLanguage)
    {
        var fallbackDetected = fallbackSourceLanguage.IsAuto ? (LanguageCode?)null : fallbackSourceLanguage;
        if (string.IsNullOrWhiteSpace(json))
        {
            return EngineOutcome.Failure("Empty Google Translate response.");
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.String)
            {
                return EngineOutcome.Success(root.GetString() ?? string.Empty, fallbackDetected);
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                return ParseGoogleObjectResponse(root, fallbackDetected);
            }

            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                return EngineOutcome.Failure("Unexpected Google Translate response structure.");
            }

            var first = root[0];

            // Shape 1 (translate_a/t with explicit sl): ["translated text", ...]
            if (first.ValueKind == JsonValueKind.String)
            {
                var sb = new StringBuilder();
                foreach (var item in root.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        sb.Append(item.GetString());
                    }
                }

                return EngineOutcome.Success(sb.ToString(), fallbackDetected);
            }

            if (first.ValueKind != JsonValueKind.Array || first.GetArrayLength() == 0)
            {
                return EngineOutcome.Failure("Missing translation segments in Google Translate response.");
            }

            // Shape 2 (translate_a/t with sl=auto): [["translated text", "en"], ...]
            if (first[0].ValueKind == JsonValueKind.String)
            {
                var sb = new StringBuilder();
                LanguageCode? detected = fallbackDetected;
                foreach (var item in root.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Array &&
                        item.GetArrayLength() > 0 &&
                        item[0].ValueKind == JsonValueKind.String)
                    {
                        sb.Append(item[0].GetString());
                        if (detected is null &&
                            item.GetArrayLength() > 1 &&
                            item[1].ValueKind == JsonValueKind.String)
                        {
                            var rawLang = item[1].GetString();
                            if (!string.IsNullOrWhiteSpace(rawLang))
                            {
                                detected = LanguageCode.Parse(rawLang);
                            }
                        }
                    }
                }

                return EngineOutcome.Success(sb.ToString(), detected);
            }

            // Shape 3 (translate_a/single): [[["translated", "source", ...], ...], null, "en", ...]
            if (first[0].ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var segment in first.EnumerateArray())
                {
                    if (segment.ValueKind == JsonValueKind.Array &&
                        segment.GetArrayLength() > 0 &&
                        segment[0].ValueKind == JsonValueKind.String)
                    {
                        sb.Append(segment[0].GetString());
                    }
                }

                LanguageCode? detected = fallbackDetected;
                if (root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String)
                {
                    var rawLang = root[2].GetString();
                    if (!string.IsNullOrWhiteSpace(rawLang))
                    {
                        detected = LanguageCode.Parse(rawLang);
                    }
                }

                return EngineOutcome.Success(sb.ToString(), detected);
            }

            return EngineOutcome.Failure("Unexpected Google Translate segment format.");
        }
        catch (JsonException ex)
        {
            return EngineOutcome.Failure($"Failed to parse Google Translate JSON: {ex.Message}");
        }
    }

    private static EngineOutcome ParseGoogleObjectResponse(JsonElement root, LanguageCode? fallbackDetected)
    {
        if (!root.TryGetProperty("sentences", out var sentences) || sentences.ValueKind != JsonValueKind.Array)
        {
            return EngineOutcome.Failure("Missing sentences field in Google Translate response.");
        }

        var sb = new StringBuilder();
        foreach (var sentence in sentences.EnumerateArray())
        {
            if (sentence.ValueKind == JsonValueKind.Object &&
                sentence.TryGetProperty("trans", out var transProp) &&
                transProp.ValueKind == JsonValueKind.String)
            {
                sb.Append(transProp.GetString());
            }
        }

        LanguageCode? detected = fallbackDetected;
        if (root.TryGetProperty("src", out var srcProp) && srcProp.ValueKind == JsonValueKind.String)
        {
            var rawLang = srcProp.GetString();
            if (!string.IsNullOrWhiteSpace(rawLang))
            {
                detected = LanguageCode.Parse(rawLang);
            }
        }

        return EngineOutcome.Success(sb.ToString(), detected);
    }

    private async Task<EngineOutcome> TranslateWithBingAsync(
        HttpClient httpClient,
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var token = await GetBingAuthTokenAsync(httpClient, forceRefresh: false, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
            {
                return EngineOutcome.Failure("Failed to acquire Bing Translate auth token.");
            }

            var outcome = await SendBingTranslateRequestAsync(httpClient, request, token, cancellationToken).ConfigureAwait(false);
            if (outcome.NeedsAuthRefresh)
            {
                token = await GetBingAuthTokenAsync(httpClient, forceRefresh: true, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(token))
                {
                    return EngineOutcome.Failure("Failed to refresh Bing Translate auth token.");
                }

                outcome = await SendBingTranslateRequestAsync(httpClient, request, token, cancellationToken).ConfigureAwait(false);
            }

            return outcome;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return EngineOutcome.Failure("Bing Translate request timed out.");
        }
        catch (HttpRequestException ex)
        {
            return EngineOutcome.Failure($"Bing Translate network error: {ex.Message}");
        }
    }

    private async Task<string?> GetBingAuthTokenAsync(
        HttpClient httpClient,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (!forceRefresh &&
            !string.IsNullOrEmpty(_cachedBingToken) &&
            DateTimeOffset.UtcNow < _cachedBingTokenExpiry)
        {
            return _cachedBingToken;
        }

        await _bingTokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh &&
                !string.IsNullOrEmpty(_cachedBingToken) &&
                DateTimeOffset.UtcNow < _cachedBingTokenExpiry)
            {
                return _cachedBingToken;
            }

            using var tokenRequest = new HttpRequestMessage(HttpMethod.Get, BingAuthEndpoint);
            tokenRequest.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            using var response = await httpClient.SendAsync(tokenRequest, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var token = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
            if (string.IsNullOrEmpty(token))
            {
                return null;
            }

            _cachedBingToken = token;
            _cachedBingTokenExpiry = DateTimeOffset.UtcNow.Add(BingTokenTtl);
            return token;
        }
        finally
        {
            _bingTokenLock.Release();
        }
    }

    private static async Task<EngineOutcome> SendBingTranslateRequestAsync(
        HttpClient httpClient,
        TranslationRequest request,
        string token,
        CancellationToken cancellationToken)
    {
        var from = MapBingLanguageCode(request.SourceLanguage);
        var to = MapBingLanguageCode(request.TargetLanguage.IsAuto ? LanguageCode.ChineseSimplified : request.TargetLanguage);

        var query = string.IsNullOrEmpty(from)
            ? $"to={Uri.EscapeDataString(to)}&api-version=3.0"
            : $"from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}&api-version=3.0";

        var payloadJson = JsonSerializer.Serialize(new[] { new { Text = request.Text } });
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{BingTranslateEndpoint}?{query}")
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        httpRequest.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return new EngineOutcome(false, string.Empty, null, "Bing Translate HTTP 401", NeedsAuthRefresh: true);
        }

        if (!response.IsSuccessStatusCode)
        {
            return EngineOutcome.Failure($"Bing Translate HTTP {(int)response.StatusCode}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseBingResponse(json);
    }

    private static EngineOutcome ParseBingResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                return EngineOutcome.Failure("Unexpected Bing Translate response structure.");
            }

            var first = root[0];
            LanguageCode? detected = null;
            if (first.TryGetProperty("detectedLanguage", out var detectedObj) &&
                detectedObj.ValueKind == JsonValueKind.Object &&
                detectedObj.TryGetProperty("language", out var langProp) &&
                langProp.ValueKind == JsonValueKind.String)
            {
                var rawLang = langProp.GetString();
                if (!string.IsNullOrWhiteSpace(rawLang))
                {
                    detected = LanguageCode.Parse(rawLang);
                }
            }

            if (first.TryGetProperty("translations", out var translations) &&
                translations.ValueKind == JsonValueKind.Array &&
                translations.GetArrayLength() > 0 &&
                translations[0].TryGetProperty("text", out var textProp) &&
                textProp.ValueKind == JsonValueKind.String)
            {
                return EngineOutcome.Success(textProp.GetString() ?? string.Empty, detected);
            }

            return EngineOutcome.Failure("Missing translations field in Bing Translate response.");
        }
        catch (JsonException ex)
        {
            return EngineOutcome.Failure($"Failed to parse Bing Translate JSON: {ex.Message}");
        }
    }

    private readonly record struct GoogleEndpointSpec(
        string BaseEndpoint,
        string Client,
        bool IncludeDtParam);

    private readonly record struct EngineOutcome(
        bool IsSuccess,
        string Text,
        LanguageCode? DetectedLanguage,
        string? ErrorMessage,
        bool NeedsAuthRefresh = false)
    {
        public static EngineOutcome Success(string text, LanguageCode? detected) =>
            new(true, text, detected, null);

        public static EngineOutcome Failure(string message) =>
            new(false, string.Empty, null, message);
    }
}
