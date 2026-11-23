using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;

namespace PopTranslate.Core.Polish;

public sealed class PolishOrchestrator : IPolishOrchestrator
{
    private readonly ISettingsStore _settingsStore;
    private readonly ITextPreprocessor _textPreprocessor;
    private readonly ILanguageDetector _languageDetector;
    private readonly IReadOnlyDictionary<ProviderKind, IPolishProvider> _providers;

    public PolishOrchestrator(
        ISettingsStore settingsStore,
        ITextPreprocessor textPreprocessor,
        ILanguageDetector languageDetector,
        IEnumerable<IPolishProvider> providers)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _textPreprocessor = textPreprocessor ?? throw new ArgumentNullException(nameof(textPreprocessor));
        _languageDetector = languageDetector ?? throw new ArgumentNullException(nameof(languageDetector));
        ArgumentNullException.ThrowIfNull(providers);

        var map = new Dictionary<ProviderKind, IPolishProvider>();
        foreach (var provider in providers)
        {
            if (provider is not null)
            {
                map[provider.Kind] = provider;
            }
        }

        _providers = map;
    }

    public IReadOnlyList<ProviderInstanceConfig> GetEligibleProviders() =>
        _settingsStore.Current.Providers
            .Where(p => p.Enabled && _providers.ContainsKey(p.Kind))
            .OrderBy(p => p.SortOrder)
            .Select(p => p.Clone())
            .ToList();

    public async IAsyncEnumerable<ProviderStreamUpdate> PolishParallelAsync(
        PolishRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (effectiveRequest, initialDetected, targetLanguage, selectedProviders) = PrepareExecution(request);

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
                TargetLanguage: targetLanguage,
                ErrorMessage: null,
                Elapsed: TimeSpan.Zero));
        }

        var workerTasks = new List<Task>(selectedProviders.Count);
        foreach (var instance in selectedProviders)
        {
            var capturedInstance = instance;
            workerTasks.Add(Task.Run(
                () => ExecuteProviderAsync(capturedInstance, effectiveRequest, initialDetected, targetLanguage, channel.Writer, linkedToken),
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

    public async Task<IReadOnlyList<TranslationResult>> PolishAllAsync(
        PolishRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (_, _, _, selectedProviders) = PrepareExecution(request);
        if (selectedProviders.Count == 0)
        {
            return Array.Empty<TranslationResult>();
        }

        var finalByInstance = new Dictionary<string, ProviderStreamUpdate>(StringComparer.Ordinal);

        await foreach (var update in PolishParallelAsync(request, cancellationToken).ConfigureAwait(false))
        {
            finalByInstance[update.ProviderInstanceId] = update;
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

        return results;
    }

    private (
        PolishRequest EffectiveRequest,
        LanguageCode? InitialDetected,
        LanguageCode TargetLanguage,
        List<ProviderInstanceConfig> SelectedProviders)
        PrepareExecution(PolishRequest request)
    {
        var settings = _settingsStore.Current;
        var shouldMerge = request.ApplyTextPreprocessing && settings.MergePdfLineBreaks;
        var processedText = request.ApplyTextPreprocessing
            ? _textPreprocessor.Preprocess(request.Text, shouldMerge, isFromOcr: false)
            : request.Text ?? string.Empty;

        var resolvedCustomPrompt = request.Style == PolishStyle.Custom && string.IsNullOrWhiteSpace(request.CustomPrompt)
            ? (settings.Polish?.CustomPrompt ?? PolishConfig.DefaultCustomPrompt)
            : request.CustomPrompt;

        var effectiveLanguage = request.PreferredLanguage is { IsAuto: false } preferred
            ? preferred
            : _languageDetector.Detect(processedText).Language;

        var initialDetected = effectiveLanguage.IsAuto
            ? (LanguageCode?)null
            : effectiveLanguage;

        var effectiveRequest = request with
        {
            Text = processedText,
            CustomPrompt = resolvedCustomPrompt,
        };

        List<ProviderInstanceConfig> selected;
        if (request.ProviderInstanceIds is { Count: > 0 } requestedIds)
        {
            var idSet = new HashSet<string>(requestedIds, StringComparer.Ordinal);
            selected = settings.Providers
                .Where(p => idSet.Contains(p.Id) && _providers.ContainsKey(p.Kind))
                .OrderBy(p => p.SortOrder)
                .Select(p => p.Clone())
                .ToList();
        }
        else
        {
            selected = settings.Providers
                .Where(p => p.Enabled && _providers.ContainsKey(p.Kind))
                .OrderBy(p => p.SortOrder)
                .Select(p => p.Clone())
                .ToList();
        }

        return (effectiveRequest, initialDetected, effectiveLanguage, selected);
    }

    private async Task ExecuteProviderAsync(
        ProviderInstanceConfig instance,
        PolishRequest effectiveRequest,
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
                $"No polish provider is registered for kind '{instance.Kind}'.",
                sw.Elapsed));
            return;
        }

        try
        {
            string? chunkError = null;

            await foreach (var chunk in provider.PolishStreamAsync(effectiveRequest, instance, cancellationToken).ConfigureAwait(false))
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
                "Polish cancelled.",
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
