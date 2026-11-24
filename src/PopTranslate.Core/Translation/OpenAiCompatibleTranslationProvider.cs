using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;

namespace PopTranslate.Core.Translation;

public sealed class OpenAiCompatibleTranslationProvider : ITranslationProvider, IReasoningTranslationProvider
{
    private readonly IProxyHttpClientFactory _httpClientFactory;
    private readonly ISecretProtector _secretProtector;
    private readonly ISettingsStore? _settingsStore;

    public OpenAiCompatibleTranslationProvider(
        IProxyHttpClientFactory httpClientFactory,
        ISecretProtector secretProtector,
        ISettingsStore? settingsStore = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _secretProtector = secretProtector ?? throw new ArgumentNullException(nameof(secretProtector));
        _settingsStore = settingsStore;
    }

    public ProviderKind Kind => ProviderKind.OpenAiCompatible;

    public bool SupportsStreaming => true;

    public async IAsyncEnumerable<TranslationChunk> TranslateStreamAsync(
        TranslationRequest request,
        ProviderInstanceConfig config,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in TranslateReasoningStreamAsync(request, config, cancellationToken).ConfigureAwait(false))
        {
            if (item.IsReasoning)
            {
                continue;
            }

            yield return item.Chunk;
        }
    }

    IAsyncEnumerable<ReasoningStreamChunk> IReasoningTranslationProvider.TranslateReasoningStreamAsync(
        TranslationRequest request,
        ProviderInstanceConfig config,
        CancellationToken cancellationToken) =>
        TranslateReasoningStreamAsync(request, config, cancellationToken);

    internal async IAsyncEnumerable<ReasoningStreamChunk> TranslateReasoningStreamAsync(
        TranslationRequest request,
        ProviderInstanceConfig config,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(config);

        var validationError = OpenAiStreamHelper.ValidateOpenAiConfig(config, out var endpointUri);
        if (validationError is not null)
        {
            yield return new ReasoningStreamChunk(
                new TranslationChunk(string.Empty, IsComplete: true, ErrorMessage: validationError));
            yield break;
        }

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            yield return new ReasoningStreamChunk(
                new TranslationChunk(string.Empty, IsComplete: true, DetectedSourceLanguage: request.SourceLanguage));
            yield break;
        }

        var settings = _settingsStore?.Current;
        var systemPrompt = ResolveSystemPrompt(config, request, settings);
        var wrappedUserText = OpenAiStreamHelper.WrapUserSourceText(request.Text);
        var detectedSource = request.SourceLanguage.IsAuto ? (LanguageCode?)null : request.SourceLanguage;

        await foreach (var item in OpenAiStreamHelper.ExecuteStreamingChatAsync(
            _httpClientFactory,
            _secretProtector,
            config,
            endpointUri!,
            systemPrompt,
            wrappedUserText,
            detectedSource,
            cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        ProviderInstanceConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(config);

        var sw = Stopwatch.StartNew();
        var sb = new StringBuilder();
        LanguageCode? detected = null;
        string? error = null;

        await foreach (var chunk in TranslateStreamAsync(request, config, cancellationToken).ConfigureAwait(false))
        {
            if (chunk.DetectedSourceLanguage is not null)
            {
                detected = chunk.DetectedSourceLanguage;
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
        return new TranslationResult(
            ProviderInstanceId: config.Id,
            ProviderDisplayName: config.DisplayName,
            ProviderKind: Kind,
            TranslatedText: sb.ToString(),
            DetectedSourceLanguage: detected,
            TargetLanguage: request.TargetLanguage,
            IsSuccess: error is null,
            ErrorMessage: error,
            Elapsed: sw.Elapsed);
    }

    public async Task<ProviderHealthCheckResult> TestConnectionAsync(
        ProviderInstanceConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var sw = Stopwatch.StartNew();
        var probeRequest = new TranslationRequest(
            Text: "Hello, world.",
            SourceLanguage: LanguageCode.English,
            TargetLanguage: LanguageCode.ChineseSimplified,
            ApplyTextPreprocessing: false);

        var previousSuppress = OpenAiStreamHelper.SuppressDictionaryAndGlossary.Value;
        OpenAiStreamHelper.SuppressDictionaryAndGlossary.Value = true;
        TranslationResult result;
        try
        {
            result = await TranslateAsync(probeRequest, config, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            OpenAiStreamHelper.SuppressDictionaryAndGlossary.Value = previousSuppress;
        }

        sw.Stop();

        if (!result.IsSuccess)
        {
            return new ProviderHealthCheckResult(false, result.ErrorMessage ?? "Connection test failed.", sw.Elapsed);
        }

        var preview = result.TranslatedText.Trim();
        var message = string.IsNullOrEmpty(preview)
            ? "Connected successfully."
            : $"Connected ({preview}).";
        return new ProviderHealthCheckResult(true, message, sw.Elapsed);
    }

    internal static string BuildChatCompletionsUrl(string baseUrl) =>
        OpenAiStreamHelper.BuildChatCompletionsUrl(baseUrl);

    internal static string ResolveSystemPrompt(
        ProviderInstanceConfig config,
        TranslationRequest request,
        AppSettings? settings = null)
    {
        var isDefaultPrompt = string.IsNullOrWhiteSpace(config.SystemPrompt) ||
                              string.Equals(
                                  config.SystemPrompt.Trim(),
                                  ProviderInstanceConfig.DefaultOpenAiSystemPrompt,
                                  StringComparison.Ordinal);

        var enableDictionary = settings?.EnableSingleWordDictionary ?? true;
        string basePrompt;

        if (isDefaultPrompt &&
            enableDictionary &&
            !OpenAiStreamHelper.SuppressDictionaryAndGlossary.Value &&
            WordLookupClassifier.IsDictionaryLookupCandidate(request.Text))
        {
            basePrompt = WordLookupClassifier.BuildDictionarySystemPrompt(
                request.SourceLanguage,
                request.TargetLanguage);
        }
        else
        {
            var template = string.IsNullOrWhiteSpace(config.SystemPrompt)
                ? ProviderInstanceConfig.DefaultOpenAiSystemPrompt
                : config.SystemPrompt;

            var sourceName = request.SourceLanguage.IsAuto
                ? "the detected source language"
                : request.SourceLanguage.DisplayName;
            var targetName = request.TargetLanguage.IsAuto
                ? LanguageCode.ChineseSimplified.DisplayName
                : request.TargetLanguage.DisplayName;

            basePrompt = template
                .Replace("{source}", sourceName, StringComparison.OrdinalIgnoreCase)
                .Replace("{target}", targetName, StringComparison.OrdinalIgnoreCase);
        }

        return OpenAiStreamHelper.AppendLiteralGuardAndGlossary(
            basePrompt,
            request.Text,
            settings?.Glossary);
    }
}
