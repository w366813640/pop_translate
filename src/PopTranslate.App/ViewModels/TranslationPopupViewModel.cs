using System.Collections.ObjectModel;
using PopTranslate.App.Services;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;

namespace PopTranslate.App.ViewModels;

public sealed partial class TranslationPopupViewModel : ObservableObject
{
    private readonly ISettingsStore _settingsStore;
    private readonly ITextPreprocessor _textPreprocessor;
    private readonly ILanguageDetector _languageDetector;
    private readonly ILanguageRouter _languageRouter;
    private readonly ITranslationOrchestrator _translationOrchestrator;
    private readonly IBackTranslationService _backTranslationService;
    private readonly IPolishOrchestrator _polishOrchestrator;
    private readonly ITextDiffService _textDiffService;

    private DispatcherQueue? _dispatcher;
    private CancellationTokenSource? _activeTranslationCts;
    private readonly Dictionary<string, CancellationTokenSource> _singleRetryCtsMap = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _backTranslateCtsMap = new(StringComparer.Ordinal);
    private readonly HashSet<string> _autoBackTranslatedCardIds = new(StringComparer.Ordinal);

    private bool _isSuppressingLanguageReaction;
    private LanguageCode _lastDetectedSource = LanguageCode.Auto;
    private LanguageCode _lastEffectiveTarget = LanguageCode.ChineseSimplified;
    private bool _lastRequestWasFromOcr;
    private bool _autoCopiedInCurrentRun;
    private bool _lastSettingPinByDefault;
    private bool _lastSettingMergeLineBreaks = true;
    private PolishStyle _lastSettingDefaultPolishStyle = PolishStyle.General;
    private string _lastSettingCustomPrompt = PolishConfig.DefaultCustomPrompt;

    public TranslationPopupViewModel(
        ISettingsStore settingsStore,
        ITextPreprocessor textPreprocessor,
        ILanguageDetector languageDetector,
        ILanguageRouter languageRouter,
        ITranslationOrchestrator translationOrchestrator,
        IBackTranslationService backTranslationService,
        IPolishOrchestrator polishOrchestrator,
        ITextDiffService textDiffService)
    {
        _settingsStore = settingsStore;
        _textPreprocessor = textPreprocessor;
        _languageDetector = languageDetector;
        _languageRouter = languageRouter;
        _translationOrchestrator = translationOrchestrator;
        _backTranslationService = backTranslationService;
        _polishOrchestrator = polishOrchestrator;
        _textDiffService = textDiffService;

        SourceLanguageOptions = BuildSourceLanguageOptions();
        TargetLanguageOptions = BuildTargetLanguageOptions(AppSettings.CreateDefault());
        PolishStyleOptions = new ObservableCollection<PolishStyleOption>(PolishStyleOption.CreatePresets());

        SelectedSourceLanguage = SourceLanguageOptions[0];
        SelectedTargetLanguage = TargetLanguageOptions[0];
        SelectedPolishStyle = PolishStyleOptions[0];
        MergeLineBreaks = true;
    }

    public event EventHandler? ContentLayoutChanged;
    public event EventHandler<bool>? PinStateChanged;

    public ObservableCollection<LanguageOption> SourceLanguageOptions { get; }

    public ObservableCollection<LanguageOption> TargetLanguageOptions { get; }

    public ObservableCollection<PolishStyleOption> PolishStyleOptions { get; }

