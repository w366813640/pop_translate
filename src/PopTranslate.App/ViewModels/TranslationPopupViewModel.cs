using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using PopTranslate.App.Services;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

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
    private readonly IOcrOrchestrator _ocrOrchestrator;
    private readonly ITextWriteBackService _textWriteBackService;
    private readonly ITtsService _ttsService;

    private DispatcherQueue? _dispatcher;
    private CancellationTokenSource? _activeTranslationCts;
    private CancellationTokenSource? _ocrRerunCts;
    private CancellationTokenSource? _ttsCts;
    private string? _speakingCardId;
    private readonly Dictionary<string, CancellationTokenSource> _singleRetryCtsMap = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _backTranslateCtsMap = new(StringComparer.Ordinal);
    private readonly HashSet<string> _autoBackTranslatedCardIds = new(StringComparer.Ordinal);

    private bool _isSuppressingLanguageReaction;
    private bool _prioritizePolishInCurrentSession;
    private int _openFlyoutCount;
    private LanguageCode _lastDetectedSource = LanguageCode.Auto;
    private LanguageCode _lastEffectiveTarget = LanguageCode.ChineseSimplified;
    private bool _lastRequestWasFromOcr;
    private bool _autoCopiedInCurrentRun;
    private bool _lastSettingPinByDefault;
    private bool _lastSettingMergeLineBreaks = true;
    private OcrPromptMode _lastSettingOcrPromptMode = OcrPromptMode.PlainText;
    private CapturedScreenshot? _lastOcrScreenshot;
    private nint _sourceWindowHandle;

    public TranslationPopupViewModel(
        ISettingsStore settingsStore,
        ITextPreprocessor textPreprocessor,
        ILanguageDetector languageDetector,
        ILanguageRouter languageRouter,
        ITranslationOrchestrator translationOrchestrator,
        IBackTranslationService backTranslationService,
        IPolishOrchestrator polishOrchestrator,
        ITextDiffService textDiffService,
        IOcrOrchestrator ocrOrchestrator,
        ITextWriteBackService textWriteBackService,
        ITtsService ttsService)
    {
        _settingsStore = settingsStore;
        _textPreprocessor = textPreprocessor;
        _languageDetector = languageDetector;
        _languageRouter = languageRouter;
        _translationOrchestrator = translationOrchestrator;
        _backTranslationService = backTranslationService;
        _polishOrchestrator = polishOrchestrator;
        _textDiffService = textDiffService;
        _ocrOrchestrator = ocrOrchestrator;
        _textWriteBackService = textWriteBackService;
        _ttsService = ttsService;

        SourceLanguageOptions = BuildSourceLanguageOptions();
        TargetLanguageOptions = BuildTargetLanguageOptions(AppSettings.CreateDefault());
        OcrPromptModeOptions = new ObservableCollection<OcrPromptModeOption>(OcrPromptModeOption.CreatePresets());

        SelectedSourceLanguage = SourceLanguageOptions[0];
        SelectedTargetLanguage = TargetLanguageOptions[0];
        SelectedOcrPromptMode = OcrPromptModeOptions[0];
        MergeLineBreaks = true;
    }

    public event EventHandler? ContentLayoutChanged;
    public event EventHandler<bool>? PinStateChanged;
    public event EventHandler? HidePopupRequested;

    public ObservableCollection<LanguageOption> SourceLanguageOptions { get; }

    public ObservableCollection<LanguageOption> TargetLanguageOptions { get; }

    public ObservableCollection<OcrPromptModeOption> OcrPromptModeOptions { get; }

    public ObservableCollection<ServiceCardViewModel> ServiceCards { get; } = [];

    public nint SourceWindowHandle => _sourceWindowHandle;

    public bool HasSourceWindow => _sourceWindowHandle != 0;

    public bool HasOpenInternalFlyout => _openFlyoutCount > 0;

    public DateTimeOffset LastFlyoutActivityUtc { get; private set; } = DateTimeOffset.MinValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSourceText))]
    [NotifyPropertyChangedFor(nameof(CharacterCountText))]
    [NotifyPropertyChangedFor(nameof(ExpandedSourceFooterHint))]
    [NotifyPropertyChangedFor(nameof(SourceCollapsedPreviewText))]
    [NotifyPropertyChangedFor(nameof(SourceLanguageButtonText))]
    [NotifyPropertyChangedFor(nameof(ShowCollapsedSourceBar))]
    [NotifyPropertyChangedFor(nameof(ShowExpandedSourceCard))]
    public partial string SourceText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceLanguageButtonText))]
    public partial LanguageOption SelectedSourceLanguage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetLanguageButtonText))]
    public partial LanguageOption SelectedTargetLanguage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedOcrPromptModeLabel))]
    public partial OcrPromptModeOption SelectedOcrPromptMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMergeLineBreaksOff))]
    [NotifyPropertyChangedFor(nameof(MergeLineBreaksTooltip))]
    public partial bool MergeLineBreaks { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnpinned))]
    [NotifyPropertyChangedFor(nameof(PinGlyph))]
    [NotifyPropertyChangedFor(nameof(PinTooltip))]
    public partial bool IsPinned { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSourceCardExpanded))]
    [NotifyPropertyChangedFor(nameof(ShowCollapsedSourceBar))]
    [NotifyPropertyChangedFor(nameof(ShowExpandedSourceCard))]
    public partial bool IsSourceCardCollapsed { get; set; }

    [ObservableProperty]
    public partial bool HasOcrContext { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OcrPreviewChevronGlyph))]
    [NotifyPropertyChangedFor(nameof(OcrPreviewToggleLabel))]
    public partial bool IsOcrPreviewExpanded { get; set; }

    [ObservableProperty]
    public partial ImageSource? OcrPreviewImage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyHint))]
    [NotifyPropertyChangedFor(nameof(ShowCollapsedSourceBar))]
    [NotifyPropertyChangedFor(nameof(ShowExpandedSourceCard))]
    public partial bool IsOcrLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusBanner))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyHint))]
    public partial string? StatusBannerText { get; set; }

    [ObservableProperty]
    public partial bool IsStatusError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoEnabledProvidersHint))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyHint))]
    public partial bool HasEnabledProviders { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceSpeakGlyph))]
    [NotifyPropertyChangedFor(nameof(SourceSpeakTooltip))]
    public partial bool IsSpeakingSource { get; set; }

    public string WindowTitle => "PopTranslate";

    public bool IsSourceCardExpanded => !IsSourceCardCollapsed;

    public bool ShowCollapsedSourceBar => IsSourceCardCollapsed && (!IsOcrLoading || HasSourceText);

    public bool ShowExpandedSourceCard => !IsSourceCardCollapsed && (!IsOcrLoading || HasSourceText);

    public bool IsMergeLineBreaksOff => !MergeLineBreaks;

    public bool IsUnpinned => !IsPinned;

    public string MergeLineBreaksTooltip => MergeLineBreaks
        ? "智能合并 PDF / OCR 换行：已开启（点击关闭）"
        : "智能合并 PDF / OCR 换行：已关闭（点击开启）";

    public string SourceLanguageButtonText
    {
        get
        {
            var selected = SelectedSourceLanguage?.Code ?? LanguageCode.Auto;
            if (!selected.IsAuto)
            {
                return selected.NativeName;
            }

            return !_lastDetectedSource.IsAuto && HasSourceText
                ? $"自动 · {_lastDetectedSource.NativeName}"
                : "自动检测";
        }
    }

    public string TargetLanguageButtonText
    {
        get
        {
            var selected = SelectedTargetLanguage?.Code ?? LanguageCode.Auto;
            if (!selected.IsAuto)
            {
                return selected.NativeName;
            }

            var settings = _settingsStore.Current;
            return $"自动 ({FormatShortLanguageLabel(settings.PrimaryTargetLanguage)}↔{FormatShortLanguageLabel(settings.SecondaryTargetLanguage)})";
        }
    }

    public string SelectedOcrPromptModeLabel => SelectedOcrPromptMode?.DisplayLabel ?? "纯文本";

    public string SourceCollapsedPreviewText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SourceText))
            {
                return "点击展开输入或粘贴原文...";
            }

            var collapsed = SourceText
                .Replace("\r\n", " ", StringComparison.Ordinal)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
            return collapsed.Length <= 120 ? collapsed : string.Concat(collapsed.AsSpan(0, 120), "...");
        }
    }

    public string OcrPreviewChevronGlyph => IsOcrPreviewExpanded ? "\uE70E" : "\uE70D";

    public string OcrPreviewToggleLabel => IsOcrPreviewExpanded ? "收起截图" : "截图预览";

    public string SourcePlaceholderText => "输入或粘贴文本，按 Enter 立即执行（Shift+Enter 换行）";

    public string EmptyHintText => "输入文本后按 Enter 立即并发执行 · Alt+S 互换语种 · Esc 隐藏";

    public string NoProvidersHintText => "当前未启用任何翻译或 AI 润色服务";

    public string NoProvidersButtonText => "前往设置启用服务";

    public bool HasSourceText => !string.IsNullOrWhiteSpace(SourceText);

    public string CharacterCountText => string.IsNullOrEmpty(SourceText) ? string.Empty : $"{SourceText.Length} 字";

    public string ExpandedSourceFooterHint => string.IsNullOrEmpty(SourceText)
        ? "Enter 立即执行 · Shift+Enter 换行"
        : $"{SourceText.Length} 字 · Enter 立即执行";

    public string SourceSpeakGlyph => IsSpeakingSource ? "\uE71A" : "\uE767";

    public string SourceSpeakTooltip => IsSpeakingSource ? "停止朗读原文" : "朗读原文";

    public bool HasStatusBanner => !string.IsNullOrWhiteSpace(StatusBannerText);

    public bool ShowNoEnabledProvidersHint => !HasEnabledProviders;

    public bool ShowEmptyHint => ServiceCards.Count == 0 && !IsOcrLoading && !HasStatusBanner && HasEnabledProviders;

    public string PinGlyph => IsPinned ? "\uE840" : "\uE718";

    public string PinTooltip => IsPinned ? "取消置顶（恢复失焦自动隐藏，Ctrl+P）" : "置顶弹窗（取消失焦自动隐藏，Ctrl+P）";

    public void AttachDispatcher(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void NotifyInternalFlyoutOpened()
    {
        _openFlyoutCount++;
        LastFlyoutActivityUtc = DateTimeOffset.UtcNow;
    }

    public void NotifyInternalFlyoutClosed()
    {
        _openFlyoutCount = Math.Max(0, _openFlyoutCount - 1);
        LastFlyoutActivityUtc = DateTimeOffset.UtcNow;
    }

    public void ResetInternalFlyoutState()
    {
        _openFlyoutCount = 0;
    }

    public void SetSourceWindowHandle(nint windowHandle)
    {
        if (_sourceWindowHandle != windowHandle)
        {
            _sourceWindowHandle = windowHandle;
            OnPropertyChanged(nameof(SourceWindowHandle));
            OnPropertyChanged(nameof(HasSourceWindow));
        }

        var hasWindow = windowHandle != 0;
        foreach (var card in ServiceCards)
        {
            card.HasSourceWindow = hasWindow;
        }
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

            var ocrMode = settings.Ocr?.PromptMode ?? OcrPromptMode.PlainText;
            if (applySessionDefaults || ocrMode != _lastSettingOcrPromptMode)
            {
                SelectedOcrPromptMode = OcrPromptModeOptions.FirstOrDefault(x => x.Mode == ocrMode)
                    ?? OcrPromptModeOptions[0];
                _lastSettingOcrPromptMode = ocrMode;
            }

            HasEnabledProviders = HasAnyUsableEnabledProvider(settings);
            OnPropertyChanged(nameof(TargetLanguageButtonText));
        }
        finally
        {
            _isSuppressingLanguageReaction = false;
        }
    }

    public void PrepareForInput(
        string? initialHint = null,
        bool prioritizePolish = false,
        bool collapseSourceCard = false)
    {
        CancelActiveTranslation();
        _lastRequestWasFromOcr = false;
        _prioritizePolishInCurrentSession = prioritizePolish;
        _lastDetectedSource = LanguageCode.Auto;
        ClearOcrContext();

        IsSourceCardCollapsed = collapseSourceCard;
        SourceText = string.Empty;
        ServiceCards.Clear();
        IsOcrLoading = false;
        StatusBannerText = initialHint;
        IsStatusError = false;
        RefreshProviderAvailability();
        OnPropertyChanged(nameof(SourceLanguageButtonText));
        OnPropertyChanged(nameof(ShowEmptyHint));
        NotifyLayoutChanged();
    }

    public void PrepareForPolishInput(string? initialHint = null) =>
        PrepareForInput(initialHint, prioritizePolish: true, collapseSourceCard: false);

    public void BeginOcrCapture(CapturedScreenshot? screenshot = null)
    {
        CancelActiveTranslation();
        _lastRequestWasFromOcr = true;
        _prioritizePolishInCurrentSession = false;
        _lastOcrScreenshot = screenshot;
        _lastDetectedSource = LanguageCode.Auto;

        IsSourceCardCollapsed = true;
        SourceText = string.Empty;
        ServiceCards.Clear();

        SyncOcrPromptModeFromSettings();
        HasOcrContext = screenshot is not null;
        IsOcrPreviewExpanded = false;
        OcrPreviewImage = null;
        if (screenshot is { PngBytes.Length: > 0 })
        {
            _ = LoadOcrPreviewBitmapAsync(screenshot.PngBytes);
        }

        IsOcrLoading = true;
        IsStatusError = false;
        StatusBannerText = "正在识别截图中的文字...";
        RefreshProviderAvailability();
        OnPropertyChanged(nameof(SourceLanguageButtonText));
        OnPropertyChanged(nameof(ShowEmptyHint));
        NotifyLayoutChanged();
    }

    public void ShowStatusError(string message)
    {
        IsOcrLoading = false;
        IsStatusError = true;
        StatusBannerText = message;
        if (!HasSourceText && !HasOcrContext)
        {
            IsSourceCardCollapsed = false;
        }

        OnPropertyChanged(nameof(ShowEmptyHint));
        NotifyLayoutChanged();
    }

    public Task TranslateTextAsync(
        string rawText,
        bool isFromOcr = false,
        bool resetLanguageSelection = true,
        bool prioritizePolish = false,
        bool collapseSourceCard = true)
    {
        _prioritizePolishInCurrentSession = prioritizePolish;
        IsOcrLoading = false;
        IsStatusError = false;
        StatusBannerText = null;
        _lastRequestWasFromOcr = isFromOcr;

        if (!isFromOcr)
        {
            ClearOcrContext();
        }

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

        var mergeBreaks = MergeLineBreaks &&
                          (!isFromOcr || SelectedOcrPromptMode.Mode == OcrPromptMode.PlainText);
        var preprocessed = PreprocessInputText(rawText, mergeBreaks, isFromOcr);
        SourceText = preprocessed;

        if (string.IsNullOrWhiteSpace(preprocessed))
        {
            IsSourceCardCollapsed = false;
            CancelActiveTranslation();
            ServiceCards.Clear();
            _lastDetectedSource = LanguageCode.Auto;
            RefreshProviderAvailability();
            OnPropertyChanged(nameof(SourceLanguageButtonText));
            OnPropertyChanged(nameof(ShowEmptyHint));
            NotifyLayoutChanged();
            return Task.CompletedTask;
        }

        IsSourceCardCollapsed = collapseSourceCard;
        return ExecuteUnifiedAsync(preprocessed, isFromOcr, prioritizePolish);
    }

    public Task PolishTextAsync(string rawText, bool resetStyleToDefault = true) =>
        TranslateTextAsync(
            rawText,
            isFromOcr: false,
            resetLanguageSelection: resetStyleToDefault,
            prioritizePolish: true,
            collapseSourceCard: true);

    public async Task TriggerAllPolishCardsAsync()
    {
        _prioritizePolishInCurrentSession = true;
        if (!HasSourceText)
        {
            return;
        }

        var waitingPolishCards = ServiceCards
            .Where(c => c.IsPolishMode && c.IsWaitingOnDemand)
            .ToList();

        if (waitingPolishCards.Count > 0)
        {
            var tasks = waitingPolishCards.Select(c => RetrySinglePolishCardAsync(c.CardId));
            await Task.WhenAll(tasks);
            return;
        }

        if (!ServiceCards.Any(c => c.IsPolishMode))
        {
            await RetranslateAsync();
        }
    }

    [RelayCommand]
    private void ToggleMergeLineBreaks()
    {
        MergeLineBreaks = !MergeLineBreaks;
    }

    [RelayCommand]
    private void TogglePin()
    {
        IsPinned = !IsPinned;
    }

    [RelayCommand]
    public async Task RetranslateAsync()
    {
        var text = SourceText?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            CancelActiveTranslation();
            ServiceCards.Clear();
            _lastDetectedSource = LanguageCode.Auto;
            RefreshProviderAvailability();
            OnPropertyChanged(nameof(SourceLanguageButtonText));
            OnPropertyChanged(nameof(ShowEmptyHint));
            NotifyLayoutChanged();
            return;
        }

        // Preserve user's manual Shift+Enter newlines in SourceText without overwriting SourceText!
        var isOcrFlow = _lastRequestWasFromOcr;
        var shouldMergeLineBreaks = isOcrFlow &&
                                    MergeLineBreaks &&
                                    SelectedOcrPromptMode.Mode == OcrPromptMode.PlainText;
        var processed = PreprocessInputText(
            text,
            mergeLineBreaks: shouldMergeLineBreaks,
            isFromOcr: isOcrFlow);

        await ExecuteUnifiedAsync(processed, isOcrFlow, _prioritizePolishInCurrentSession);
    }

    private string PreprocessInputText(string rawText, bool mergeLineBreaks, bool isFromOcr)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return string.Empty;
        }

        var settings = _settingsStore.Current;
        var disableIdentifierSplit = !settings.EnableCodeIdentifierSplit || isFromOcr;

        if (disableIdentifierSplit && IsSingleWhitespaceFreeToken(rawText))
        {
            return rawText.Trim();
        }

        return _textPreprocessor.Preprocess(rawText, mergeLineBreaks, isFromOcr);
    }

    private static bool IsSingleWhitespaceFreeToken(string rawText)
    {
        foreach (var ch in rawText.AsSpan().Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                return false;
            }
        }

        return true;
    }

    [RelayCommand]
    private void ToggleSourceCollapse()
    {
        IsSourceCardCollapsed = !IsSourceCardCollapsed;
        NotifyLayoutChanged();
    }

    [RelayCommand]
    private void ToggleOcrPreview()
    {
        IsOcrPreviewExpanded = !IsOcrPreviewExpanded;
        NotifyLayoutChanged();
    }

    [RelayCommand]
    public async Task RerunOcrAsync()
    {
        var screenshot = _lastOcrScreenshot;
        if (screenshot is null || screenshot.PngBytes.Length == 0)
        {
            return;
        }

        CancelActiveTranslation();
        _ocrRerunCts?.Cancel();
        _ocrRerunCts?.Dispose();
        _ocrRerunCts = new CancellationTokenSource();
        var ct = _ocrRerunCts.Token;

        ServiceCards.Clear();
        IsOcrLoading = true;
        IsStatusError = false;
        StatusBannerText = $"正在按「{SelectedOcrPromptMode.DisplayLabel}」模式重新识别...";
        OnPropertyChanged(nameof(ShowEmptyHint));
        NotifyLayoutChanged();

        try
        {
            var ocrResult = await _ocrOrchestrator.RecognizeAsync(screenshot.ToOcrImage(), ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (ocrResult.IsSuccess && !string.IsNullOrWhiteSpace(ocrResult.Text))
            {
                await TranslateTextAsync(
                    ocrResult.Text,
                    isFromOcr: true,
                    resetLanguageSelection: false,
                    prioritizePolish: _prioritizePolishInCurrentSession,
                    collapseSourceCard: true);
            }
            else
            {
                var error = string.IsNullOrWhiteSpace(ocrResult.ErrorMessage)
                    ? "未在所选截图区域中识别到文字。"
                    : $"OCR 识别失败：{ocrResult.ErrorMessage}";
                ShowStatusError(error);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled cleanly.
        }
        catch (Exception ex)
        {
            ShowStatusError($"OCR 识别异常：{ex.Message}");
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

        OnPropertyChanged(nameof(SourceLanguageButtonText));
        OnPropertyChanged(nameof(TargetLanguageButtonText));

        if (HasSourceText)
        {
            await RetranslateAsync();
        }
    }

    [RelayCommand]
    private void ClearSource()
    {
        CancelActiveTranslation();
        SourceText = string.Empty;
        _lastDetectedSource = LanguageCode.Auto;
        StatusBannerText = null;
        IsStatusError = false;
        ServiceCards.Clear();
        OnPropertyChanged(nameof(SourceLanguageButtonText));
        OnPropertyChanged(nameof(ShowEmptyHint));
        NotifyLayoutChanged();
    }

    [RelayCommand]
    public async Task ToggleSpeakSourceAsync()
    {
        if (IsSpeakingSource)
        {
            StopActiveSpeech();
            return;
        }

        var text = SourceText?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        StopActiveSpeech();

        var cts = new CancellationTokenSource();
        _ttsCts = cts;
        IsSpeakingSource = true;

        var lang = !SelectedSourceLanguage.Code.IsAuto
            ? SelectedSourceLanguage.Code
            : (!_lastDetectedSource.IsAuto ? _lastDetectedSource : _languageDetector.Detect(text).Language);

        try
        {
            await _ttsService.SpeakAsync(text, lang.IsAuto ? null : lang, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Speech stopped cleanly.
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("TTS", "Source speech playback failed.", ex);
        }
        finally
        {
            EnqueueUi(() =>
            {
                if (ReferenceEquals(_ttsCts, cts))
                {
                    _ttsCts = null;
                    IsSpeakingSource = false;
                }
            });

            cts.Dispose();
        }
    }

    public async Task ToggleSpeakCardAsync(string cardId)
    {
        var card = FindCardById(cardId);
        if (card is null || string.IsNullOrWhiteSpace(card.TranslatedText))
        {
            return;
        }

        if (card.IsSpeaking && string.Equals(_speakingCardId, cardId, StringComparison.Ordinal))
        {
            StopActiveSpeech();
            return;
        }

        StopActiveSpeech();

        var cts = new CancellationTokenSource();
        _ttsCts = cts;
        _speakingCardId = cardId;
        card.IsSpeaking = true;

        var lang = !card.TargetLanguage.IsAuto
            ? card.TargetLanguage
            : _languageDetector.Detect(card.TranslatedText).Language;

        try
        {
            await _ttsService.SpeakAsync(card.TranslatedText, lang.IsAuto ? null : lang, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Speech stopped cleanly.
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("TTS", "Card speech playback failed.", ex);
        }
        finally
        {
            EnqueueUi(() =>
            {
                if (ReferenceEquals(_ttsCts, cts))
                {
                    _ttsCts = null;
                    _speakingCardId = null;
                    card.IsSpeaking = false;
                }
            });

            cts.Dispose();
        }
    }

    public void StopActiveSpeech()
    {
        if (_ttsCts is not null)
        {
            try
            {
                _ttsCts.Cancel();
            }
            catch
            {
                // Ignore cancellation race.
            }

            _ttsCts = null;
        }

        try
        {
            if (_ttsService.IsSpeaking)
            {
                _ttsService.Stop();
            }
        }
        catch
        {
            // Ignore TTS teardown errors.
        }

        IsSpeakingSource = false;
        _speakingCardId = null;
        foreach (var card in ServiceCards)
        {
            if (card.IsSpeaking)
            {
                card.IsSpeaking = false;
            }
        }
    }

    public async Task<bool> WriteBackCardAsync(string cardId)
    {
        var card = FindCardById(cardId);
        if (card is null || _sourceWindowHandle == 0 || string.IsNullOrEmpty(card.TranslatedText) || card.IsLoading)
        {
            return false;
        }

        StopActiveSpeech();
        var targetHwnd = _sourceWindowHandle;
        var text = card.TranslatedText;

        ClipboardLoopGuard.SuppressFor(TimeSpan.FromMilliseconds(1400));
        HidePopupRequested?.Invoke(this, EventArgs.Empty);

        try
        {
            // Allow popup hide transition to relinquish foreground cleanly before SendInput Ctrl+V.
            await Task.Delay(25);
            var replaced = await _textWriteBackService.ReplaceTextInWindowAsync(targetHwnd, text);
            if (replaced)
            {
                card.MarkWroteBack();
            }

            return replaced;
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("WriteBack", "Write-back replacement failed.", ex);
            return false;
        }
    }

    public async Task<bool> WriteBackFirstResultAsync()
    {
        if (_sourceWindowHandle == 0)
        {
            return false;
        }

        ServiceCardViewModel? candidate = null;
        if (_prioritizePolishInCurrentSession)
        {
            candidate = ServiceCards.FirstOrDefault(c => c.IsPolishMode && c.CanWriteBack);
        }

        candidate ??= ServiceCards.FirstOrDefault(c => c.CanWriteBack);
        if (candidate is null)
        {
            return false;
        }

        return await WriteBackCardAsync(candidate.CardId);
    }

    public bool CopyCardByIndex(int zeroBasedIndex)
    {
        if (zeroBasedIndex < 0 || zeroBasedIndex >= ServiceCards.Count)
        {
            return false;
        }

        var card = ServiceCards[zeroBasedIndex];
        if (!card.HasText)
        {
            return false;
        }

        card.CopyResult();
        return true;
    }

    public bool CopyFirstAvailableCard()
    {
        var first = ServiceCards.FirstOrDefault(c => c.HasText);
        if (first is null)
        {
            return false;
        }

        first.CopyResult();
        return true;
    }

    public void CancelActiveTranslation()
    {
        StopActiveSpeech();

        if (_ocrRerunCts is not null)
        {
            try
            {
                _ocrRerunCts.Cancel();
            }
            catch
            {
                // Ignore cancellation race.
            }

            _ocrRerunCts.Dispose();
            _ocrRerunCts = null;
        }

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
        if (_isSuppressingLanguageReaction || !HasSourceText)
        {
            return;
        }

        _ = RetranslateAsync();
    }

    partial void OnSelectedTargetLanguageChanged(LanguageOption value)
    {
        if (_isSuppressingLanguageReaction || !HasSourceText)
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

    partial void OnSelectedOcrPromptModeChanged(OcrPromptModeOption value)
    {
        if (_isSuppressingLanguageReaction || value is null)
        {
            return;
        }

        _ = PersistOcrPromptModeAndRerunAsync(value.Mode);
    }

    partial void OnIsPinnedChanged(bool value)
    {
        PinStateChanged?.Invoke(this, value);
    }

    private async Task PersistOcrPromptModeAndRerunAsync(OcrPromptMode mode)
    {
        try
        {
            var updated = _settingsStore.Current.Clone();
            updated.Ocr.PromptMode = mode;
            _lastSettingOcrPromptMode = mode;
            await _settingsStore.SaveAsync(updated);
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("OCR", "Failed to save OCR prompt mode.", ex);
        }

        if (HasOcrContext && _lastOcrScreenshot is not null)
        {
            await RerunOcrAsync();
        }
    }

    private async Task LoadOcrPreviewBitmapAsync(byte[] pngBytes)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(pngBytes.AsBuffer());
            stream.Seek(0);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            OcrPreviewImage = bitmap;
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("OCR", "Failed to decode OCR screenshot preview bitmap.", ex);
        }
    }

    private void ClearOcrContext()
    {
        _lastOcrScreenshot = null;
        HasOcrContext = false;
        IsOcrPreviewExpanded = false;
        OcrPreviewImage = null;
    }

    private void SyncOcrPromptModeFromSettings()
    {
        var ocrMode = _settingsStore.Current.Ocr?.PromptMode ?? OcrPromptMode.PlainText;
        _isSuppressingLanguageReaction = true;
        try
        {
            SelectedOcrPromptMode = OcrPromptModeOptions.FirstOrDefault(x => x.Mode == ocrMode)
                ?? OcrPromptModeOptions[0];
            _lastSettingOcrPromptMode = ocrMode;
        }
        finally
        {
            _isSuppressingLanguageReaction = false;
        }
    }

    private async Task ExecuteUnifiedAsync(string text, bool isFromOcr, bool prioritizePolish)
    {
        CancelActiveTranslation();
        _autoCopiedInCurrentRun = false;
        StatusBannerText = null;
        IsStatusError = false;

        var settings = _settingsStore.Current;
        var enabledProviders = settings.Providers
            .Where(p => p.Enabled)
            .OrderBy(p => p.SortOrder)
            .ToList();

        ServiceCards.Clear();

        var routing = _languageRouter.Resolve(
            text,
            SelectedSourceLanguage.Code,
            SelectedTargetLanguage.Code,
            settings.PrimaryTargetLanguage,
            settings.SecondaryTargetLanguage);

        _lastDetectedSource = routing.DetectedSourceLanguage;
        _lastEffectiveTarget = routing.TargetLanguage;
        OnPropertyChanged(nameof(SourceLanguageButtonText));
        OnPropertyChanged(nameof(TargetLanguageButtonText));

        var showDiffByDefault = settings.Polish?.ShowDiffByDefault ?? false;
        var translateCardsByProviderId = new Dictionary<string, ServiceCardViewModel>(StringComparer.Ordinal);
        var polishCardsToRun = new List<(ServiceCardViewModel Card, ProviderInstanceConfig Config)>();
        var polishCardCount = 0;

        foreach (var provider in enabledProviders)
        {
            if (provider.Role == ProviderRole.Polish)
            {
                if (provider.Kind != ProviderKind.OpenAiCompatible)
                {
                    continue;
                }

                polishCardCount++;
                var isOnDemand = provider.TriggerMode == PolishTriggerMode.OnDemand && !prioritizePolish;
                var polishCard = CreatePolishCard(
                    cardId: provider.Id,
                    config: provider,
                    sourceText: text,
                    initialStyle: provider.DefaultPolishStyle,
                    isWaitingOnDemand: isOnDemand,
                    showDiffByDefault: showDiffByDefault,
                    detectedLanguage: routing.DetectedSourceLanguage);

                ServiceCards.Add(polishCard);
                if (!isOnDemand)
                {
                    polishCardsToRun.Add((polishCard, provider));
                }
            }
            else
            {
                var translateCard = CreateTranslateCard(
                    cardId: provider.Id,
                    config: provider,
                    sourceText: text,
                    detectedSource: routing.DetectedSourceLanguage,
                    targetLanguage: routing.TargetLanguage);

                ServiceCards.Add(translateCard);
                translateCardsByProviderId[provider.Id] = translateCard;
            }
        }

        // Fallback: If invoked via Alt+P and the user has 0 enabled Role == ProviderRole.Polish providers,
        // temporarily append/run any enabled OpenAiCompatible provider as a Polish card so Alt+P works out-of-the-box!
        if (prioritizePolish && polishCardCount == 0)
        {
            var fallbackOpenAiProviders = enabledProviders
                .Where(p => p.Kind == ProviderKind.OpenAiCompatible)
                .ToList();

            if (fallbackOpenAiProviders.Count == 0)
            {
                fallbackOpenAiProviders = SafeGetEligiblePolishProviders().ToList();
            }

            var defaultPolishStyle = settings.Polish?.DefaultStyle ?? PolishStyle.General;
            foreach (var fallbackProvider in fallbackOpenAiProviders)
            {
                var fallbackStyle = fallbackProvider.DefaultPolishStyle != PolishStyle.General
                    ? fallbackProvider.DefaultPolishStyle
                    : defaultPolishStyle;

                var fallbackCard = CreatePolishCard(
                    cardId: $"{fallbackProvider.Id}::polish-fallback",
                    config: fallbackProvider,
                    sourceText: text,
                    initialStyle: fallbackStyle,
                    isWaitingOnDemand: false,
                    showDiffByDefault: showDiffByDefault,
                    detectedLanguage: routing.DetectedSourceLanguage);

                ServiceCards.Add(fallbackCard);
                polishCardsToRun.Add((fallbackCard, fallbackProvider));
                polishCardCount++;
            }

            if (polishCardCount == 0 && ServiceCards.Count > 0)
            {
                StatusBannerText = "尚未启用 OpenAI 兼容大模型，前往「设置 → 翻译服务」添加即可使用 AI 润色。";
                IsStatusError = false;
            }
        }

        HasEnabledProviders = ServiceCards.Count > 0;
        OnPropertyChanged(nameof(ShowNoEnabledProvidersHint));
        OnPropertyChanged(nameof(ShowEmptyHint));
        NotifyLayoutChanged();

        if (ServiceCards.Count == 0)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _activeTranslationCts = cts;
        var ct = cts.Token;

        var parallelTasks = new List<Task>(1 + polishCardsToRun.Count);

        if (translateCardsByProviderId.Count > 0)
        {
            parallelTasks.Add(StreamTranslateCardsBatchAsync(
                text,
                isFromOcr,
                routing.TargetLanguage,
                settings,
                translateCardsByProviderId,
                preferPolishForAutoCopy: prioritizePolish && polishCardsToRun.Count > 0,
                ct));
        }

        foreach (var (polishCard, config) in polishCardsToRun)
        {
            parallelTasks.Add(StreamPolishCardInternalAsync(
                polishCard,
                config,
                text,
                routing.DetectedSourceLanguage,
                settings,
                allowAutoCopy: true,
                ct));
        }

        if (parallelTasks.Count > 0)
        {
            await Task.WhenAll(parallelTasks);
        }
    }

    private ServiceCardViewModel CreateTranslateCard(
        string cardId,
        ProviderInstanceConfig config,
        string sourceText,
        LanguageCode detectedSource,
        LanguageCode targetLanguage)
    {
        return new ServiceCardViewModel(
            config,
            PopupMode.Translate,
            sourceText,
            RetrySingleCardAsync,
            BackTranslateCardManualAsync,
            CancelBackTranslationForCard,
            _textDiffService,
            NotifyLayoutChanged,
            showDiffByDefault: false,
            dispatcher: _dispatcher,
            writeBackCallback: WriteBackCardAsync,
            toggleSpeakCallback: ToggleSpeakCardAsync,
            hasSourceWindow: HasSourceWindow,
            cardId: cardId,
            flyoutOpenedCallback: NotifyInternalFlyoutOpened,
            flyoutClosedCallback: NotifyInternalFlyoutClosed)
        {
            DetectedSourceLanguage = detectedSource,
            TargetLanguage = targetLanguage,
        };
    }

    private ServiceCardViewModel CreatePolishCard(
        string cardId,
        ProviderInstanceConfig config,
        string sourceText,
        PolishStyle initialStyle,
        bool isWaitingOnDemand,
        bool showDiffByDefault,
        LanguageCode detectedLanguage)
    {
        return new ServiceCardViewModel(
            config,
            PopupMode.Polish,
            sourceText,
            RetrySingleCardAsync,
            backTranslateCallback: null,
            cancelBackTranslateCallback: null,
            _textDiffService,
            NotifyLayoutChanged,
            showDiffByDefault: showDiffByDefault,
            dispatcher: _dispatcher,
            writeBackCallback: WriteBackCardAsync,
            toggleSpeakCallback: ToggleSpeakCardAsync,
            hasSourceWindow: HasSourceWindow,
            cardId: cardId,
            initialPolishStyle: initialStyle,
            isWaitingOnDemand: isWaitingOnDemand,
            flyoutOpenedCallback: NotifyInternalFlyoutOpened,
            flyoutClosedCallback: NotifyInternalFlyoutClosed)
        {
            DetectedSourceLanguage = detectedLanguage,
            TargetLanguage = detectedLanguage,
        };
    }

    private async Task StreamTranslateCardsBatchAsync(
        string text,
        bool isFromOcr,
        LanguageCode targetLanguage,
        AppSettings settings,
        Dictionary<string, ServiceCardViewModel> translateCardsByProviderId,
        bool preferPolishForAutoCopy,
        CancellationToken ct)
    {
        var request = new TranslationRequest(
            Text: text,
            SourceLanguage: SelectedSourceLanguage.Code,
            TargetLanguage: targetLanguage,
            ApplyTextPreprocessing: false,
            IsFromOcr: isFromOcr,
            ProviderInstanceIds: translateCardsByProviderId.Keys.ToList());

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

                    translateCardsByProviderId.TryGetValue(update.ProviderInstanceId, out var card);
                    if (card is not null)
                    {
                        card.ApplyUpdate(update);

                        if (update.State == ProviderStreamState.Completed &&
                            settings.AutoBackTranslate &&
                            !string.IsNullOrWhiteSpace(card.TranslatedText) &&
                            _autoBackTranslatedCardIds.Add(card.CardId))
                        {
                            _ = BackTranslateCardAsync(card.CardId, ct, expandCard: false);
                        }
                    }

                    if (update.DetectedSourceLanguage is { IsAuto: false } detected)
                    {
                        _lastDetectedSource = detected;
                        OnPropertyChanged(nameof(SourceLanguageButtonText));
                    }

                    if (update.State == ProviderStreamState.Completed &&
                        !preferPolishForAutoCopy &&
                        !_autoCopiedInCurrentRun &&
                        settings.Window.AutoCopyFirstResult &&
                        !string.IsNullOrWhiteSpace(update.AccumulatedText))
                    {
                        if (ClipboardLoopGuard.TryCopyText(update.AccumulatedText))
                        {
                            _autoCopiedInCurrentRun = true;
                            if (card is not null)
                            {
                                card.IsAutoCopied = true;
                            }
                        }
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when a newer request starts or popup is dismissed.
        }
        catch (Exception ex)
        {
            EnqueueUi(() => ShowStatusError($"翻译调度发生错误：{ex.Message}"));
        }
    }

    private async Task StreamPolishCardInternalAsync(
        ServiceCardViewModel card,
        ProviderInstanceConfig? config,
        string text,
        LanguageCode detectedLanguage,
        AppSettings settings,
        bool allowAutoCopy,
        CancellationToken parentCt)
    {
        var linkedCts = parentCt.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(parentCt)
            : new CancellationTokenSource();

        if (_singleRetryCtsMap.Remove(card.CardId, out var previous))
        {
            try { previous.Cancel(); } catch { }
            previous.Dispose();
        }

        _singleRetryCtsMap[card.CardId] = linkedCts;
        var ct = linkedCts.Token;

        var request = BuildPolishRequestForCard(text, detectedLanguage, settings, card, config);

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

                    if (string.Equals(update.ProviderInstanceId, card.ProviderInstanceId, StringComparison.Ordinal))
                    {
                        card.ApplyUpdate(update);
                    }

                    if (update.DetectedSourceLanguage is { IsAuto: false } detected)
                    {
                        _lastDetectedSource = detected;
                        OnPropertyChanged(nameof(SourceLanguageButtonText));
                    }

                    if (allowAutoCopy &&
                        update.State == ProviderStreamState.Completed &&
                        !_autoCopiedInCurrentRun &&
                        settings.Window.AutoCopyFirstResult &&
                        !string.IsNullOrWhiteSpace(update.AccumulatedText))
                    {
                        if (ClipboardLoopGuard.TryCopyText(update.AccumulatedText))
                        {
                            _autoCopiedInCurrentRun = true;
                            card.IsAutoCopied = true;
                        }
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when card style changes, newer request starts, or popup is dismissed.
        }
        catch (Exception ex)
        {
            EnqueueUi(() =>
            {
                if (!ct.IsCancellationRequested)
                {
                    card.ApplyUpdate(new ProviderStreamUpdate(
                        card.ProviderInstanceId,
                        card.DisplayName,
                        card.ProviderKind,
                        ProviderStreamState.Failed,
                        string.Empty,
                        card.TranslatedText,
                        null,
                        detectedLanguage,
                        ex.Message,
                        TimeSpan.Zero));
                }
            });
        }
        finally
        {
            if (_singleRetryCtsMap.TryGetValue(card.CardId, out var active) && ReferenceEquals(active, linkedCts))
            {
                _singleRetryCtsMap.Remove(card.CardId);
            }

            linkedCts.Dispose();
        }
    }

    private Task RetrySingleCardAsync(string cardId)
    {
        var card = FindCardById(cardId);
        if (card is null)
        {
            return Task.CompletedTask;
        }

        return card.IsPolishMode
            ? RetrySinglePolishCardAsync(cardId)
            : RetrySingleTranslateCardAsync(cardId);
    }

    private async Task RetrySingleTranslateCardAsync(string cardId)
    {
        var text = SourceText?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var card = FindCardById(cardId);
        if (card is null)
        {
            return;
        }

        CancelBackTranslationForCard(cardId);
        ReplaceCtsInMap(_singleRetryCtsMap, cardId, out var cts);
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
            ProviderInstanceIds: [card.ProviderInstanceId]);

        var autoBackTriggered = false;

        try
        {
            await foreach (var update in _translationOrchestrator.TranslateParallelAsync(request, ct))
            {
                EnqueueUi(() =>
                {
                    if (!ct.IsCancellationRequested &&
                        string.Equals(update.ProviderInstanceId, card.ProviderInstanceId, StringComparison.Ordinal))
                    {
                        card.ApplyUpdate(update);

                        if (update.State == ProviderStreamState.Completed &&
                            settings.AutoBackTranslate &&
                            !string.IsNullOrWhiteSpace(card.TranslatedText) &&
                            !autoBackTriggered)
                        {
                            autoBackTriggered = true;
                            _ = BackTranslateCardAsync(cardId, ct, expandCard: false);
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
                card.ProviderInstanceId,
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

    private async Task RetrySinglePolishCardAsync(string cardId)
    {
        var text = SourceText?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var card = FindCardById(cardId);
        if (card is null)
        {
            return;
        }

        card.ResetForRetry();

        var settings = _settingsStore.Current;
        var config = settings.Providers.FirstOrDefault(p =>
            string.Equals(p.Id, card.ProviderInstanceId, StringComparison.Ordinal));
        var detection = _languageDetector.Detect(text);
        var parentCt = _activeTranslationCts?.Token ?? CancellationToken.None;

        await StreamPolishCardInternalAsync(
            card,
            config,
            text,
            detection.Language,
            settings,
            allowAutoCopy: false,
            parentCt);
    }

    private Task BackTranslateCardManualAsync(string cardId) =>
        BackTranslateCardAsync(cardId, CancellationToken.None, expandCard: true);

    private async Task BackTranslateCardAsync(
        string cardId,
        CancellationToken parentToken,
        bool expandCard)
    {
        var card = FindCardById(cardId);
        if (card is null || !card.IsTranslateMode || string.IsNullOrWhiteSpace(card.TranslatedText))
        {
            return;
        }

        CancelBackTranslationForCard(cardId);

        var cts = parentToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(parentToken)
            : new CancellationTokenSource();
        _backTranslateCtsMap[cardId] = cts;
        var ct = cts.Token;

        var translatedLang = !card.TargetLanguage.IsAuto ? card.TargetLanguage : _lastEffectiveTarget;
        var backTarget = ResolveBackTargetLanguage(card);

        card.BeginBackTranslation(backTarget, expandCard);

        var request = new BackTranslationRequest(
            ProviderInstanceId: card.ProviderInstanceId,
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
                        card.ProviderInstanceId,
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
            if (_backTranslateCtsMap.TryGetValue(cardId, out var active) && ReferenceEquals(active, cts))
            {
                _backTranslateCtsMap.Remove(cardId);
            }

            cts.Dispose();
        }
    }

    private ServiceCardViewModel? FindCardById(string cardId) =>
        ServiceCards.FirstOrDefault(c => string.Equals(c.CardId, cardId, StringComparison.Ordinal))
        ?? ServiceCards.FirstOrDefault(c => string.Equals(c.ProviderInstanceId, cardId, StringComparison.Ordinal));

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

    private void CancelBackTranslationForCard(string cardId)
    {
        if (_backTranslateCtsMap.Remove(cardId, out var prev))
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

    private static PolishRequest BuildPolishRequestForCard(
        string text,
        LanguageCode detectedLanguage,
        AppSettings settings,
        ServiceCardViewModel card,
        ProviderInstanceConfig? config)
    {
        var style = card.CardPolishStyle;
        string? customPrompt = null;

        if (style == PolishStyle.Custom)
        {
            if (config is not null &&
                !string.IsNullOrWhiteSpace(config.SystemPrompt) &&
                !string.Equals(
                    config.SystemPrompt.Trim(),
                    ProviderInstanceConfig.DefaultOpenAiSystemPrompt,
                    StringComparison.Ordinal))
            {
                customPrompt = config.SystemPrompt.Trim();
            }
            else
            {
                customPrompt = string.IsNullOrWhiteSpace(settings.Polish?.CustomPrompt)
                    ? PolishConfig.DefaultCustomPrompt
                    : settings.Polish.CustomPrompt.Trim();
            }
        }

        return new PolishRequest(
            Text: text,
            Style: style,
            CustomPrompt: customPrompt,
            PreferredLanguage: detectedLanguage.IsAuto ? null : detectedLanguage,
            ApplyTextPreprocessing: false,
            ProviderInstanceIds: [card.ProviderInstanceId]);
    }

    private static bool HasAnyUsableEnabledProvider(AppSettings settings) =>
        settings.Providers.Any(p =>
            p.Enabled &&
            (p.Role == ProviderRole.Translate || p.Kind == ProviderKind.OpenAiCompatible));

    private void RefreshProviderAvailability()
    {
        HasEnabledProviders = HasAnyUsableEnabledProvider(_settingsStore.Current);
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

    private static string FormatShortLanguageLabel(LanguageCode code) => code.Value switch
    {
        "zh-Hans" => "中",
        "zh-Hant" => "繁",
        "en" => "英",
        "ja" => "日",
        "ko" => "韩",
        "fr" => "法",
        "de" => "德",
        "es" => "西",
        "ru" => "俄",
        "it" => "意",
        "pt" => "葡",
        "ar" => "阿",
        _ => code.NativeName,
    };

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
                $"自动 ({FormatShortLanguageLabel(settings.PrimaryTargetLanguage)}↔{FormatShortLanguageLabel(settings.SecondaryTargetLanguage)})"),
        };

        foreach (var lang in LanguageCode.SelectableTargets)
        {
            list.Add(new LanguageOption(lang, lang.NativeName));
        }

        return list;
    }
}
