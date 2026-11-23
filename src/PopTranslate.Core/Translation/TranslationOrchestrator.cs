using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;

namespace PopTranslate.Core.Translation;

public sealed class TranslationOrchestrator : ITranslationOrchestrator
{
    private readonly ISettingsStore _settingsStore;
    private readonly ITextPreprocessor _textPreprocessor;
    private readonly ILanguageRouter _languageRouter;
    private readonly IReadOnlyDictionary<ProviderKind, ITranslationProvider> _providers;

    public TranslationOrchestrator(
        ISettingsStore settingsStore,
        ITextPreprocessor textPreprocessor,
        ILanguageRouter languageRouter,
        IEnumerable<ITranslationProvider> providers)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _textPreprocessor = textPreprocessor ?? throw new ArgumentNullException(nameof(textPreprocessor));
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

    public async IAsyncEnumerable<ProviderStreamUpdate> TranslateParallelAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (settings, processedText, routing, effectiveRequest, selectedProviders) = PrepareExecution(request);
        _ = settings;

        if (selectedProviders.Count == 0)
        {
            yield break;
        }

        var channel = Channel.CreateUnbounded<ProviderStreamUpdate>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var linkedToken = linkedCts.Token;

        var initialDetected = routing.DetectedSourceLanguage.IsAuto
            ? (LanguageCode?)null
            : routing.DetectedSourceLanguage;

        // Emit immediate Pending updates in configured SortOrder before spawning worker tasks.
        foreach (var instance in selectedProviders)
        {
            channel.Writer.TryWrite(new ProviderStreamUpdate(
                ProviderInstanceId: instance.Id,
                ProviderDisplayName: instance.DisplayName,
                ProviderKind: instance.Kind,
                State: ProviderStreamState.Pending,
                DeltaText: string.Empty,
                AccumulatedText: string.Empty,
                DetectedSourceLanguage: initialDetected,
                TargetLanguage: routing.TargetLanguage,
                ErrorMessage: null,
                Elapsed: TimeSpan.Zero));
        }

        var workerTasks = new List<Task>(selectedProviders.Count);
        foreach (var instance in selectedProviders)
        {
            var capturedInstance = instance;
            workerTasks.Add(Task.Run(
                () => ExecuteProviderAsync(capturedInstance, effectiveRequest, initialDetected, routing.TargetLanguage, channel.Writer, linkedToken),
                CancellationToken.None));
        }

        var allWorkersTask = Task.WhenAll(workerTasks);
        _ = allWorkersTask.ContinueWith(
            _ => channel.Writer.TryComplete(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            await foreach (var update in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }
        }
        finally
        {
            if (!allWorkersTask.IsCompleted)
            {
                try
                {
                    await linkedCts.CancelAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Ignore cancellation errors during teardown.
                }
            }

            try
            {
                await allWorkersTask.ConfigureAwait(false);
            }
            catch
            {
                // Provider exceptions are already captured and emitted as Failed updates.
            }
        }
    }

    public async Task<TranslationBatchResult> TranslateAllAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (_, processedText, routing, effectiveRequest, selectedProviders) = PrepareExecution(request);
        var finalByInstance = new Dictionary<string, ProviderStreamUpdate>(StringComparer.Ordinal);
        var effectiveSource = effectiveRequest.SourceLanguage;

        await foreach (var update in TranslateParallelAsync(request, cancellationToken).ConfigureAwait(false))
        {
            finalByInstance[update.ProviderInstanceId] = update;
            if (effectiveSource.IsAuto && update.DetectedSourceLanguage is { IsAuto: false } detected)
            {
                effectiveSource = detected;
            }
        }

        var results = new List<TranslationResult>(selectedProviders.Count);
        foreach (var instance in selectedProviders)
        {
            if (finalByInstance.TryGetValue(instance.Id, out var finalUpdate))
            {
                results.Add(new TranslationResult(
                    ProviderInstanceId: finalUpdate.ProviderInstanceId,
                    ProviderDisplayName: finalUpdate.ProviderDisplayName,
                    ProviderKind: finalUpdate.ProviderKind,
                    TranslatedText: finalUpdate.AccumulatedText,
                    DetectedSourceLanguage: finalUpdate.DetectedSourceLanguage,
                    TargetLanguage: finalUpdate.TargetLanguage,
                    IsSuccess: finalUpdate.State == ProviderStreamState.Completed,
                    ErrorMessage: finalUpdate.ErrorMessage,
                    Elapsed: finalUpdate.Elapsed));
            }
        }

