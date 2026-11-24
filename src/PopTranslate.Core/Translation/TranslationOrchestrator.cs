using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;

namespace PopTranslate.Core.Translation;

public sealed class TranslationOrchestrator : ITranslationOrchestrator
{
    private static readonly TimeSpan WorkerTeardownTimeout = TimeSpan.FromSeconds(1);

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

    public IAsyncEnumerable<ProviderStreamUpdate> TranslateParallelAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prepared = PrepareExecution(request);
        return TranslateParallelCoreAsync(prepared, cancellationToken);
    }

    public async Task<TranslationBatchResult> TranslateAllAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var prepared = PrepareExecution(request);
        var finalByInstance = new Dictionary<string, ProviderStreamUpdate>(StringComparer.Ordinal);
        var effectiveSource = prepared.EffectiveRequest.SourceLanguage.IsAuto
            ? prepared.Routing.DetectedSourceLanguage
            : prepared.EffectiveRequest.SourceLanguage;

        await foreach (var update in TranslateParallelCoreAsync(prepared, cancellationToken).ConfigureAwait(false))
        {
            finalByInstance[update.ProviderInstanceId] = update;
            if (prepared.EffectiveRequest.SourceLanguage.IsAuto &&
                update.DetectedSourceLanguage is { IsAuto: false } detected)
            {
                effectiveSource = detected;
            }
        }

        var results = new List<TranslationResult>(prepared.SelectedProviders.Count);
        foreach (var instance in prepared.SelectedProviders)
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
            PreprocessedText: prepared.ProcessedText,
            EffectiveSourceLanguage: effectiveSource,
            EffectiveTargetLanguage: prepared.Routing.TargetLanguage,
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

    private async IAsyncEnumerable<ProviderStreamUpdate> TranslateParallelCoreAsync(
        PreparedExecution prepared,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (prepared.SelectedProviders.Count == 0)
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

        var initialDetected = prepared.Routing.DetectedSourceLanguage.IsAuto
            ? (LanguageCode?)null
            : prepared.Routing.DetectedSourceLanguage;

        // Emit immediate Pending updates in configured SortOrder before spawning worker tasks.
        foreach (var instance in prepared.SelectedProviders)
        {
            channel.Writer.TryWrite(new ProviderStreamUpdate(
                ProviderInstanceId: instance.Id,
                ProviderDisplayName: instance.DisplayName,
                ProviderKind: instance.Kind,
                State: ProviderStreamState.Pending,
                DeltaText: string.Empty,
                AccumulatedText: string.Empty,
                DetectedSourceLanguage: initialDetected,
                TargetLanguage: prepared.Routing.TargetLanguage,
                ErrorMessage: null,
                Elapsed: TimeSpan.Zero));
        }

        var workerTasks = new List<Task>(prepared.SelectedProviders.Count);
        foreach (var instance in prepared.SelectedProviders)
        {
            var capturedInstance = instance;
            workerTasks.Add(Task.Run(
                () => ExecuteProviderAsync(
                    capturedInstance,
                    prepared.EffectiveRequest,
                    initialDetected,
                    prepared.Routing.TargetLanguage,
                    channel.Writer,
                    linkedToken),
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
                await allWorkersTask.WaitAsync(WorkerTeardownTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Bounded teardown: do not hang caller disposal if a socket refuses cancellation.
            }
            catch
            {
                // Provider exceptions are already captured and emitted as Failed updates.
            }
        }
    }

    private PreparedExecution PrepareExecution(TranslationRequest request)
    {
        var settings = _settingsStore.Current;
        string processedText;

        if (!request.ApplyTextPreprocessing || request.IsFromOcr)
        {
            processedText = request.Text ?? string.Empty;
        }
        else
        {
            var shouldMerge = settings.MergePdfLineBreaks;
            processedText = _textPreprocessor.Preprocess(request.Text, shouldMerge, request.IsFromOcr);
            if (!settings.EnableCodeIdentifierSplit && IsSingleWhitespaceFreeToken(request.Text))
            {
                processedText = request.Text!.Trim();
            }
        }

        var routing = _languageRouter.Resolve(
            processedText,
            request.SourceLanguage,
            request.TargetLanguage,
            settings.PrimaryTargetLanguage,
            settings.SecondaryTargetLanguage);

        // Pass SourceLanguage = Auto through to providers so DeepL, Google/Bing, and LLMs
        // execute native server-side source language detection rather than local fallback overrides.
        var effectiveRequest = request with
        {
            Text = processedText,
            SourceLanguage = request.SourceLanguage,
            TargetLanguage = routing.TargetLanguage,
        };

        List<ProviderInstanceConfig> selected;
        var providersList = settings.Providers ?? [];
        if (request.ProviderInstanceIds is { Count: > 0 } requestedIds)
        {
            var idSet = new HashSet<string>(requestedIds, StringComparer.Ordinal);
            selected = providersList
                .Where(p => p is not null && idSet.Contains(p.Id))
                .OrderBy(p => p.SortOrder)
                .Select(p => p.Clone())
                .ToList();
        }
        else
        {
            selected = providersList
                .Where(p => p is not null && p.Enabled && p.Role == ProviderRole.Translate)
                .OrderBy(p => p.SortOrder)
                .Select(p => p.Clone())
                .ToList();
        }

        return new PreparedExecution(processedText, routing, effectiveRequest, selected);
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

            if (provider is IReasoningTranslationProvider reasoningProvider)
            {
                await foreach (var item in reasoningProvider
                    .TranslateReasoningStreamAsync(effectiveRequest, instance, cancellationToken)
                    .ConfigureAwait(false))
                {
                    var chunk = item.Chunk;
                    if (chunk.DetectedSourceLanguage is { IsAuto: false } chunkDetected)
                    {
                        detected = chunkDetected;
                    }

                    if (!string.IsNullOrWhiteSpace(chunk.ErrorMessage))
                    {
                        chunkError = chunk.ErrorMessage;
                        break;
                    }

                    if (item.IsReasoning && accumulated.Length == 0)
                    {
                        writer.TryWrite(new ProviderStreamUpdate(
                            instance.Id,
                            instance.DisplayName,
                            instance.Kind,
                            ProviderStreamState.Streaming,
                            string.Empty,
                            string.Empty,
                            detected,
                            targetLanguage,
                            null,
                            sw.Elapsed,
                            IsReasoning: true));
                        continue;
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
                            sw.Elapsed,
                            IsReasoning: false));
                    }

                    if (chunk.IsComplete)
                    {
                        break;
                    }
                }
            }
            else
            {
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

    private static bool IsSingleWhitespaceFreeToken(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var ch in text.AsSpan().Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                return false;
            }
        }

        return true;
    }

    private readonly record struct PreparedExecution(
        string ProcessedText,
        LanguageRoutingDecision Routing,
        TranslationRequest EffectiveRequest,
        List<ProviderInstanceConfig> SelectedProviders);
}
