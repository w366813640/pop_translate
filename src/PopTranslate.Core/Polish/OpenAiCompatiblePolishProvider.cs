using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using PopTranslate.Core.Translation;

namespace PopTranslate.Core.Polish;

public sealed class OpenAiCompatiblePolishProvider : IPolishProvider, IReasoningPolishProvider
{
    private readonly IProxyHttpClientFactory _httpClientFactory;
    private readonly ISecretProtector _secretProtector;
    private readonly ISettingsStore? _settingsStore;

    public OpenAiCompatiblePolishProvider(
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

    public async IAsyncEnumerable<TranslationChunk> PolishStreamAsync(
        PolishRequest request,
        ProviderInstanceConfig config,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in PolishReasoningStreamAsync(request, config, cancellationToken).ConfigureAwait(false))
        {
            if (item.IsReasoning)
            {
                continue;
            }

            yield return item.Chunk;
        }
    }

    IAsyncEnumerable<ReasoningStreamChunk> IReasoningPolishProvider.PolishReasoningStreamAsync(
        PolishRequest request,
        ProviderInstanceConfig config,
        CancellationToken cancellationToken) =>
        PolishReasoningStreamAsync(request, config, cancellationToken);

    internal async IAsyncEnumerable<ReasoningStreamChunk> PolishReasoningStreamAsync(
        PolishRequest request,
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

        var detectedLanguage = request.PreferredLanguage is { IsAuto: false } preferred
            ? preferred
            : (LanguageCode?)null;

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            yield return new ReasoningStreamChunk(
                new TranslationChunk(string.Empty, IsComplete: true, DetectedSourceLanguage: detectedLanguage));
            yield break;
        }

        var baseSystemPrompt = PolishPromptTemplates.ResolveSystemPrompt(
            request.Style,
            request.CustomPrompt,
            request.PreferredLanguage);

        var systemPrompt = OpenAiStreamHelper.AppendLiteralGuardAndGlossary(
            baseSystemPrompt,
            request.Text,
            _settingsStore?.Current.Glossary,
            includeGlossary: request.Style != PolishStyle.CodeNaming);

        var wrappedUserText = OpenAiStreamHelper.WrapUserSourceText(request.Text);

        await foreach (var item in OpenAiStreamHelper.ExecuteStreamingChatAsync(
            _httpClientFactory,
            _secretProtector,
            config,
            endpointUri!,
            systemPrompt,
            wrappedUserText,
            detectedLanguage,
            cancellationToken).ConfigureAwait(false))
        {
            yield return item;
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
}