        return new TranslationBatchResult(
            PreprocessedText: processedText,
            EffectiveSourceLanguage: effectiveSource,
            EffectiveTargetLanguage: routing.TargetLanguage,
            Results: results);
    }

    public async Task<ProviderHealthCheckResult> TestProviderAsync(
        ProviderInstanceConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!_providers.TryGetValue(config.Kind, out var provider))
        {
            return new ProviderHealthCheckResult(
                IsSuccess: false,
                Message: $"No translation provider is registered for kind '{config.Kind}'.",
                Latency: TimeSpan.Zero);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            return await provider.TestConnectionAsync(config, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProviderHealthCheckResult(false, ex.Message, sw.Elapsed);
        }
    }

    private (
        AppSettings Settings,
        string ProcessedText,
        LanguageRoutingDecision Routing,
        TranslationRequest EffectiveRequest,
        List<ProviderInstanceConfig> SelectedProviders)
        PrepareExecution(TranslationRequest request)
    {
        var settings = _settingsStore.Current;
        var shouldMerge = request.ApplyTextPreprocessing && settings.MergePdfLineBreaks;
        var processedText = request.ApplyTextPreprocessing
            ? _textPreprocessor.Preprocess(request.Text, shouldMerge, request.IsFromOcr)
            : request.Text ?? string.Empty;

        var routing = _languageRouter.Resolve(
            processedText,
            request.SourceLanguage,
            request.TargetLanguage,
            settings.PrimaryTargetLanguage,
            settings.SecondaryTargetLanguage);

        var effectiveSource = request.SourceLanguage.IsAuto
            ? routing.DetectedSourceLanguage
            : request.SourceLanguage;

        var effectiveRequest = request with
        {
            Text = processedText,
            SourceLanguage = effectiveSource,
            TargetLanguage = routing.TargetLanguage,
        };

        List<ProviderInstanceConfig> selected;
        if (request.ProviderInstanceIds is { Count: > 0 } requestedIds)
        {
            var idSet = new HashSet<string>(requestedIds, StringComparer.Ordinal);
            selected = settings.Providers
                .Where(p => idSet.Contains(p.Id))
                .OrderBy(p => p.SortOrder)
                .Select(p => p.Clone())
                .ToList();
        }
        else
        {
            selected = settings.Providers
                .Where(p => p.Enabled)
                .OrderBy(p => p.SortOrder)
                .Select(p => p.Clone())
                .ToList();
        }

        return (settings, processedText, routing, effectiveRequest, selected);
    }

    private async Task ExecuteProviderAsync(
        ProviderInstanceConfig instance,
        TranslationRequest effectiveRequest,
        LanguageCode? initialDetected,
        LanguageCode targetLanguage,
        ChannelWriter<ProviderStreamUpdate> writer,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var accumulated = new StringBuilder();
        var detected = initialDetected;

        if (!_providers.TryGetValue(instance.Kind, out var provider))
        {
            sw.Stop();
            writer.TryWrite(new ProviderStreamUpdate(
                instance.Id,
                instance.DisplayName,
                instance.Kind,
                ProviderStreamState.Failed,
                string.Empty,
                string.Empty,
                detected,
                targetLanguage,
                $"No translation provider is registered for kind '{instance.Kind}'.",
                sw.Elapsed));
            return;
        }

        try
        {
            string? chunkError = null;

            await foreach (var chunk in provider.TranslateStreamAsync(effectiveRequest, instance, cancellationToken).ConfigureAwait(false))
            {
                if (chunk.DetectedSourceLanguage is { IsAuto: false } chunkDetected)
                {
                    detected = chunkDetected;
                }

                if (!string.IsNullOrWhiteSpace(chunk.ErrorMessage))
                {
                    chunkError = chunk.ErrorMessage;
                    break;
                }

                if (!string.IsNullOrEmpty(chunk.DeltaText))
                {
                    accumulated.Append(chunk.DeltaText);
                    writer.TryWrite(new ProviderStreamUpdate(
                        instance.Id,
                        instance.DisplayName,
                        instance.Kind,
                        ProviderStreamState.Streaming,
                        chunk.DeltaText,
                        accumulated.ToString(),
                        detected,
                        targetLanguage,
                        null,
                        sw.Elapsed));
                }

                if (chunk.IsComplete)
                {
                    break;
                }
            }

            sw.Stop();

            if (chunkError is not null)
            {
                writer.TryWrite(new ProviderStreamUpdate(
                    instance.Id,
                    instance.DisplayName,
                    instance.Kind,
                    ProviderStreamState.Failed,
                    string.Empty,
                    accumulated.ToString(),
                    detected,
                    targetLanguage,
                    chunkError,
                    sw.Elapsed));
                return;
            }

            writer.TryWrite(new ProviderStreamUpdate(
                instance.Id,
                instance.DisplayName,
                instance.Kind,
                ProviderStreamState.Completed,
                string.Empty,
                accumulated.ToString(),
                detected,
                targetLanguage,
                null,
                sw.Elapsed));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            writer.TryWrite(new ProviderStreamUpdate(
                instance.Id,
                instance.DisplayName,
                instance.Kind,
                ProviderStreamState.Cancelled,
                string.Empty,
                accumulated.ToString(),
                detected,
                targetLanguage,
                "Translation cancelled.",
                sw.Elapsed));
        }
        catch (Exception ex)
        {
            sw.Stop();
            writer.TryWrite(new ProviderStreamUpdate(
                instance.Id,
                instance.DisplayName,
                instance.Kind,
                ProviderStreamState.Failed,
                string.Empty,
                accumulated.ToString(),
                detected,
                targetLanguage,
                ex.Message,
                sw.Elapsed));
        }
    }
}
