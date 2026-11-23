using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;

namespace PopTranslate.Core.Translation;

public sealed class BackTranslationService : IBackTranslationService
{
    private readonly ISettingsStore _settingsStore;
    private readonly ILanguageRouter _languageRouter;
    private readonly IReadOnlyDictionary<ProviderKind, ITranslationProvider> _providers;

    public BackTranslationService(
        ISettingsStore settingsStore,
        ILanguageRouter languageRouter,
        IEnumerable<ITranslationProvider> providers)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _languageRouter = languageRouter ?? throw new ArgumentNullException(nameof(languageRouter));
        ArgumentNullException.ThrowIfNull(providers);

        var map = new Dictionary<ProviderKind, ITranslationProvider>();
        foreach (var provider in providers)
        {
            if (provider is not null)
            {
                map[provider.Kind] = provider;
            }
        }

        _providers = map;
    }

    public async IAsyncEnumerable<ProviderStreamUpdate> BackTranslateStreamAsync(
        BackTranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = _settingsStore.Current;
        var matchedInstance = settings.Providers
            .FirstOrDefault(p => string.Equals(p.Id, request.ProviderInstanceId, StringComparison.Ordinal))
            ?.Clone();

        var detectedSource = request.TranslatedLanguage.IsAuto
            ? (LanguageCode?)null
            : request.TranslatedLanguage;

        if (matchedInstance is null)
        {
            yield return new ProviderStreamUpdate(
                ProviderInstanceId: request.ProviderInstanceId,
                ProviderDisplayName: request.ProviderInstanceId,
                ProviderKind: ProviderKind.FreeTraditional,
                State: ProviderStreamState.Failed,
                DeltaText: string.Empty,
                AccumulatedText: string.Empty,
                DetectedSourceLanguage: detectedSource,
                TargetLanguage: request.BackTargetLanguage,
                ErrorMessage: $"Translation service '{request.ProviderInstanceId}' was not found.",
                Elapsed: TimeSpan.Zero);
            yield break;
        }

        var backTarget = request.BackTargetLanguage.IsAuto
            ? _languageRouter.Resolve(
                request.TranslatedText ?? string.Empty,
                request.TranslatedLanguage,
                LanguageCode.Auto,
                settings.PrimaryTargetLanguage,
                settings.SecondaryTargetLanguage).TargetLanguage
            : request.BackTargetLanguage;

        if (!_providers.TryGetValue(matchedInstance.Kind, out var provider))
        {
            yield return new ProviderStreamUpdate(
                ProviderInstanceId: matchedInstance.Id,
                ProviderDisplayName: matchedInstance.DisplayName,
                ProviderKind: matchedInstance.Kind,
                State: ProviderStreamState.Failed,
                DeltaText: string.Empty,
                AccumulatedText: string.Empty,
                DetectedSourceLanguage: detectedSource,
                TargetLanguage: backTarget,
                ErrorMessage: $"No translation provider is registered for kind '{matchedInstance.Kind}'.",
                Elapsed: TimeSpan.Zero);
            yield break;
        }

        if (string.IsNullOrWhiteSpace(request.TranslatedText))
        {
            yield return new ProviderStreamUpdate(
                ProviderInstanceId: matchedInstance.Id,
                ProviderDisplayName: matchedInstance.DisplayName,
                ProviderKind: matchedInstance.Kind,
                State: ProviderStreamState.Completed,
                DeltaText: string.Empty,
                AccumulatedText: string.Empty,
                DetectedSourceLanguage: detectedSource,
                TargetLanguage: backTarget,
                ErrorMessage: null,
                Elapsed: TimeSpan.Zero);
            yield break;
        }

        yield return new ProviderStreamUpdate(
            ProviderInstanceId: matchedInstance.Id,
            ProviderDisplayName: matchedInstance.DisplayName,
            ProviderKind: matchedInstance.Kind,
            State: ProviderStreamState.Pending,
            DeltaText: string.Empty,
            AccumulatedText: string.Empty,
            DetectedSourceLanguage: detectedSource,
            TargetLanguage: backTarget,
            ErrorMessage: null,
            Elapsed: TimeSpan.Zero);

        var sw = Stopwatch.StartNew();
        var accumulated = new StringBuilder();
        var reverseRequest = new TranslationRequest(
            Text: request.TranslatedText,
            SourceLanguage: request.TranslatedLanguage,
            TargetLanguage: backTarget,
            ApplyTextPreprocessing: false,
            IsFromOcr: false,
            ProviderInstanceIds: [matchedInstance.Id]);

        IAsyncEnumerator<TranslationChunk>? enumerator = null;
        string? terminalError = null;
        var cancelled = false;

        try
        {
            enumerator = provider.TranslateStreamAsync(reverseRequest, matchedInstance, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (OperationCanceledException)
        {
            terminalError = "Back-translation request timed out.";
        }
        catch (Exception ex)
        {
            terminalError = ex.Message;
        }

        if (enumerator is not null)
        {
            try
            {
                while (true)
                {
                    bool hasNext;
                    try
                    {
                        hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        terminalError = "Back-translation stream timed out.";
                        break;
                    }
                    catch (Exception ex)
                    {
                        terminalError = ex.Message;
                        break;
                    }

                    if (!hasNext)
                    {
                        break;
                    }

                    var chunk = enumerator.Current;
                    if (chunk.DetectedSourceLanguage is { IsAuto: false } chunkDetected)
                    {
                        detectedSource = chunkDetected;
                    }

                    if (!string.IsNullOrWhiteSpace(chunk.ErrorMessage))
                    {
                        terminalError = chunk.ErrorMessage;
                        break;
                    }

                    if (!string.IsNullOrEmpty(chunk.DeltaText))
                    {
                        accumulated.Append(chunk.DeltaText);
                        yield return new ProviderStreamUpdate(
                            ProviderInstanceId: matchedInstance.Id,
                            ProviderDisplayName: matchedInstance.DisplayName,
                            ProviderKind: matchedInstance.Kind,
                            State: ProviderStreamState.Streaming,
                            DeltaText: chunk.DeltaText,
                            AccumulatedText: accumulated.ToString(),
                            DetectedSourceLanguage: detectedSource,
                            TargetLanguage: backTarget,
                            ErrorMessage: null,
                            Elapsed: sw.Elapsed);
                    }

                    if (chunk.IsComplete)
                    {
                        break;
                    }
                }
            }
            finally
            {
                try
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Ignore enumerator teardown errors during cancellation/failure.
                }
            }
        }

        sw.Stop();

        if (cancelled)
        {
            yield return new ProviderStreamUpdate(
                ProviderInstanceId: matchedInstance.Id,
                ProviderDisplayName: matchedInstance.DisplayName,
                ProviderKind: matchedInstance.Kind,
                State: ProviderStreamState.Cancelled,
                DeltaText: string.Empty,
                AccumulatedText: accumulated.ToString(),
                DetectedSourceLanguage: detectedSource,
                TargetLanguage: backTarget,
                ErrorMessage: "Back-translation cancelled.",
                Elapsed: sw.Elapsed);
            yield break;
        }

        if (terminalError is not null)
        {
            yield return new ProviderStreamUpdate(
                ProviderInstanceId: matchedInstance.Id,
                ProviderDisplayName: matchedInstance.DisplayName,
                ProviderKind: matchedInstance.Kind,
                State: ProviderStreamState.Failed,
                DeltaText: string.Empty,
                AccumulatedText: accumulated.ToString(),
                DetectedSourceLanguage: detectedSource,
                TargetLanguage: backTarget,
                ErrorMessage: terminalError,
                Elapsed: sw.Elapsed);
            yield break;
        }

        yield return new ProviderStreamUpdate(
            ProviderInstanceId: matchedInstance.Id,
            ProviderDisplayName: matchedInstance.DisplayName,
            ProviderKind: matchedInstance.Kind,
            State: ProviderStreamState.Completed,
            DeltaText: string.Empty,
            AccumulatedText: accumulated.ToString(),
            DetectedSourceLanguage: detectedSource,
            TargetLanguage: backTarget,
            ErrorMessage: null,
            Elapsed: sw.Elapsed);
    }

    public async Task<TranslationResult> BackTranslateAsync(
        BackTranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ProviderStreamUpdate? finalUpdate = null;
        await foreach (var update in BackTranslateStreamAsync(request, cancellationToken).ConfigureAwait(false))
        {
            finalUpdate = update;
        }

        if (finalUpdate is null)
        {
            return new TranslationResult(
                ProviderInstanceId: request.ProviderInstanceId,
                ProviderDisplayName: request.ProviderInstanceId,
                ProviderKind: ProviderKind.FreeTraditional,
                TranslatedText: string.Empty,
                DetectedSourceLanguage: request.TranslatedLanguage.IsAuto ? null : request.TranslatedLanguage,
                TargetLanguage: request.BackTargetLanguage,
                IsSuccess: false,
                ErrorMessage: "Back-translation produced no updates.",
                Elapsed: TimeSpan.Zero);
        }

        return new TranslationResult(
            ProviderInstanceId: finalUpdate.ProviderInstanceId,
            ProviderDisplayName: finalUpdate.ProviderDisplayName,
            ProviderKind: finalUpdate.ProviderKind,
            TranslatedText: finalUpdate.AccumulatedText,
            DetectedSourceLanguage: finalUpdate.DetectedSourceLanguage,
            TargetLanguage: finalUpdate.TargetLanguage,
            IsSuccess: finalUpdate.State == ProviderStreamState.Completed,
            ErrorMessage: finalUpdate.ErrorMessage,
            Elapsed: finalUpdate.Elapsed);
    }
}