    public ObservableCollection<ServiceCardViewModel> ServiceCards { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTranslateMode))]
    [NotifyPropertyChangedFor(nameof(IsPolishMode))]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyPropertyChangedFor(nameof(PrimaryActionLabel))]
    [NotifyPropertyChangedFor(nameof(SourcePlaceholderText))]
    [NotifyPropertyChangedFor(nameof(EmptyHintText))]
    [NotifyPropertyChangedFor(nameof(NoProvidersHintText))]
    [NotifyPropertyChangedFor(nameof(NoProvidersButtonText))]
    [NotifyPropertyChangedFor(nameof(HasActiveModeProviders))]
    [NotifyPropertyChangedFor(nameof(ShowNoEnabledProvidersHint))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyHint))]
    public partial PopupMode CurrentMode { get; set; } = PopupMode.Translate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSourceText))]
    [NotifyPropertyChangedFor(nameof(CharacterCountText))]
    public partial string SourceText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial LanguageOption SelectedSourceLanguage { get; set; }

    [ObservableProperty]
    public partial LanguageOption SelectedTargetLanguage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomPolishStyleSelected))]
    public partial PolishStyleOption SelectedPolishStyle { get; set; }

    [ObservableProperty]
    public partial string InlineCustomPrompt { get; set; } = PolishConfig.DefaultCustomPrompt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetectedBadge))]
    public partial string DetectedLanguageBadge { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool MergeLineBreaks { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinGlyph))]
    [NotifyPropertyChangedFor(nameof(PinTooltip))]
    public partial bool IsPinned { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyHint))]
    public partial bool IsOcrLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusBanner))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyHint))]
    public partial string? StatusBannerText { get; set; }

    [ObservableProperty]
    public partial bool IsStatusError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveModeProviders))]
    [NotifyPropertyChangedFor(nameof(ShowNoEnabledProvidersHint))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyHint))]
    public partial bool HasEnabledProviders { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveModeProviders))]
    [NotifyPropertyChangedFor(nameof(ShowNoEnabledProvidersHint))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyHint))]
    public partial bool HasEligiblePolishProviders { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceCopyGlyph))]
    [NotifyPropertyChangedFor(nameof(SourceCopyTooltip))]
    public partial bool SourceJustCopied { get; set; }

    public bool IsTranslateMode => CurrentMode == PopupMode.Translate;

    public bool IsPolishMode => CurrentMode == PopupMode.Polish;

    public bool IsCustomPolishStyleSelected => SelectedPolishStyle?.Style == PolishStyle.Custom;

    public string WindowTitle => IsPolishMode ? "PopTranslate 润色" : "PopTranslate 翻译";

    public string PrimaryActionLabel => IsPolishMode ? "润色" : "翻译";

    public string SourcePlaceholderText => IsPolishMode
        ? "输入或粘贴需要润色的文本，按 Enter 润色（Shift+Enter 换行）"
        : "输入或粘贴文本，按 Enter 翻译（Shift+Enter 换行）";

    public string EmptyHintText => IsPolishMode
        ? "输入文本后按 Enter 立即并发润色 · 按 Esc 隐藏弹窗"
        : "输入文本后按 Enter 立即并发翻译 · 按 Esc 隐藏弹窗";

    public string NoProvidersHintText => IsPolishMode
        ? "润色功能需要至少启用一个 OpenAI 兼容大模型服务"
        : "当前未启用任何翻译服务";

    public string NoProvidersButtonText => IsPolishMode
        ? "前往配置大模型"
        : "前往设置启用服务";

    public bool HasActiveModeProviders => IsPolishMode ? HasEligiblePolishProviders : HasEnabledProviders;

    public bool HasSourceText => !string.IsNullOrWhiteSpace(SourceText);

    public string CharacterCountText => string.IsNullOrEmpty(SourceText) ? string.Empty : $"{SourceText.Length} 字";

    public string SourceCopyGlyph => SourceJustCopied ? "\uE73E" : "\uE8C8";

    public string SourceCopyTooltip => SourceJustCopied ? "已复制原文" : "复制原文";

    public bool HasDetectedBadge => !string.IsNullOrWhiteSpace(DetectedLanguageBadge);

    public bool HasStatusBanner => !string.IsNullOrWhiteSpace(StatusBannerText);

    public bool ShowNoEnabledProvidersHint => !HasActiveModeProviders;

    public bool ShowEmptyHint => ServiceCards.Count == 0 && !IsOcrLoading && !HasStatusBanner && HasActiveModeProviders;

    public string PinGlyph => IsPinned ? "\uE840" : "\uE718";

    public string PinTooltip => IsPinned ? "取消置顶（恢复失焦自动隐藏）" : "置顶弹窗（取消失焦自动隐藏）";

    public void AttachDispatcher(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void ApplySettings(AppSettings settings, bool applySessionDefaults = false)
    {
        _isSuppressingLanguageReaction = true;
        try
        {
            if (applySessionDefaults || settings.MergePdfLineBreaks != _lastSettingMergeLineBreaks)
            {
                MergeLineBreaks = settings.MergePdfLineBreaks;
                _lastSettingMergeLineBreaks = settings.MergePdfLineBreaks;
            }

            if (applySessionDefaults || settings.Window.PinByDefault != _lastSettingPinByDefault)
            {
                IsPinned = settings.Window.PinByDefault;
                _lastSettingPinByDefault = settings.Window.PinByDefault;
            }

            var previousTargetCode = SelectedTargetLanguage?.Code ?? LanguageCode.Auto;
            var refreshedTargets = BuildTargetLanguageOptions(settings);
            TargetLanguageOptions.Clear();
            foreach (var item in refreshedTargets)
            {
                TargetLanguageOptions.Add(item);
            }

            SelectedTargetLanguage = TargetLanguageOptions.FirstOrDefault(x => x.Code == previousTargetCode)
                ?? TargetLanguageOptions[0];

            var polish = settings.Polish ?? new PolishConfig();
            if (applySessionDefaults || polish.DefaultStyle != _lastSettingDefaultPolishStyle)
            {
                SelectedPolishStyle = PolishStyleOptions.FirstOrDefault(x => x.Style == polish.DefaultStyle)
                    ?? PolishStyleOptions[0];
                _lastSettingDefaultPolishStyle = polish.DefaultStyle;
            }

            var resolvedPrompt = string.IsNullOrWhiteSpace(polish.CustomPrompt)
                ? PolishConfig.DefaultCustomPrompt
                : polish.CustomPrompt;
            if (applySessionDefaults || !string.Equals(resolvedPrompt, _lastSettingCustomPrompt, StringComparison.Ordinal))
            {
                InlineCustomPrompt = resolvedPrompt;
                _lastSettingCustomPrompt = resolvedPrompt;
            }

            HasEnabledProviders = settings.Providers.Any(p => p.Enabled);
            HasEligiblePolishProviders = SafeGetEligiblePolishProviders().Count > 0;
        }
        finally
        {
            _isSuppressingLanguageReaction = false;
        }
    }

    public void PrepareForInput(string? initialHint = null)
    {
        CancelActiveTranslation();
        var modeChanged = CurrentMode != PopupMode.Translate;
        CurrentMode = PopupMode.Translate;

        if (modeChanged)
        {
            ServiceCards.Clear();
            DetectedLanguageBadge = string.Empty;
        }

        IsOcrLoading = false;
        StatusBannerText = initialHint;
        IsStatusError = false;
        RefreshProviderAvailability();
        OnPropertyChanged(nameof(ShowEmptyHint));
        NotifyLayoutChanged();
    }

    public void PrepareForPolishInput(string? initialHint = null)
    {
        CancelActiveTranslation();
        var modeChanged = CurrentMode != PopupMode.Polish;
        CurrentMode = PopupMode.Polish;

        SyncPolishDefaultsFromSettings();

        if (modeChanged)
        {
            ServiceCards.Clear();
            DetectedLanguageBadge = string.Empty;
        }

        IsOcrLoading = false;
        StatusBannerText = initialHint;
        IsStatusError = false;
        RefreshProviderAvailability();
        OnPropertyChanged(nameof(ShowEmptyHint));
        NotifyLayoutChanged();
    }

    public void BeginOcrCapture()
    {
        CancelActiveTranslation();
        CurrentMode = PopupMode.Translate;
        SourceText = string.Empty;
        DetectedLanguageBadge = string.Empty;
        ServiceCards.Clear();
        IsOcrLoading = true;
        IsStatusError = false;
        StatusBannerText = "正在识别截图中的文字...";
        RefreshProviderAvailability();
        OnPropertyChanged(nameof(ShowEmptyHint));
        NotifyLayoutChanged();
    }

    public void ShowStatusError(string message)
    {
        IsOcrLoading = false;
        IsStatusError = true;
        StatusBannerText = message;
        OnPropertyChanged(nameof(ShowEmptyHint));
        NotifyLayoutChanged();
    }

    public Task TranslateTextAsync(string rawText, bool isFromOcr = false, bool resetLanguageSelection = true)
    {
        CurrentMode = PopupMode.Translate;
        IsOcrLoading = false;
        IsStatusError = false;
        StatusBannerText = null;
        _lastRequestWasFromOcr = isFromOcr;

        if (resetLanguageSelection)
        {
            _isSuppressingLanguageReaction = true;
            try
            {
                SelectedSourceLanguage = SourceLanguageOptions[0];
                SelectedTargetLanguage = TargetLanguageOptions[0];
            }
            finally
            {
                _isSuppressingLanguageReaction = false;
            }
        }

        var preprocessed = _textPreprocessor.Preprocess(rawText, MergeLineBreaks, isFromOcr);
        SourceText = preprocessed;

        if (string.IsNullOrWhiteSpace(preprocessed))
        {
            CancelActiveTranslation();
            ServiceCards.Clear();
            DetectedLanguageBadge = string.Empty;
            RefreshProviderAvailability();
            OnPropertyChanged(nameof(ShowEmptyHint));
            NotifyLayoutChanged();
            return Task.CompletedTask;
        }

        return ExecuteTranslationAsync(preprocessed, isFromOcr);
    }

    public Task PolishTextAsync(string rawText, bool resetStyleToDefault = true)
    {
        CurrentMode = PopupMode.Polish;
        IsOcrLoading = false;
        IsStatusError = false;
        StatusBannerText = null;
        _lastRequestWasFromOcr = false;

        if (resetStyleToDefault)
        {
            SyncPolishDefaultsFromSettings();
        }

        var preprocessed = _textPreprocessor.Preprocess(rawText, MergeLineBreaks, isFromOcr: false);
        SourceText = preprocessed;

        if (string.IsNullOrWhiteSpace(preprocessed))
        {
            CancelActiveTranslation();
            ServiceCards.Clear();
            DetectedLanguageBadge = string.Empty;
            RefreshProviderAvailability();
            OnPropertyChanged(nameof(ShowEmptyHint));
            NotifyLayoutChanged();
            return Task.CompletedTask;
        }

        return ExecutePolishAsync(preprocessed);
    }

    [RelayCommand]
    private void SelectTranslateMode() => SwitchMode(PopupMode.Translate);

    [RelayCommand]
    private void SelectPolishMode() => SwitchMode(PopupMode.Polish);

    public void SwitchMode(PopupMode mode)
    {
        if (CurrentMode == mode)
        {
            return;
        }

        CancelActiveTranslation();
        CurrentMode = mode;
        IsOcrLoading = false;
        StatusBannerText = null;
        IsStatusError = false;
        ServiceCards.Clear();
        DetectedLanguageBadge = string.Empty;
        RefreshProviderAvailability();

        if (HasSourceText)
        {
            _ = RetranslateAsync();
        }
        else
        {
            OnPropertyChanged(nameof(ShowEmptyHint));
            NotifyLayoutChanged();
        }
    }

    [RelayCommand]
    public async Task RetranslateAsync()
    {
        var text = SourceText?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            CancelActiveTranslation();
            ServiceCards.Clear();
            DetectedLanguageBadge = string.Empty;
            RefreshProviderAvailability();
            OnPropertyChanged(nameof(ShowEmptyHint));
            NotifyLayoutChanged();
            return;
        }

        var processed = _textPreprocessor.Preprocess(text, MergeLineBreaks, CurrentMode == PopupMode.Translate && _lastRequestWasFromOcr);
        if (!string.Equals(processed, SourceText, StringComparison.Ordinal))
        {
            SourceText = processed;
        }

        if (CurrentMode == PopupMode.Polish)
        {
            await ExecutePolishAsync(processed);
        }
        else
        {
            await ExecuteTranslationAsync(processed, _lastRequestWasFromOcr);
        }
    }

    [RelayCommand]
    private async Task SwapLanguagesAsync()
    {
        var effectiveSource = SelectedSourceLanguage.Code.IsAuto
            ? _lastDetectedSource
            : SelectedSourceLanguage.Code;
        var effectiveTarget = SelectedTargetLanguage.Code.IsAuto
            ? _lastEffectiveTarget
            : SelectedTargetLanguage.Code;

        if (effectiveSource.IsAuto || effectiveTarget.IsAuto || effectiveSource == effectiveTarget)
        {
            return;
        }

        _isSuppressingLanguageReaction = true;
        try
        {
            SelectedSourceLanguage = SourceLanguageOptions.FirstOrDefault(x => x.Code == effectiveTarget)
                ?? SourceLanguageOptions[0];
            SelectedTargetLanguage = TargetLanguageOptions.FirstOrDefault(x => x.Code == effectiveSource)
                ?? TargetLanguageOptions[0];
        }
        finally
        {
            _isSuppressingLanguageReaction = false;
        }

        if (HasSourceText)
        {
            await RetranslateAsync();
        }
    }

    [RelayCommand]
    private void CopySource()
    {
        if (!AppAssetHelper.TryCopyTextToClipboard(SourceText))
        {
            return;
        }

        SourceJustCopied = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(1200);
            EnqueueUi(() => SourceJustCopied = false);
        });
    }

    [RelayCommand]
    private void ClearSource()
    {
        CancelActiveTranslation();
        SourceText = string.Empty;
        DetectedLanguageBadge = string.Empty;
        StatusBannerText = null;
        IsStatusError = false;
        ServiceCards.Clear();
        OnPropertyChanged(nameof(ShowEmptyHint));
        NotifyLayoutChanged();
    }

    public void CancelActiveTranslation()
    {
        if (_activeTranslationCts is not null)
        {
            try
            {
                _activeTranslationCts.Cancel();
            }
            catch
            {
                // Ignore cancellation race.
            }

            _activeTranslationCts.Dispose();
            _activeTranslationCts = null;
        }

        CancelAllCtsInMap(_singleRetryCtsMap);
        CancelAllCtsInMap(_backTranslateCtsMap);
        _autoBackTranslatedCardIds.Clear();
    }

    partial void OnSelectedSourceLanguageChanged(LanguageOption value)
    {
        if (_isSuppressingLanguageReaction || CurrentMode != PopupMode.Translate || !HasSourceText)
        {
            return;
        }

        _ = RetranslateAsync();
    }

    partial void OnSelectedTargetLanguageChanged(LanguageOption value)
    {
        if (_isSuppressingLanguageReaction || CurrentMode != PopupMode.Translate || !HasSourceText)
        {
            return;
        }

        _ = RetranslateAsync();
    }

    partial void OnMergeLineBreaksChanged(bool value)
    {
        if (_isSuppressingLanguageReaction || !HasSourceText)
        {
            return;
        }

        _ = RetranslateAsync();
    }

    partial void OnSelectedPolishStyleChanged(PolishStyleOption value)
    {
        NotifyLayoutChanged();

        if (_isSuppressingLanguageReaction || CurrentMode != PopupMode.Polish || !HasSourceText)
        {
            return;
        }

        _ = RetranslateAsync();
    }

    partial void OnIsPinnedChanged(bool value)
    {
        PinStateChanged?.Invoke(this, value);
    }

    private async Task ExecuteTranslationAsync(string text, bool isFromOcr)
    {
        CancelActiveTranslation();
        _autoCopiedInCurrentRun = false;

        var settings = _settingsStore.Current;
        var enabledProviders = settings.Providers
            .Where(p => p.Enabled)
            .OrderBy(p => p.SortOrder)
            .ToList();

        HasEnabledProviders = enabledProviders.Count > 0;
        ServiceCards.Clear();

        var routing = _languageRouter.Resolve(
            text,
            SelectedSourceLanguage.Code,
            SelectedTargetLanguage.Code,
            settings.PrimaryTargetLanguage,
            settings.SecondaryTargetLanguage);

        _lastDetectedSource = routing.DetectedSourceLanguage;
        _lastEffectiveTarget = routing.TargetLanguage;
        UpdateDetectedBadge(routing.DetectedSourceLanguage, routing.TargetLanguage);

        if (enabledProviders.Count == 0)
        {
            OnPropertyChanged(nameof(ShowEmptyHint));
            NotifyLayoutChanged();
            return;
        }

        var cardsById = new Dictionary<string, ServiceCardViewModel>(StringComparer.Ordinal);
        foreach (var provider in enabledProviders)
        {
            var card = new ServiceCardViewModel(
                provider,
                PopupMode.Translate,
                text,
                RetrySingleProviderAsync,
                BackTranslateCardManualAsync,
                CancelBackTranslationForCard,
                _textDiffService,
                NotifyLayoutChanged,
                showDiffByDefault: false,
                dispatcher: _dispatcher)
            {
                DetectedSourceLanguage = routing.DetectedSourceLanguage,
                TargetLanguage = routing.TargetLanguage,
            };

            ServiceCards.Add(card);
            cardsById[provider.Id] = card;
        }

        OnPropertyChanged(nameof(ShowEmptyHint));
        NotifyLayoutChanged();

        var cts = new CancellationTokenSource();
        _activeTranslationCts = cts;
        var ct = cts.Token;

        var request = new TranslationRequest(
            Text: text,
            SourceLanguage: SelectedSourceLanguage.Code,
            TargetLanguage: routing.TargetLanguage,
            ApplyTextPreprocessing: false, // Already preprocessed in UI so source box matches translated payload.
            IsFromOcr: isFromOcr);

        try
        {
            await foreach (var update in _translationOrchestrator.TranslateParallelAsync(request, ct))
            {
                EnqueueUi(() =>
                {
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    if (cardsById.TryGetValue(update.ProviderInstanceId, out var card))
                    {
                        card.ApplyUpdate(update);

                        if (update.State == ProviderStreamState.Completed &&
                            settings.AutoBackTranslate &&
                            !string.IsNullOrWhiteSpace(card.TranslatedText) &&
                            _autoBackTranslatedCardIds.Add(card.ProviderInstanceId))
                        {
                            _ = BackTranslateCardAsync(card.ProviderInstanceId, ct, expandCard: false);
                        }
                    }

                    if (update.DetectedSourceLanguage is { IsAuto: false } detected)
                    {
                        _lastDetectedSource = detected;
                        UpdateDetectedBadge(detected, update.TargetLanguage);
                    }

                    if (update.State == ProviderStreamState.Completed &&
                        !_autoCopiedInCurrentRun &&
                        settings.Window.AutoCopyFirstResult &&
                        !string.IsNullOrWhiteSpace(update.AccumulatedText))
                    {
                        _autoCopiedInCurrentRun = true;
                        AppAssetHelper.TryCopyTextToClipboard(update.AccumulatedText);
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when a newer request starts, mode switches, or popup is dismissed.
        }
        catch (Exception ex)
        {
            EnqueueUi(() => ShowStatusError($"翻译调度发生错误：{ex.Message}"));
        }
    }

    private async Task ExecutePolishAsync(string text)
    {
        CancelActiveTranslation();
        _autoCopiedInCurrentRun = false;

        var settings = _settingsStore.Current;
        var eligibleProviders = SafeGetEligiblePolishProviders();

        HasEligiblePolishProviders = eligibleProviders.Count > 0;
        ServiceCards.Clear();

        var detection = _languageDetector.Detect(text);
        _lastDetectedSource = detection.Language;
        UpdatePolishBadge(detection.Language, SelectedPolishStyle);

        if (eligibleProviders.Count == 0)
        {
            OnPropertyChanged(nameof(ShowNoEnabledProvidersHint));
            OnPropertyChanged(nameof(ShowEmptyHint));
            NotifyLayoutChanged();
            return;
        }

        var showDiffByDefault = settings.Polish?.ShowDiffByDefault ?? false;
        var cardsById = new Dictionary<string, ServiceCardViewModel>(StringComparer.Ordinal);
        foreach (var provider in eligibleProviders)
        {
            var card = new ServiceCardViewModel(
                provider,
                PopupMode.Polish,
                text,
                RetrySingleProviderAsync,
                backTranslateCallback: null,
                cancelBackTranslateCallback: null,
                _textDiffService,
                NotifyLayoutChanged,
                showDiffByDefault: showDiffByDefault,
                dispatcher: _dispatcher)
            {
                DetectedSourceLanguage = detection.Language,
                TargetLanguage = detection.Language,
            };

            ServiceCards.Add(card);
            cardsById[provider.Id] = card;
        }

        OnPropertyChanged(nameof(ShowEmptyHint));
        NotifyLayoutChanged();

        var cts = new CancellationTokenSource();
        _activeTranslationCts = cts;
        var ct = cts.Token;

        var request = BuildPolishRequest(text, detection.Language, settings, providerInstanceIds: null);

        try
        {
            await foreach (var update in _polishOrchestrator.PolishParallelAsync(request, ct))
            {
                EnqueueUi(() =>
                {
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    if (cardsById.TryGetValue(update.ProviderInstanceId, out var card))
                    {
                        card.ApplyUpdate(update);
                    }

                    if (update.DetectedSourceLanguage is { IsAuto: false } detected)
                    {
                        _lastDetectedSource = detected;
                        UpdatePolishBadge(detected, SelectedPolishStyle);
                    }

                    if (update.State == ProviderStreamState.Completed &&
                        !_autoCopiedInCurrentRun &&
                        settings.Window.AutoCopyFirstResult &&
                        !string.IsNullOrWhiteSpace(update.AccumulatedText))
                    {
                        _autoCopiedInCurrentRun = true;
                        AppAssetHelper.TryCopyTextToClipboard(update.AccumulatedText);
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when a newer request starts, mode switches, or popup is dismissed.
        }
        catch (Exception ex)
        {
            EnqueueUi(() => ShowStatusError($"润色调度发生错误：{ex.Message}"));
        }
    }

    private Task RetrySingleProviderAsync(string providerInstanceId) =>
        CurrentMode == PopupMode.Polish
            ? RetrySinglePolishProviderAsync(providerInstanceId)
            : RetrySingleTranslateProviderAsync(providerInstanceId);

    private async Task RetrySingleTranslateProviderAsync(string providerInstanceId)
    {
        var text = SourceText?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var card = ServiceCards.FirstOrDefault(c => string.Equals(c.ProviderInstanceId, providerInstanceId, StringComparison.Ordinal));
        if (card is null)
        {
            return;
        }

        CancelBackTranslationForCard(providerInstanceId);
        ReplaceCtsInMap(_singleRetryCtsMap, providerInstanceId, out var cts);
        var ct = cts.Token;

        card.ResetForRetry();

        var settings = _settingsStore.Current;
        var routing = _languageRouter.Resolve(
            text,
            SelectedSourceLanguage.Code,
            SelectedTargetLanguage.Code,
            settings.PrimaryTargetLanguage,
            settings.SecondaryTargetLanguage);

        card.DetectedSourceLanguage = routing.DetectedSourceLanguage;
        card.TargetLanguage = routing.TargetLanguage;

        var request = new TranslationRequest(
            Text: text,
            SourceLanguage: SelectedSourceLanguage.Code,
            TargetLanguage: routing.TargetLanguage,
            ApplyTextPreprocessing: false,
            IsFromOcr: _lastRequestWasFromOcr,
            ProviderInstanceIds: [providerInstanceId]);

        var autoBackTriggered = false;

        try
        {
            await foreach (var update in _translationOrchestrator.TranslateParallelAsync(request, ct))
            {
                EnqueueUi(() =>
                {
                    if (!ct.IsCancellationRequested &&
                        string.Equals(update.ProviderInstanceId, providerInstanceId, StringComparison.Ordinal))
                    {
                        card.ApplyUpdate(update);

                        if (update.State == ProviderStreamState.Completed &&
                            settings.AutoBackTranslate &&
                            !string.IsNullOrWhiteSpace(card.TranslatedText) &&
                            !autoBackTriggered)
                        {
                            autoBackTriggered = true;
                            _ = BackTranslateCardAsync(providerInstanceId, ct, expandCard: false);
                        }
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled cleanly.
        }
        catch (Exception ex)
        {
            EnqueueUi(() => card.ApplyUpdate(new ProviderStreamUpdate(
                providerInstanceId,
                card.DisplayName,
                card.ProviderKind,
                ProviderStreamState.Failed,
                string.Empty,
                card.TranslatedText,
                null,
                routing.TargetLanguage,
                ex.Message,
                TimeSpan.Zero)));
        }
    }

    private async Task RetrySinglePolishProviderAsync(string providerInstanceId)
    {
        var text = SourceText?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var card = ServiceCards.FirstOrDefault(c => string.Equals(c.ProviderInstanceId, providerInstanceId, StringComparison.Ordinal));
        if (card is null)
        {
            return;
        }

        ReplaceCtsInMap(_singleRetryCtsMap, providerInstanceId, out var cts);
        var ct = cts.Token;

        card.ResetForRetry();

        var settings = _settingsStore.Current;
        var detection = _languageDetector.Detect(text);
        var request = BuildPolishRequest(text, detection.Language, settings, providerInstanceIds: [providerInstanceId]);

        try
        {
            await foreach (var update in _polishOrchestrator.PolishParallelAsync(request, ct))
            {
                EnqueueUi(() =>
                {
                    if (!ct.IsCancellationRequested &&
                        string.Equals(update.ProviderInstanceId, providerInstanceId, StringComparison.Ordinal))
                    {
                        card.ApplyUpdate(update);
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled cleanly.
        }
        catch (Exception ex)
        {
            EnqueueUi(() => card.ApplyUpdate(new ProviderStreamUpdate(
                providerInstanceId,
                card.DisplayName,
                card.ProviderKind,
                ProviderStreamState.Failed,
                string.Empty,
                card.TranslatedText,
                null,
                detection.Language,
                ex.Message,
                TimeSpan.Zero)));
        }
    }

    private Task BackTranslateCardManualAsync(string providerInstanceId) =>
        BackTranslateCardAsync(providerInstanceId, CancellationToken.None, expandCard: true);

    private async Task BackTranslateCardAsync(
        string providerInstanceId,
        CancellationToken parentToken,
        bool expandCard)
    {
        var card = ServiceCards.FirstOrDefault(c => string.Equals(c.ProviderInstanceId, providerInstanceId, StringComparison.Ordinal));
        if (card is null || !card.IsTranslateMode || string.IsNullOrWhiteSpace(card.TranslatedText))
        {
            return;
        }

        CancelBackTranslationForCard(providerInstanceId);

        var cts = parentToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(parentToken)
            : new CancellationTokenSource();
        _backTranslateCtsMap[providerInstanceId] = cts;
        var ct = cts.Token;

        var translatedLang = !card.TargetLanguage.IsAuto ? card.TargetLanguage : _lastEffectiveTarget;
        var backTarget = ResolveBackTargetLanguage(card);

        card.BeginBackTranslation(backTarget, expandCard);

        var request = new BackTranslationRequest(
            ProviderInstanceId: providerInstanceId,
            TranslatedText: card.TranslatedText,
            TranslatedLanguage: translatedLang,
            BackTargetLanguage: backTarget);

        try
        {
            await foreach (var update in _backTranslationService.BackTranslateStreamAsync(request, ct))
            {
                EnqueueUi(() =>
                {
                    if (!ct.IsCancellationRequested)
                    {
                        card.ApplyBackTranslationUpdate(update);
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled cleanly.
        }
        catch (Exception ex)
        {
            EnqueueUi(() =>
            {
                if (!ct.IsCancellationRequested)
                {
                    card.ApplyBackTranslationUpdate(new ProviderStreamUpdate(
                        providerInstanceId,
                        card.DisplayName,
                        card.ProviderKind,
                        ProviderStreamState.Failed,
                        string.Empty,
                        card.BackTranslatedText,
                        translatedLang,
                        backTarget,
                        ex.Message,
                        TimeSpan.Zero));
                }
            });
        }
        finally
        {
            if (_backTranslateCtsMap.TryGetValue(providerInstanceId, out var active) && ReferenceEquals(active, cts))
            {
                _backTranslateCtsMap.Remove(providerInstanceId);
            }

            cts.Dispose();
        }
    }

    private LanguageCode ResolveBackTargetLanguage(ServiceCardViewModel card)
    {
        if (!SelectedSourceLanguage.Code.IsAuto)
        {
            return SelectedSourceLanguage.Code;
        }

        if (card.DetectedSourceLanguage is { IsAuto: false } cardDetected)
        {
            return cardDetected;
        }

        if (!_lastDetectedSource.IsAuto)
        {
            return _lastDetectedSource;
        }

        if (!string.IsNullOrWhiteSpace(card.SourceText))
        {
            var detected = _languageDetector.Detect(card.SourceText);
            if (!detected.Language.IsAuto)
            {
                return detected.Language;
            }
        }

        return LanguageCode.Auto;
    }

    private void CancelBackTranslationForCard(string providerInstanceId)
    {
        if (_backTranslateCtsMap.Remove(providerInstanceId, out var prev))
        {
            try
            {
                prev.Cancel();
            }
            catch
            {
                // Ignore cancellation race.
            }

            prev.Dispose();
        }
    }

    private PolishRequest BuildPolishRequest(
        string text,
        LanguageCode detectedLanguage,
        AppSettings settings,
        IReadOnlyList<string>? providerInstanceIds)
    {
        var style = SelectedPolishStyle?.Style ?? PolishStyle.General;
        string? customPrompt = null;
        if (style == PolishStyle.Custom)
        {
            var fallback = string.IsNullOrWhiteSpace(settings.Polish?.CustomPrompt)
                ? PolishConfig.DefaultCustomPrompt
                : settings.Polish.CustomPrompt;
            customPrompt = string.IsNullOrWhiteSpace(InlineCustomPrompt)
                ? fallback
                : InlineCustomPrompt.Trim();
        }

        return new PolishRequest(
            Text: text,
            Style: style,
            CustomPrompt: customPrompt,
            PreferredLanguage: detectedLanguage.IsAuto ? null : detectedLanguage,
            ApplyTextPreprocessing: false,
            ProviderInstanceIds: providerInstanceIds);
    }

    private void SyncPolishDefaultsFromSettings()
    {
        var polish = _settingsStore.Current.Polish ?? new PolishConfig();
        _isSuppressingLanguageReaction = true;
        try
        {
            SelectedPolishStyle = PolishStyleOptions.FirstOrDefault(x => x.Style == polish.DefaultStyle)
                ?? PolishStyleOptions[0];
            InlineCustomPrompt = string.IsNullOrWhiteSpace(polish.CustomPrompt)
                ? PolishConfig.DefaultCustomPrompt
                : polish.CustomPrompt;
            _lastSettingDefaultPolishStyle = polish.DefaultStyle;
            _lastSettingCustomPrompt = InlineCustomPrompt;
        }
        finally
        {
            _isSuppressingLanguageReaction = false;
        }
    }

    private void RefreshProviderAvailability()
    {
        HasEnabledProviders = _settingsStore.Current.Providers.Any(p => p.Enabled);
        HasEligiblePolishProviders = SafeGetEligiblePolishProviders().Count > 0;
    }

    private IReadOnlyList<ProviderInstanceConfig> SafeGetEligiblePolishProviders()
    {
        try
        {
            return _polishOrchestrator.GetEligibleProviders();
        }
        catch
        {
            return [];
        }
    }

    private void UpdateDetectedBadge(LanguageCode detectedSource, LanguageCode target)
    {
        var sourceName = detectedSource.IsAuto ? "自动" : detectedSource.NativeName;
        var targetName = target.IsAuto ? "自动" : target.NativeName;
        DetectedLanguageBadge = $"{sourceName} → {targetName}";
    }

    private void UpdatePolishBadge(LanguageCode detectedSource, PolishStyleOption? styleOption)
    {
        var styleLabel = styleOption?.DisplayLabel ?? "通用流畅";
        DetectedLanguageBadge = detectedSource.IsAuto
            ? $"润色 · {styleLabel}"
            : $"{detectedSource.NativeName} · {styleLabel}";
    }

    private void NotifyLayoutChanged()
    {
        ContentLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EnqueueUi(Action action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcher.TryEnqueue(() => action());
        }
    }

    private static void ReplaceCtsInMap(
        Dictionary<string, CancellationTokenSource> map,
        string key,
        out CancellationTokenSource next)
    {
        if (map.TryGetValue(key, out var prev))
        {
            try
            {
                prev.Cancel();
            }
            catch
            {
                // Ignore cancellation race.
            }

            prev.Dispose();
        }

        next = new CancellationTokenSource();
        map[key] = next;
    }

    private static void CancelAllCtsInMap(Dictionary<string, CancellationTokenSource> map)
    {
        foreach (var cts in map.Values)
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
                // Ignore cancellation race.
            }

            cts.Dispose();
        }

        map.Clear();
    }

    private static ObservableCollection<LanguageOption> BuildSourceLanguageOptions()
    {
        var list = new ObservableCollection<LanguageOption>();
        foreach (var lang in LanguageCode.SelectableSources)
        {
            list.Add(new LanguageOption(lang, lang.IsAuto ? "自动检测" : lang.NativeName));
        }

        return list;
    }

    private static ObservableCollection<LanguageOption> BuildTargetLanguageOptions(AppSettings settings)
    {
        var list = new ObservableCollection<LanguageOption>
        {
            new(
                LanguageCode.Auto,
                $"自动 ({settings.PrimaryTargetLanguage.NativeName}↔{settings.SecondaryTargetLanguage.NativeName})"),
        };

        foreach (var lang in LanguageCode.SelectableTargets)
        {
            list.Add(new LanguageOption(lang, lang.NativeName));
        }

        return list;
    }
}
