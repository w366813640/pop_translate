using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using PopTranslate.Core.Translation;

namespace PopTranslate.Core.Polish;

public sealed class PolishOrchestrator : IPolishOrchestrator
{
    private static readonly TimeSpan WorkerTeardownTimeout = TimeSpan.FromSeconds(1);

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
        ResolveDefaultPolishProviders(_settingsStore.Current.Providers ?? []);

    public IAsyncEnumerable<ProviderStreamUpdate> PolishParallelAsync(
        PolishRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prepared = PrepareExecution(request);
        return PolishParallelCoreAsync(prepared, cancellationToken);
    }

    public async Task<IReadOnlyList<TranslationResult>> PolishAllAsync(
        PolishRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var prepared = PrepareExecution(request);
        if (prepared.SelectedProviders.Count == 0)
        {
            return Array.Empty<TranslationResult>();
        }

        var finalByInstance = new Dictionary<string, ProviderStreamUpdate>(StringComparer.Ordinal);

        await foreach (var update in PolishParallelCoreAsync(prepared, cancellationToken).ConfigureAwait(false))
        {
            finalByInstance[update.ProviderInstanceId] = update;
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

        return results;
    }

    private async IAsyncEnumerable<ProviderStreamUpdate> PolishParallelCoreAsync(
        PreparedPolishExecution prepared,
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
                DetectedSourceLanguage: prepared.InitialDetected,
                TargetLanguage: prepared.TargetLanguage,
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
                    prepared.AllowInstanceCustomPromptFallback,
                    prepared.InitialDetected,
                    prepared.TargetLanguage,
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

    private PreparedPolishExecution PrepareExecution(PolishRequest request)
    {
        var settings = _settingsStore.Current;
        string processedText;

        if (!request.ApplyTextPreprocessing)
        {
            processedText = request.Text ?? string.Empty;
        }
        else
        {
            var shouldMerge = settings.MergePdfLineBreaks;
            processedText = _textPreprocessor.Preprocess(request.Text, shouldMerge, isFromOcr: false);
            if ((!settings.EnableCodeIdentifierSplit || request.Style == PolishStyle.CodeNaming) &&
                IsSingleWhitespaceFreeToken(request.Text))
            {
                processedText = request.Text!.Trim();
            }
        }

        var hasExplicitCustomPrompt = !string.IsNullOrWhiteSpace(request.CustomPrompt);
        var globalCustomPrompt = settings.Polish?.CustomPrompt ?? PolishConfig.DefaultCustomPrompt;
        var resolvedCustomPrompt = request.Style == PolishStyle.Custom && !hasExplicitCustomPrompt
            ? globalCustomPrompt
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
            PreferredLanguage = initialDetected,
        };

        List<ProviderInstanceConfig> selected;
        var providersList = settings.Providers ?? [];
        if (request.ProviderInstanceIds is { Count: > 0 } requestedIds)
        {
            var idSet = new HashSet<string>(requestedIds, StringComparer.Ordinal);
            selected = providersList
                .Where(p => p is not null
                    && idSet.Contains(p.Id)
                    && p.Kind == ProviderKind.OpenAiCompatible
                    && _providers.ContainsKey(p.Kind))
                .OrderBy(p => p.SortOrder)
                .Select(p => p.Clone())
                .ToList();
        }
        else
        {
            selected = ResolveDefaultPolishProviders(providersList);
        }

        var allowInstanceCustomPromptFallback =
            !hasExplicitCustomPrompt ||
            string.Equals(request.CustomPrompt, globalCustomPrompt, StringComparison.Ordinal);

        return new PreparedPolishExecution(
            effectiveRequest,
            initialDetected,
            effectiveLanguage,
            selected,
            allowInstanceCustomPromptFallback);
    }

    private List<ProviderInstanceConfig> ResolveDefaultPolishProviders(IReadOnlyList<ProviderInstanceConfig> providersList)
    {
        var enabledOpenAi = providersList
            .Where(p => p is not null
                && p.Enabled
                && p.Kind == ProviderKind.OpenAiCompatible
                && _providers.ContainsKey(p.Kind))
            .OrderBy(p => p.SortOrder)
            .ToList();

        var dedicatedPolish = enabledOpenAi
            .Where(p => p.Role == ProviderRole.Polish)
            .ToList();

        var activeList = dedicatedPolish.Count > 0 ? dedicatedPolish : enabledOpenAi;
        return activeList
            .Select(p => p.Clone())
            .ToList();
    }

    private async Task ExecuteProviderAsync(
        ProviderInstanceConfig instance,
        PolishRequest effectiveRequest,
        bool allowInstanceCustomPromptFallback,
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

        var providerRequest = effectiveRequest;
        if (effectiveRequest.Style == PolishStyle.Custom &&
            allowInstanceCustomPromptFallback &&
            instance.Role == ProviderRole.Polish &&
            !string.IsNullOrWhiteSpace(instance.SystemPrompt) &&
            !string.Equals(instance.SystemPrompt.Trim(), ProviderInstanceConfig.DefaultOpenAiSystemPrompt, StringComparison.Ordinal))
        {
            providerRequest = effectiveRequest with { CustomPrompt = instance.SystemPrompt.Trim() };
        }

        try
        {
            string? chunkError = null;

            if (provider is IReasoningPolishProvider reasoningProvider)
            {
                await foreach (var item in reasoningProvider
                    .PolishReasoningStreamAsync(providerRequest, instance, cancellationToken)
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
                await foreach (var chunk in provider.PolishStreamAsync(providerRequest, instance, cancellationToken).ConfigureAwait(false))
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

    private readonly record struct PreparedPolishExecution(
        PolishRequest EffectiveRequest,
        LanguageCode? InitialDetected,
        LanguageCode TargetLanguage,
        List<ProviderInstanceConfig> SelectedProviders,
        bool AllowInstanceCustomPromptFallback);
}
