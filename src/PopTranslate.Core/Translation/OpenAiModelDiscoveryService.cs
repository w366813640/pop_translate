using System.Net.Http.Headers;
using System.Text.Json;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;

namespace PopTranslate.Core.Translation;

public sealed class OpenAiModelDiscoveryService : IModelDiscoveryService
{
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(15);

    private readonly IProxyHttpClientFactory _httpClientFactory;

    public OpenAiModelDiscoveryService(IProxyHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    public async Task<IReadOnlyList<string>> FetchModelsAsync(
        string baseUrl,
        string? plaintextApiKey,
        ProxyConfig? proxyOverride = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("Base URL is required to fetch models.", nameof(baseUrl));
        }

        var modelsUrl = BuildModelsEndpointUrl(baseUrl);
        if (!Uri.TryCreate(modelsUrl, UriKind.Absolute, out var endpointUri) ||
            (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"Invalid Base URL '{baseUrl}'. Expected an http:// or https:// URL.");
        }

        try
        {
            using var httpClient = _httpClientFactory.CreateClient(overrideConfig: proxyOverride, timeout: DiscoveryTimeout);
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, endpointUri);
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (!string.IsNullOrWhiteSpace(plaintextApiKey))
            {
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", plaintextApiKey.Trim());
            }

            using var response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            var body = await OpenAiStreamHelper
                .SafeReadBodyAsync(response, DiscoveryTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(OpenAiStreamHelper.FormatHttpError(response.StatusCode, body));
            }

            var stripModelsPrefix = endpointUri.Host.Contains("googleapis.com", StringComparison.OrdinalIgnoreCase);
            return ParseModelsResponse(body, stripModelsPrefix);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Model discovery timed out after {(int)DiscoveryTimeout.TotalSeconds}s.");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Network error contacting {endpointUri.Host}: {ex.Message}", ex);
        }
    }

    public static string BuildModelsEndpointUrl(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^"/chat/completions".Length];
        }

        if (trimmed.EndsWith("/models", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith("/api/tags", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return trimmed + "/models";
    }

    public static IReadOnlyList<string> ParseModelsResponse(string jsonBody, bool stripGeminiModelsPrefix = false)
    {
        if (string.IsNullOrWhiteSpace(jsonBody))
        {
            return [];
        }

        using var doc = JsonDocument.Parse(jsonBody);
        var root = doc.RootElement;
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (OpenAiStreamHelper.TryExtractApiErrorMessage(root, out var apiError))
            {
                throw new InvalidOperationException(apiError);
            }

            if (root.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
            {
                CollectModelEntries(dataArr, results, stripGeminiModelsPrefix);
            }
            else if (root.TryGetProperty("models", out var modelsArr) && modelsArr.ValueKind == JsonValueKind.Array)
            {
                CollectModelEntries(modelsArr, results, stripGeminiModelsPrefix: true);
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            CollectModelEntries(root, results, stripGeminiModelsPrefix);
        }

        var sorted = results.ToList();
        sorted.Sort(StringComparer.OrdinalIgnoreCase);
        return sorted;
    }

    private static void CollectModelEntries(JsonElement array, HashSet<string> sink, bool stripGeminiModelsPrefix)
    {
        foreach (var item in array.EnumerateArray())
        {
            string? raw = null;
            if (item.ValueKind == JsonValueKind.String)
            {
                raw = item.GetString();
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                if (item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                {
                    raw = idProp.GetString();
                }
                else if (item.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                {
                    raw = nameProp.GetString();
                }
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var cleaned = raw.Trim();
            if (stripGeminiModelsPrefix && cleaned.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned["models/".Length..];
            }

            if (!string.IsNullOrEmpty(cleaned))
            {
                sink.Add(cleaned);
            }
        }
    }
}
