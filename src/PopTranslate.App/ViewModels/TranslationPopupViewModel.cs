using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using PopTranslate.App.Services;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Language;
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
    private const int MaxHistoryEntries = 30;

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
    private readonly ISmartInputClassifier _smartInputClassifier;

    private DispatcherQueue? _dispatcher;
    private CancellationTokenSource? _activeTranslationCts;
    private CancellationTokenSource? _ocrRerunCts;
    private CancellationTokenSource? _ttsCts;
    private string? _speakingCardId;
    private readonly Dictionary<string, CancellationTokenSource> _singleRetryCtsMap = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _backTranslateCtsMap = new(StringComparer.Ordinal);
    private readonly HashSet<string> _autoBackTranslatedCardIds = new(StringComparer.Ordinal);
    private readonly List<TranslationHistoryRecord> _historyRecords = [];
    private readonly Lock _historyLock = new();

    private bool _isSuppressingLanguageReaction;
    private bool _prioritizePolishInCurrentSession;
    private bool _isPopupVisible = true;
    private bool _wasExplicitlyCleared;
    private int _openFlyoutCount;
    private LanguageCode _lastDetectedSource = LanguageCode.Auto;
    private LanguageCode _lastEffectiveTarget = LanguageCode.ChineseSimplified;
    private bool _lastRequestWasFromOcr;
    private bool _autoCopiedInCurrentRun;
    private bool _lastSettingPinByDefault;
    private bool _lastSettingMergeLineBreaks = true;
    private OcrPromptMode _lastSettingOcrPromptMode = OcrPromptMode.PlainText;
    private CapturedScreenshot? _lastOcrScreenshot;
    private SessionCacheKey? _activeSessionKey;
    private SessionCacheKey? _displayedSessionKey;
    private Task? _activeSessionTask;
    private string? _pendingGuardText;
    private bool _pendingGuardIsFromOcr;
    private bool _pendingGuardPrioritizePolish;
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
        ITtsService ttsService,
        ISmartInputClassifier? smartInputClassifier = null)
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
        _smartInputClassifier = smartInputClassifier ?? new SmartInputClassifier();

        SourceLanguageOptions = BuildSourceLanguageOptions();
        TargetLanguageOptions = BuildTargetLanguageOptions(AppSettings.CreateDefault());
        OcrPromptModeOptions = new ObservableCollection<OcrPromptModeOption>(OcrPromptModeOption.CreatePresets());

        _isSuppressingLanguageReaction = true;
        try
        {
            SelectedSourceLanguage = SourceLanguageOptions[0];
            SelectedTargetLanguage = TargetLanguageOptions[0];
            SelectedOcrPromptMode = OcrPromptModeOptions[0];
            MergeLineBreaks = true;
        }
        finally
        {
            _isSuppressingLanguageReaction = false;
        }
    }

    public event EventHandler? ContentLayoutChanged;
    public event EventHandler<bool>? PinStateChanged;
    public event EventHandler? HidePopupRequested;

    public ObservableCollection<LanguageOption> SourceLanguageOptions { get; }

    public ObservableCollection<LanguageOption> TargetLanguageOptions { get; }

    public ObservableCollection<OcrPromptModeOption> OcrPromptModeOptions { get; }

    public ObservableCollection<ServiceCardViewModel> ServiceCards { get; } = [];

    public ObservableCollection<TranslationHistoryItemView> HistoryEntries { get; } = [];

    public bool HasHistory => HistoryEntries.Count > 0;

    public bool ShowRestoreLastHistoryHint => ShowEmptyHint && HasHistory;

    public string RestoreLastHistoryLabel => HistoryEntries.Count > 0
        ? $"恢复上次：{HistoryEntries[0].PreviewText}"
        : string.Empty;

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
    [NotifyPropertyChangedFor(nameof(ShowRestoreLastHistoryHint))]
    [NotifyPropertyChangedFor(nameof(ShowCollapsedSourceBar))]
    [NotifyPropertyChangedFor(nameof(ShowExpandedSourceCard))]
    public partial bool IsOcrLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusBanner))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyHint))]
    [NotifyPropertyChangedFor(nameof(ShowRestoreLastHistoryHint))]
    public partial string? StatusBannerText { get; set; }

    [ObservableProperty]
    public partial bool IsStatusError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoEnabledProvidersHint))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyHint))]
    [NotifyPropertyChangedFor(nameof(ShowRestoreLastHistoryHint))]
    public partial bool HasEnabledProviders { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceSpeakGlyph))]
    [NotifyPropertyChangedFor(nameof(SourceSpeakTooltip))]
    public partial bool IsSpeakingSource { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingInputGuard))]
    [NotifyPropertyChangedFor(nameof(PendingGuardTitle))]
    [NotifyPropertyChangedFor(nameof(PendingGuardDescription))]
    [NotifyPropertyChangedFor(nameof(PendingGuardPrimaryActionText))]
    [NotifyPropertyChangedFor(nameof(ShowGuardExplainAction))]
    [NotifyPropertyChangedFor(nameof(PendingGuardExplainActionText))]
    [NotifyPropertyChangedFor(nameof(ShowGuardCopyOriginalAction))]
    [NotifyPropertyChangedFor(nameof(ShowGuardEditSourceAction))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyHint))]
    [NotifyPropertyChangedFor(nameof(ShowRestoreLastHistoryHint))]
    public partial InputGuardReason PendingGuardReason { get; set; } = InputGuardReason.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PendingGuardTitle))]
    [NotifyPropertyChangedFor(nameof(PendingGuardDescription))]
    public partial int PendingGuardCharacterCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGuardExplainAction))]
    public partial bool HasEligibleAiExplainProvider { get; set; }

    public bool HasPendingInputGuard => PendingGuardReason != InputGuardReason.None;

    public bool ShowGuardExplainAction =>
        HasPendingInputGuard &&
        HasEligibleAiExplainProvider &&
        PendingGuardReason is InputGuardReason.CodeSnippet or InputGuardReason.JsonOrMarkup or InputGuardReason.StackTrace;

    public bool ShowGuardCopyOriginalAction =>
        PendingGuardReason is InputGuardReason.UrlOrEmail or InputGuardReason.FilePath;

    public bool ShowGuardEditSourceAction => PendingGuardReason == InputGuardReason.LongText;

    public string PendingGuardTitle => PendingGuardReason switch
    {
        InputGuardReason.LongText => $"检测到长文本（{PendingGuardCharacterCount:N0} 字）",
        InputGuardReason.UrlOrEmail => "检测到网址或邮箱",
        InputGuardReason.FilePath => "检测到文件路径",
        InputGuardReason.JsonOrMarkup => "检测到结构化数据 (JSON / XML)",
        InputGuardReason.StackTrace => "检测到报错日志 / 堆栈",
        InputGuardReason.CodeSnippet => "检测到代码片段 / 命令",
        _ => string.Empty,
    };

    public string PendingGuardDescription => PendingGuardReason switch
    {
        InputGuardReason.LongText => "为避免误选整页消耗大量 Token，已暂停自动请求；确认后立即执行。",
        InputGuardReason.UrlOrEmail => "直接翻译链接通常会破坏原始地址，已跳过自动翻译。",
        InputGuardReason.FilePath => "直接翻译路径会破坏目录结构，已跳过自动翻译。",
        InputGuardReason.JsonOrMarkup => "普通翻译可能破坏字段键名，可由 AI 解释结构含义或强制翻译。",
        InputGuardReason.StackTrace => "可由 AI 直接分析报错原因与排查方向，或强制普通翻译。",
        InputGuardReason.CodeSnippet => "普通翻译容易破坏代码语法，可由 AI 解释逻辑或强制翻译。",
        _ => string.Empty,
    };

    public string PendingGuardPrimaryActionText => PendingGuardReason switch
    {
        InputGuardReason.LongText => _pendingGuardPrioritizePolish ? "确认继续润色" : "确认继续翻译",
        InputGuardReason.CodeSnippet or InputGuardReason.JsonOrMarkup or InputGuardReason.StackTrace => "仍要普通翻译",
        InputGuardReason.UrlOrEmail or InputGuardReason.FilePath => "仍要翻译",
        _ => "继续翻译",
    };

    public string PendingGuardExplainActionText => PendingGuardReason switch
    {
        InputGuardReason.StackTrace => "✨ AI 分析报错",
        InputGuardReason.JsonOrMarkup => "✨ AI 解释结构",
        _ => "✨ AI 解释代码",
    };

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

    public bool ShowEmptyHint => ServiceCards.Count == 0 && !IsOcrLoading && !HasStatusBanner && !HasPendingInputGuard && HasEnabledProviders;

    public string PinGlyph => IsPinned ? "\uE840" : "\uE718";

    public string PinTooltip => IsPinned ? "取消置顶（恢复失焦自动隐藏，Ctrl+P）" : "置顶弹窗（取消失焦自动隐藏，Ctrl+P）";

    public void AttachDispatcher(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void SetPopupVisible(bool isVisible)
    {
        _isPopupVisible = isVisible;
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
        ClearPendingInputGuard();
        _lastRequestWasFromOcr = false;
        _prioritizePolishInCurrentSession = prioritizePolish;
        _lastDetectedSource = LanguageCode.Auto;
        ClearOcrContext();

        IsSourceCardCollapsed = collapseSourceCard;
        SourceText = string.Empty;
        _displayedSessionKey = null;
        ServiceCards.Clear();
        IsOcrLoading = false;
        StatusBannerText = initialHint;
        IsStatusError = false;
        RefreshProviderAvailability();
        OnPropertyChanged(nameof(SourceLanguageButtonText));
        NotifyEmptyAndHistoryHintsChanged();
        NotifyLayoutChanged();
    }

    /// <summary>
    /// Attempts to resume the last active, completed, or guarded popup session without triggering
    /// any new network requests (used when clicking the taskbar/tray icon or triggering selection
    /// hotkey with no new text selected).
    /// </summary>
    public bool TryResumeLastSession(bool prioritizePolish = false)
    {
        if (_wasExplicitlyCleared)
        {
            return false;
        }

        var hasLiveCards = ServiceCards.Count > 0 &&
                           HasSourceText &&
                           (_activeTranslationCts is { IsCancellationRequested: false } ||
                            ServiceCards.Any(c => c.HasText || c.IsDeferredUntilExpanded || c.IsWaitingOnDemand));

        if (hasLiveCards)
        {
            if (prioritizePolish)
            {
                _prioritizePolishInCurrentSession = true;
                _ = TriggerAllPolishCardsAsync();
            }

            NotifyLayoutChanged();
            return true;
        }

        if (HasPendingInputGuard && HasSourceText)
        {
            NotifyLayoutChanged();
            return true;
        }

        if (HasHistory)
        {
            RestoreHistoryEntry(HistoryEntries[0].Id, prioritizePolish);
            return true;
        }

        return false;
    }

    public void PrepareForPolishInput(string? initialHint = null) =>
        PrepareForInput(initialHint, prioritizePolish: true, collapseSourceCard: false);

    public void BeginOcrCapture(CapturedScreenshot? screenshot = null)
    {
        CancelActiveTranslation();
        ClearPendingInputGuard();
        _wasExplicitlyCleared = false;
        _lastRequestWasFromOcr = true;
        _prioritizePolishInCurrentSession = false;
        _lastOcrScreenshot = screenshot;
        _lastDetectedSource = LanguageCode.Auto;

        IsSourceCardCollapsed = true;
        SourceText = string.Empty;
        _displayedSessionKey = null;
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
        NotifyEmptyAndHistoryHintsChanged();
        NotifyLayoutChanged();
    }

    public void ReleaseOcrRawPixels()
    {
        if (_lastOcrScreenshot is { RawBgraBytes: not null } screenshot)
        {
            _lastOcrScreenshot = screenshot with { RawBgraBytes = null };
        }
    }

    public void ReleaseIdleMemory()
    {
        ClearOcrContext();
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

        NotifyEmptyAndHistoryHintsChanged();
        NotifyLayoutChanged();
    }

    public Task TranslateTextAsync(
        string rawText,
        bool isFromOcr = false,
        bool resetLanguageSelection = true,
        bool prioritizePolish = false,
        bool collapseSourceCard = true)
    {
        _wasExplicitlyCleared = false;
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

        var rawTrimmed = rawText?.Trim() ?? string.Empty;
        var smartClassification = _settingsStore.Current.EnableSmartInputGuard && !prioritizePolish
            ? _smartInputClassifier.Classify(rawTrimmed)
            : new SmartInputClassification(SmartInputKind.Prose, string.Empty);

        var mergeBreaks = MergeLineBreaks &&
                          (!isFromOcr || SelectedOcrPromptMode.Mode == OcrPromptMode.PlainText);
        // Preserve original line breaks & indentation for code snippets, stack traces, JSON/XML, URLs, and file paths.
        var preprocessed = smartClassification.Kind != SmartInputKind.Prose
            ? rawTrimmed
            : PreprocessInputText(rawText ?? string.Empty, mergeBreaks, isFromOcr);
        SourceText = preprocessed;

        if (string.IsNullOrWhiteSpace(preprocessed))
        {
            IsSourceCardCollapsed = false;
            CancelActiveTranslation();
            ClearPendingInputGuard();
            _displayedSessionKey = null;
            ServiceCards.Clear();
            _lastDetectedSource = LanguageCode.Auto;
            RefreshProviderAvailability();
            OnPropertyChanged(nameof(SourceLanguageButtonText));
            NotifyEmptyAndHistoryHintsChanged();
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
            .Where(c => c.IsPolishMode && (c.IsWaitingOnDemand || c.IsDeferredUntilExpanded))
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

    public void RestoreHistoryEntry(Guid entryId, bool prioritizePolish = false)
    {
        TranslationHistoryRecord? record;
        lock (_historyLock)
        {
            record = _historyRecords.FirstOrDefault(r => r.Id == entryId);
        }

        if (record is null)
        {
            return;
        }

        _wasExplicitlyCleared = false;
        CancelActiveTranslation();
        ClearPendingInputGuard();
        ClearOcrContext();
        _lastRequestWasFromOcr = false;
        _prioritizePolishInCurrentSession = prioritizePolish;

        _isSuppressingLanguageReaction = true;
        try
        {
            SelectedSourceLanguage = SourceLanguageOptions.FirstOrDefault(x => x.Code == record.SourceLanguage)
                ?? SourceLanguageOptions[0];
            SelectedTargetLanguage = TargetLanguageOptions.FirstOrDefault(x => x.Code == record.TargetLanguage)
                ?? TargetLanguageOptions[0];
            MergeLineBreaks = record.MergeLineBreaks;
        }
        finally
        {
            _isSuppressingLanguageReaction = false;
        }

        SourceText = record.SourceText;
        IsSourceCardCollapsed = true;
        _ = ExecuteUnifiedAsync(record.SourceText, isFromOcr: false, prioritizePolish: prioritizePolish, bypassCache: false, bypassInputGuards: true);
    }

    [RelayCommand]
    public async Task ConfirmPendingGuardAsync()
    {
        var text = _pendingGuardText ?? SourceText?.Trim();
        var isFromOcr = _pendingGuardIsFromOcr;
        var prioritizePolish = _pendingGuardPrioritizePolish;
        ClearPendingInputGuard();

        if (string.IsNullOrWhiteSpace(text))
        {
            NotifyEmptyAndHistoryHintsChanged();
            NotifyLayoutChanged();
            return;
        }

        _wasExplicitlyCleared = false;
        await ExecuteUnifiedAsync(text, isFromOcr, prioritizePolish, bypassCache: false, bypassInputGuards: true);
    }

    [RelayCommand]
    public async Task ExplainPendingGuardWithAiAsync()
    {
        var text = _pendingGuardText ?? SourceText?.Trim();
        var reason = PendingGuardReason;
        ClearPendingInputGuard();

        if (string.IsNullOrWhiteSpace(text))
        {
            NotifyEmptyAndHistoryHintsChanged();
            NotifyLayoutChanged();
            return;
        }

        var settings = _settingsStore.Current;
        var provider = FindBestAiExplainProvider(settings);
        if (provider is null)
        {
            ShowStatusError("尚未配置可用的 OpenAI 兼容大模型，请先前往「设置 → 翻译服务」启用后再使用 AI 解释。");
            return;
        }

        _wasExplicitlyCleared = false;
        CancelActiveTranslation();
        _autoCopiedInCurrentRun = false;
        StatusBannerText = null;
        IsStatusError = false;
        ServiceCards.Clear();
        _displayedSessionKey = null;

        var routing = _languageRouter.Resolve(
            text,
            SelectedSourceLanguage.Code,
            SelectedTargetLanguage.Code,
            settings.PrimaryTargetLanguage,
            settings.SecondaryTargetLanguage);

        _lastDetectedSource = routing.DetectedSourceLanguage;
        _lastEffectiveTarget = settings.PrimaryTargetLanguage;
        OnPropertyChanged(nameof(SourceLanguageButtonText));
        OnPropertyChanged(nameof(TargetLanguageButtonText));

        var explainCard = CreatePolishCard(
            cardId: $"{provider.Id}::ai-explain",
            config: provider,
            sourceText: text,
            initialStyle: PolishStyle.Custom,
            isWaitingOnDemand: false,
            showDiffByDefault: false,
            detectedLanguage: settings.PrimaryTargetLanguage,
            isDeferredUntilExpanded: false);
        explainCard.IsCollapsed = false;
        explainCard.ShowDiff = false;
        explainCard.StatusText = "AI 解释中...";

        ServiceCards.Add(explainCard);
        HasEnabledProviders = true;
        OnPropertyChanged(nameof(ShowNoEnabledProvidersHint));
        NotifyEmptyAndHistoryHintsChanged();
        NotifyLayoutChanged();

        var explainPrompt = BuildAiExplainPrompt(reason, settings.PrimaryTargetLanguage);
        var request = new PolishRequest(
            Text: text,
            Style: PolishStyle.Custom,
            CustomPrompt: explainPrompt,
            PreferredLanguage: settings.PrimaryTargetLanguage,
            ApplyTextPreprocessing: false,
            ProviderInstanceIds: [provider.Id]);

        var cts = new CancellationTokenSource();
        _activeTranslationCts = cts;
        var ct = cts.Token;

        try
        {
            await ConsumeCoalescedStreamAsync(
                _polishOrchestrator.PolishParallelAsync(request, ct),
                update =>
                {
                    if (string.Equals(update.ProviderInstanceId, provider.Id, StringComparison.Ordinal))
                    {
                        explainCard.ApplyUpdate(update);
                        explainCard.ShowDiff = false;
                    }
                },
                ct);
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
                    explainCard.ApplyUpdate(new ProviderStreamUpdate(
                        provider.Id,
                        explainCard.DisplayName,
                        explainCard.ProviderKind,
                        ProviderStreamState.Failed,
                        string.Empty,
                        explainCard.TranslatedText,
                        null,
                        settings.PrimaryTargetLanguage,
                        ex.Message,
                        TimeSpan.Zero));
                }
            });
        }
        finally
        {
            if (ReferenceEquals(_activeTranslationCts, cts))
            {
                _activeTranslationCts = null;
                cts.Dispose();
            }
        }
    }

    [RelayCommand]
    private void CopyGuardedSource()
    {
        if (!string.IsNullOrEmpty(SourceText))
        {
            ClipboardLoopGuard.TryCopyText(SourceText);
        }
    }

    [RelayCommand]
    public void RestoreLastHistory()
    {
        if (HistoryEntries.Count > 0)
        {
            RestoreHistoryEntry(HistoryEntries[0].Id);
        }
    }

    [RelayCommand]
    public void ClearHistory()
    {
        lock (_historyLock)
        {
            _historyRecords.Clear();
            HistoryEntries.Clear();
        }

        NotifyEmptyAndHistoryHintsChanged();
        NotifyLayoutChanged();
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
    public Task RetranslateAsync() => RetranslateCoreAsync(allowBypassIfSameDisplayed: true);

    private async Task RetranslateCoreAsync(bool allowBypassIfSameDisplayed)
    {
        var text = SourceText?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            CancelActiveTranslation();
            _displayedSessionKey = null;
            ServiceCards.Clear();
            _lastDetectedSource = LanguageCode.Auto;
            RefreshProviderAvailability();
            OnPropertyChanged(nameof(SourceLanguageButtonText));
            NotifyEmptyAndHistoryHintsChanged();
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

        var settings = _settingsStore.Current;
        var routing = _languageRouter.Resolve(
            processed,
            SelectedSourceLanguage.Code,
            SelectedTargetLanguage.Code,
            settings.PrimaryTargetLanguage,
            settings.SecondaryTargetLanguage);
        var candidateKey = BuildSessionCacheKey(processed, SelectedSourceLanguage.Code, routing.TargetLanguage, MergeLineBreaks);
        var bypassCache = allowBypassIfSameDisplayed &&
                          ServiceCards.Count > 0 &&
                          _displayedSessionKey == candidateKey &&
                          _activeTranslationCts is null;

        _wasExplicitlyCleared = false;
        await ExecuteUnifiedAsync(
            processed,
            isOcrFlow,
            _prioritizePolishInCurrentSession,
            bypassCache: bypassCache,
            bypassInputGuards: allowBypassIfSameDisplayed);
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
        _displayedSessionKey = null;
        IsOcrLoading = true;
        IsStatusError = false;
        StatusBannerText = $"正在按「{SelectedOcrPromptMode.DisplayLabel}」模式重新识别...";
        NotifyEmptyAndHistoryHintsChanged();
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
        ClearPendingInputGuard();
        _wasExplicitlyCleared = true;
        SourceText = string.Empty;
        _displayedSessionKey = null;
        _lastDetectedSource = LanguageCode.Auto;
        StatusBannerText = null;
        IsStatusError = false;
        ServiceCards.Clear();
        OnPropertyChanged(nameof(SourceLanguageButtonText));
        NotifyEmptyAndHistoryHintsChanged();
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

        if (_ocrRerunCts is { } ocrCts)
        {
            _ocrRerunCts = null;
            try
            {
                ocrCts.Cancel();
            }
            catch
            {
                // Ignore cancellation race.
            }

            ocrCts.Dispose();
        }

        if (_activeTranslationCts is { } activeCts)
        {
            _activeTranslationCts = null;
            try
            {
                activeCts.Cancel();
            }
            catch
            {
                // Ignore cancellation race.
            }

            activeCts.Dispose();
        }

        CancelAllCtsInMap(_singleRetryCtsMap);
        CancelAllCtsInMap(_backTranslateCtsMap);
        _autoBackTranslatedCardIds.Clear();
        _activeSessionKey = null;
        _activeSessionTask = null;
    }

    partial void OnSelectedSourceLanguageChanged(LanguageOption value)
    {
        if (_isSuppressingLanguageReaction || !HasSourceText)
        {
            return;
        }

        _ = RetranslateCoreAsync(allowBypassIfSameDisplayed: false);
    }

    partial void OnSelectedTargetLanguageChanged(LanguageOption value)
    {
        if (_isSuppressingLanguageReaction || !HasSourceText)
        {
            return;
        }

        _ = RetranslateCoreAsync(allowBypassIfSameDisplayed: false);
    }

    partial void OnMergeLineBreaksChanged(bool value)
    {
        if (_isSuppressingLanguageReaction || !HasSourceText)
        {
            return;
        }

        _ = RetranslateCoreAsync(allowBypassIfSameDisplayed: false);
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
            if (HasOcrContext && ReferenceEquals(_lastOcrScreenshot?.PngBytes, pngBytes))
            {
                OcrPreviewImage = bitmap;
            }
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

    private async Task ExecuteUnifiedAsync(
        string text,
        bool isFromOcr,
        bool prioritizePolish,
        bool bypassCache = false,
        bool bypassInputGuards = false)
    {
        var settings = _settingsStore.Current;
        var routing = _languageRouter.Resolve(
            text,
            SelectedSourceLanguage.Code,
            SelectedTargetLanguage.Code,
            settings.PrimaryTargetLanguage,
            settings.SecondaryTargetLanguage);

        var sessionKey = BuildSessionCacheKey(text, SelectedSourceLanguage.Code, routing.TargetLanguage, MergeLineBreaks);

        var enabledProviders = settings.Providers
            .Where(p => p.Enabled)
            .OrderBy(p => p.SortOrder)
            .ToList();

        // If the exact same request is ALREADY displayed (completed or streaming in background after accidental focus loss)
        // and the enabled provider roster has not changed, keep the live cards in-place with zero flicker and zero re-upload!
        if (!bypassCache &&
            _displayedSessionKey == sessionKey &&
            ServiceCards.Count > 0 &&
            (!prioritizePolish || ServiceCards.Any(c => c.IsPolishMode)) &&
            HasMatchingEnabledProviders(enabledProviders))
        {
            ClearPendingInputGuard();
            var existingHistory = FindHistoryRecord(sessionKey);
            if (existingHistory is not null)
            {
                PromoteHistoryRecord(existingHistory);
            }

            if (prioritizePolish)
            {
                await TriggerAllPolishCardsAsync();
            }

            if (_activeTranslationCts is not null &&
                !_activeTranslationCts.IsCancellationRequested &&
                _activeSessionKey == sessionKey &&
                _activeSessionTask is { } runningTask)
            {
                await runningTask;
            }

            NotifyLayoutChanged();
            return;
        }

        var cachedRecord = bypassCache ? null : FindHistoryRecord(sessionKey);

        // Input Guards (Item 2: Long-Text Token Guard, Item 5: Smart Non-Prose / Code / URL / Path / StackTrace Guard).
        // Never block inputs that already hit the session history cache (0 API calls anyway).
        if (!bypassInputGuards && cachedRecord is null)
        {
            var guardReason = EvaluateInputGuard(text, settings, prioritizePolish);
            if (guardReason != InputGuardReason.None)
            {
                CancelActiveTranslation();
                ServiceCards.Clear();
                _displayedSessionKey = null;
                _lastDetectedSource = routing.DetectedSourceLanguage;
                _lastEffectiveTarget = routing.TargetLanguage;
                _pendingGuardText = text;
                _pendingGuardIsFromOcr = isFromOcr;
                _pendingGuardPrioritizePolish = prioritizePolish;
                PendingGuardCharacterCount = text.Length;
                HasEligibleAiExplainProvider = FindBestAiExplainProvider(settings) is not null;
                PendingGuardReason = guardReason;
                HasEnabledProviders = HasAnyUsableEnabledProvider(settings);
                OnPropertyChanged(nameof(SourceLanguageButtonText));
                OnPropertyChanged(nameof(TargetLanguageButtonText));
                OnPropertyChanged(nameof(ShowNoEnabledProvidersHint));
                NotifyEmptyAndHistoryHintsChanged();
                NotifyLayoutChanged();
                return;
            }
        }

        ClearPendingInputGuard();
        CancelActiveTranslation();
        _autoCopiedInCurrentRun = false;
        StatusBannerText = null;
        IsStatusError = false;

        ServiceCards.Clear();

        _lastDetectedSource = routing.DetectedSourceLanguage;
        _lastEffectiveTarget = routing.TargetLanguage;
        _displayedSessionKey = sessionKey;
        OnPropertyChanged(nameof(SourceLanguageButtonText));
        OnPropertyChanged(nameof(TargetLanguageButtonText));

        if (cachedRecord is not null)
        {
            PromoteHistoryRecord(cachedRecord);
        }

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
                var cachedPolish = cachedRecord?.TryGetResult(provider.Id, PopupMode.Polish, provider.DefaultPolishStyle);
                if (cachedPolish is not null)
                {
                    var cachedPolishCard = CreatePolishCard(
                        cardId: provider.Id,
                        config: provider,
                        sourceText: text,
                        initialStyle: provider.DefaultPolishStyle,
                        isWaitingOnDemand: false,
                        isDeferredUntilExpanded: false,
                        showDiffByDefault: showDiffByDefault,
                        detectedLanguage: routing.DetectedSourceLanguage);
                    if (prioritizePolish)
                    {
                        cachedPolishCard.IsCollapsed = false;
                    }

                    cachedPolishCard.ApplyCachedResult(
                        cachedPolish.TranslatedText,
                        cachedPolish.DetectedSourceLanguage,
                        cachedPolish.TargetLanguage,
                        cachedPolish.Elapsed);
                    ServiceCards.Add(cachedPolishCard);
                    continue;
                }

                var isOnDemand = provider.TriggerMode == PolishTriggerMode.OnDemand && !prioritizePolish;
                var isDeferred = !isOnDemand && provider.IsCollapsedByDefault && !prioritizePolish;
                var polishCard = CreatePolishCard(
                    cardId: provider.Id,
                    config: provider,
                    sourceText: text,
                    initialStyle: provider.DefaultPolishStyle,
                    isWaitingOnDemand: isOnDemand,
                    isDeferredUntilExpanded: isDeferred,
                    showDiffByDefault: showDiffByDefault,
                    detectedLanguage: routing.DetectedSourceLanguage);

                if (prioritizePolish)
                {
                    polishCard.IsCollapsed = false;
                }

                ServiceCards.Add(polishCard);
                if (!isOnDemand && !isDeferred)
                {
                    polishCardsToRun.Add((polishCard, provider));
                }
            }
            else
            {
                var cachedTranslate = cachedRecord?.TryGetResult(provider.Id, PopupMode.Translate, PolishStyle.General);
                if (cachedTranslate is not null)
                {
                    var cachedTranslateCard = CreateTranslateCard(
                        cardId: provider.Id,
                        config: provider,
                        sourceText: text,
                        detectedSource: routing.DetectedSourceLanguage,
                        targetLanguage: routing.TargetLanguage,
                        isDeferredUntilExpanded: false);
                    cachedTranslateCard.ApplyCachedResult(
                        cachedTranslate.TranslatedText,
                        cachedTranslate.DetectedSourceLanguage,
                        cachedTranslate.TargetLanguage,
                        cachedTranslate.Elapsed,
                        cachedTranslate.BackTranslatedText,
                        cachedTranslate.BackTranslateTargetLabel);
                    ServiceCards.Add(cachedTranslateCard);
                    continue;
                }

                var isDeferred = provider.IsCollapsedByDefault;
                var translateCard = CreateTranslateCard(
                    cardId: provider.Id,
                    config: provider,
                    sourceText: text,
                    detectedSource: routing.DetectedSourceLanguage,
                    targetLanguage: routing.TargetLanguage,
                    isDeferredUntilExpanded: isDeferred);

                ServiceCards.Add(translateCard);
                if (!isDeferred)
                {
                    translateCardsByProviderId[provider.Id] = translateCard;
                }
            }
        }

        // Fallback: If invoked via Alt+Q and the user has 0 enabled Role == ProviderRole.Polish providers,
        // temporarily append/run any enabled OpenAiCompatible provider as a Polish card so Alt+Q works out-of-the-box!
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

                var cachedFallback = cachedRecord?.TryGetResult(fallbackProvider.Id, PopupMode.Polish, fallbackStyle);
                var fallbackCard = CreatePolishCard(
                    cardId: $"{fallbackProvider.Id}::polish-fallback",
                    config: fallbackProvider,
                    sourceText: text,
                    initialStyle: fallbackStyle,
                    isWaitingOnDemand: false,
                    isDeferredUntilExpanded: false,
                    showDiffByDefault: showDiffByDefault,
                    detectedLanguage: routing.DetectedSourceLanguage);
                fallbackCard.IsCollapsed = false;

                ServiceCards.Add(fallbackCard);
                polishCardCount++;

                if (cachedFallback is not null)
                {
                    fallbackCard.ApplyCachedResult(
                        cachedFallback.TranslatedText,
                        cachedFallback.DetectedSourceLanguage,
                        cachedFallback.TargetLanguage,
                        cachedFallback.Elapsed);
                }
                else
                {
                    polishCardsToRun.Add((fallbackCard, fallbackProvider));
                }
            }

            if (polishCardCount == 0 && ServiceCards.Count > 0)
            {
                StatusBannerText = "尚未启用 OpenAI 兼容大模型，前往「设置 → 翻译服务」添加即可使用 AI 润色。";
                IsStatusError = false;
            }
        }

        HasEnabledProviders = ServiceCards.Count > 0;
        OnPropertyChanged(nameof(ShowNoEnabledProvidersHint));
        NotifyEmptyAndHistoryHintsChanged();
        NotifyLayoutChanged();

        if (ServiceCards.Count == 0 || (translateCardsByProviderId.Count == 0 && polishCardsToRun.Count == 0))
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _activeTranslationCts = cts;
        _activeSessionKey = sessionKey;
        var ct = cts.Token;

        var parallelTasks = new List<Task>(1 + polishCardsToRun.Count);

        if (translateCardsByProviderId.Count > 0)
        {
            parallelTasks.Add(StreamTranslateCardsBatchAsync(
                sessionKey,
                text,
                isFromOcr,
                routing.DetectedSourceLanguage,
                routing.TargetLanguage,
                settings,
                translateCardsByProviderId,
                preferPolishForAutoCopy: prioritizePolish && polishCardsToRun.Count > 0,
                ct));
        }

        foreach (var (polishCard, config) in polishCardsToRun)
        {
            parallelTasks.Add(StreamPolishCardInternalAsync(
                sessionKey,
                polishCard,
                config,
                text,
                routing.DetectedSourceLanguage,
                routing.TargetLanguage,
                settings,
                allowAutoCopy: true,
                ct));
        }

        var combinedTask = parallelTasks.Count > 0 ? Task.WhenAll(parallelTasks) : Task.CompletedTask;
        _activeSessionTask = combinedTask;
        try
        {
            await combinedTask;
        }
        finally
        {
            if (ReferenceEquals(_activeTranslationCts, cts))
            {
                _activeTranslationCts = null;
                _activeSessionKey = null;
                _activeSessionTask = null;
                cts.Dispose();
            }
        }
    }

    private ServiceCardViewModel CreateTranslateCard(
        string cardId,
        ProviderInstanceConfig config,
        string sourceText,
        LanguageCode detectedSource,
        LanguageCode targetLanguage,
        bool isDeferredUntilExpanded = false)
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
            flyoutClosedCallback: NotifyInternalFlyoutClosed,
            isDeferredUntilExpanded: isDeferredUntilExpanded)
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
        LanguageCode detectedLanguage,
        bool isDeferredUntilExpanded = false)
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
            flyoutClosedCallback: NotifyInternalFlyoutClosed,
            isDeferredUntilExpanded: isDeferredUntilExpanded)
        {
            DetectedSourceLanguage = detectedLanguage,
            TargetLanguage = detectedLanguage,
        };
    }

    private async Task StreamTranslateCardsBatchAsync(
        SessionCacheKey sessionKey,
        string text,
        bool isFromOcr,
        LanguageCode initialDetectedSource,
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
            await ConsumeCoalescedStreamAsync(
                _translationOrchestrator.TranslateParallelAsync(request, ct),
                update =>
                {
                    translateCardsByProviderId.TryGetValue(update.ProviderInstanceId, out var card);
                    if (card is not null)
                    {
                        card.ApplyUpdate(update);

                        if (update.State == ProviderStreamState.Completed &&
                            !string.IsNullOrWhiteSpace(card.TranslatedText))
                        {
                            RecordCompletedCardInHistory(
                                sessionKey,
                                update.DetectedSourceLanguage is { IsAuto: false } d ? d : initialDetectedSource,
                                targetLanguage,
                                card,
                                update.Elapsed);

                            if (settings.AutoBackTranslate &&
                                _autoBackTranslatedCardIds.Add(card.CardId))
                            {
                                _ = BackTranslateCardAsync(card.CardId, ct, expandCard: false);
                            }
                        }
                    }

                    if (update.DetectedSourceLanguage is { IsAuto: false } detected)
                    {
                        _lastDetectedSource = detected;
                        OnPropertyChanged(nameof(SourceLanguageButtonText));
                    }

                    if (_isPopupVisible &&
                        update.State == ProviderStreamState.Completed &&
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
                },
                ct);
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
        SessionCacheKey sessionKey,
        ServiceCardViewModel card,
        ProviderInstanceConfig? config,
        string text,
        LanguageCode detectedLanguage,
        LanguageCode effectiveTargetLanguage,
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
            await ConsumeCoalescedStreamAsync(
                _polishOrchestrator.PolishParallelAsync(request, ct),
                update =>
                {
                    if (string.Equals(update.ProviderInstanceId, card.ProviderInstanceId, StringComparison.Ordinal))
                    {
                        card.ApplyUpdate(update);

                        if (update.State == ProviderStreamState.Completed &&
                            !string.IsNullOrWhiteSpace(card.TranslatedText))
                        {
                            RecordCompletedCardInHistory(
                                sessionKey,
                                update.DetectedSourceLanguage is { IsAuto: false } d ? d : detectedLanguage,
                                effectiveTargetLanguage,
                                card,
                                update.Elapsed);
                        }
                    }

                    if (update.DetectedSourceLanguage is { IsAuto: false } detected)
                    {
                        _lastDetectedSource = detected;
                        OnPropertyChanged(nameof(SourceLanguageButtonText));
                    }

                    if (_isPopupVisible &&
                        allowAutoCopy &&
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
                },
                ct);
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
        var sessionKey = BuildSessionCacheKey(text, SelectedSourceLanguage.Code, routing.TargetLanguage, MergeLineBreaks);

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
            await ConsumeCoalescedStreamAsync(
                _translationOrchestrator.TranslateParallelAsync(request, ct),
                update =>
                {
                    if (string.Equals(update.ProviderInstanceId, card.ProviderInstanceId, StringComparison.Ordinal))
                    {
                        card.ApplyUpdate(update);

                        if (update.State == ProviderStreamState.Completed &&
                            !string.IsNullOrWhiteSpace(card.TranslatedText))
                        {
                            RecordCompletedCardInHistory(
                                sessionKey,
                                update.DetectedSourceLanguage is { IsAuto: false } d ? d : routing.DetectedSourceLanguage,
                                routing.TargetLanguage,
                                card,
                                update.Elapsed);

                            if (settings.AutoBackTranslate && !autoBackTriggered)
                            {
                                autoBackTriggered = true;
                                _ = BackTranslateCardAsync(cardId, ct, expandCard: false);
                            }
                        }
                    }
                },
                ct);
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
        var routing = _languageRouter.Resolve(
            text,
            SelectedSourceLanguage.Code,
            SelectedTargetLanguage.Code,
            settings.PrimaryTargetLanguage,
            settings.SecondaryTargetLanguage);
        var sessionKey = BuildSessionCacheKey(text, SelectedSourceLanguage.Code, routing.TargetLanguage, MergeLineBreaks);
        var parentCt = _activeTranslationCts?.Token ?? CancellationToken.None;

        await StreamPolishCardInternalAsync(
            sessionKey,
            card,
            config,
            text,
            routing.DetectedSourceLanguage,
            routing.TargetLanguage,
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

                        if (update.State == ProviderStreamState.Completed &&
                            !string.IsNullOrWhiteSpace(card.BackTranslatedText) &&
                            _displayedSessionKey is { } key)
                        {
                            RecordCompletedBackTranslationInHistory(key, card);
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

    private InputGuardReason EvaluateInputGuard(string text, AppSettings settings, bool prioritizePolish)
    {
        if (settings.EnableLongTextGuard)
        {
            var threshold = Math.Max(100, settings.LongTextGuardThreshold);
            if (text.Length > threshold)
            {
                return InputGuardReason.LongText;
            }
        }

        if (settings.EnableSmartInputGuard && !prioritizePolish)
        {
            var classification = _smartInputClassifier.Classify(text);
            return classification.Kind switch
            {
                SmartInputKind.UrlOrEmail => InputGuardReason.UrlOrEmail,
                SmartInputKind.FilePath => InputGuardReason.FilePath,
                SmartInputKind.JsonOrMarkup => InputGuardReason.JsonOrMarkup,
                SmartInputKind.StackTrace => InputGuardReason.StackTrace,
                SmartInputKind.CodeSnippet => InputGuardReason.CodeSnippet,
                _ => InputGuardReason.None,
            };
        }

        return InputGuardReason.None;
    }

    private void ClearPendingInputGuard()
    {
        _pendingGuardText = null;
        _pendingGuardIsFromOcr = false;
        _pendingGuardPrioritizePolish = false;
        PendingGuardCharacterCount = 0;
        HasEligibleAiExplainProvider = false;
        PendingGuardReason = InputGuardReason.None;
    }

    private ProviderInstanceConfig? FindBestAiExplainProvider(AppSettings settings)
    {
        var enabledLlm = settings.Providers
            .Where(p => p.Enabled && p.Kind == ProviderKind.OpenAiCompatible)
            .OrderBy(p => p.SortOrder)
            .FirstOrDefault();
        if (enabledLlm is not null)
        {
            return enabledLlm;
        }

        return SafeGetEligiblePolishProviders().FirstOrDefault();
    }

    private static string BuildAiExplainPrompt(InputGuardReason reason, LanguageCode targetLanguage)
    {
        var langName = targetLanguage.IsAuto
            ? LanguageCode.ChineseSimplified.DisplayName
            : targetLanguage.DisplayName;

        return reason switch
        {
            InputGuardReason.StackTrace =>
                $"You are a senior software engineer. Analyze the user's error log or stack trace concisely in {langName}. " +
                "Provide: 1) Root cause summary (1-2 sentences), 2) Key failing location/symbol, 3) Concrete troubleshooting steps. " +
                "Keep code identifiers and file paths verbatim; do not translate variable or class names.",

            InputGuardReason.JsonOrMarkup =>
                $"You are a senior software engineer. Briefly explain this JSON/XML/XAML payload in {langName}. " +
                "Summarize its purpose and the meaning of key fields cleanly using bullet points. Keep original key names verbatim.",

            _ =>
                $"You are a senior software engineer. Concisely explain what the user's code snippet or command does in {langName}. " +
                "Highlight key behavior, parameters/flags, and any notable side effects or edge cases. Keep code identifiers verbatim.",
        };
    }

    private bool HasMatchingEnabledProviders(IReadOnlyList<ProviderInstanceConfig> enabledProviders)
    {
        var expected = enabledProviders
            .Where(p => p.Role == ProviderRole.Translate || p.Kind == ProviderKind.OpenAiCompatible)
            .ToList();
        var primaryCards = ServiceCards
            .Where(c => !c.CardId.EndsWith("::polish-fallback", StringComparison.Ordinal))
            .ToList();

        if (expected.Count != primaryCards.Count)
        {
            return false;
        }

        for (var i = 0; i < expected.Count; i++)
        {
            var expectedMode = expected[i].Role == ProviderRole.Polish ? PopupMode.Polish : PopupMode.Translate;
            if (!string.Equals(expected[i].Id, primaryCards[i].ProviderInstanceId, StringComparison.Ordinal) ||
                expectedMode != primaryCards[i].Mode)
            {
                return false;
            }
        }

        return true;
    }

    private void NotifyEmptyAndHistoryHintsChanged()
    {
        OnPropertyChanged(nameof(ShowEmptyHint));
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(ShowRestoreLastHistoryHint));
        OnPropertyChanged(nameof(RestoreLastHistoryLabel));
    }

    private static SessionCacheKey BuildSessionCacheKey(
        string text,
        LanguageCode sourceLanguage,
        LanguageCode effectiveTargetLanguage,
        bool mergeLineBreaks) =>
        new(text, sourceLanguage.Value, effectiveTargetLanguage.Value, mergeLineBreaks);

    private TranslationHistoryRecord? FindHistoryRecord(SessionCacheKey key)
    {
        lock (_historyLock)
        {
            return _historyRecords.FirstOrDefault(r => r.Key == key);
        }
    }

    private void PromoteHistoryRecord(TranslationHistoryRecord record)
    {
        lock (_historyLock)
        {
            var idx = _historyRecords.IndexOf(record);
            if (idx > 0)
            {
                _historyRecords.RemoveAt(idx);
                _historyRecords.Insert(0, record);
            }

            record.Timestamp = DateTimeOffset.Now;
            SyncHistoryEntriesCollectionLocked();
        }

        NotifyEmptyAndHistoryHintsChanged();
    }

    private void RecordCompletedCardInHistory(
        SessionCacheKey sessionKey,
        LanguageCode detectedSource,
        LanguageCode effectiveTarget,
        ServiceCardViewModel card,
        TimeSpan elapsed)
    {
        if (string.IsNullOrWhiteSpace(sessionKey.Text) || string.IsNullOrWhiteSpace(card.TranslatedText))
        {
            return;
        }

        lock (_historyLock)
        {
            var record = _historyRecords.FirstOrDefault(r => r.Key == sessionKey);
            if (record is null)
            {
                record = new TranslationHistoryRecord(
                    Id: Guid.NewGuid(),
                    Key: sessionKey,
                    SourceText: sessionKey.Text,
                    SourceLanguage: SelectedSourceLanguage.Code,
                    TargetLanguage: SelectedTargetLanguage.Code,
                    EffectiveTargetLanguage: effectiveTarget,
                    DetectedSourceLanguage: detectedSource,
                    MergeLineBreaks: sessionKey.MergeLineBreaks);
                _historyRecords.Insert(0, record);
                while (_historyRecords.Count > MaxHistoryEntries)
                {
                    _historyRecords.RemoveAt(_historyRecords.Count - 1);
                }
            }
            else
            {
                if (!detectedSource.IsAuto)
                {
                    record.DetectedSourceLanguage = detectedSource;
                }

                var idx = _historyRecords.IndexOf(record);
                if (idx > 0)
                {
                    _historyRecords.RemoveAt(idx);
                    _historyRecords.Insert(0, record);
                }
            }

            record.Timestamp = DateTimeOffset.Now;
            var styleKey = card.IsPolishMode ? card.CardPolishStyle : PolishStyle.General;
            record.SetResult(
                card.ProviderInstanceId,
                card.Mode,
                styleKey,
                new CachedCardResult(
                    TranslatedText: card.TranslatedText,
                    DetectedSourceLanguage: card.DetectedSourceLanguage,
                    TargetLanguage: card.TargetLanguage,
                    Elapsed: elapsed,
                    BackTranslatedText: card.HasBackTranslatedText ? card.BackTranslatedText : null,
                    BackTranslateTargetLabel: card.BackTranslateTargetLabel));

            SyncHistoryEntriesCollectionLocked();
        }

        NotifyEmptyAndHistoryHintsChanged();
    }

    private void RecordCompletedBackTranslationInHistory(SessionCacheKey sessionKey, ServiceCardViewModel card)
    {
        lock (_historyLock)
        {
            var record = _historyRecords.FirstOrDefault(r => r.Key == sessionKey);
            if (record is null)
            {
                return;
            }

            var existing = record.TryGetResult(card.ProviderInstanceId, PopupMode.Translate, PolishStyle.General);
            if (existing is not null)
            {
                record.SetResult(
                    card.ProviderInstanceId,
                    PopupMode.Translate,
                    PolishStyle.General,
                    existing with
                    {
                        BackTranslatedText = card.BackTranslatedText,
                        BackTranslateTargetLabel = card.BackTranslateTargetLabel,
                    });
            }
        }
    }

    private void SyncHistoryEntriesCollectionLocked()
    {
        HistoryEntries.Clear();
        foreach (var record in _historyRecords)
        {
            var collapsed = record.SourceText
                .Replace("\r\n", " ", StringComparison.Ordinal)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
            var preview = collapsed.Length <= 36 ? collapsed : string.Concat(collapsed.AsSpan(0, 36), "...");
            var srcLabel = !record.DetectedSourceLanguage.IsAuto
                ? FormatShortLanguageLabel(record.DetectedSourceLanguage)
                : (!record.SourceLanguage.IsAuto ? FormatShortLanguageLabel(record.SourceLanguage) : "自动");
            var tgtLabel = FormatShortLanguageLabel(record.EffectiveTargetLanguage);

            HistoryEntries.Add(new TranslationHistoryItemView(
                Id: record.Id,
                SourceText: record.SourceText,
                PreviewText: preview,
                LanguagePairLabel: $"{srcLabel}→{tgtLabel}",
                Timestamp: record.Timestamp,
                CachedResultCount: record.ResultCount));
        }
    }

    private readonly record struct SessionCacheKey(
        string Text,
        string SourceLanguageValue,
        string EffectiveTargetLanguageValue,
        bool MergeLineBreaks);

    private readonly record struct CachedCardKey(
        string ProviderInstanceId,
        PopupMode Mode,
        PolishStyle PolishStyle);

    private sealed record CachedCardResult(
        string TranslatedText,
        LanguageCode? DetectedSourceLanguage,
        LanguageCode TargetLanguage,
        TimeSpan Elapsed,
        string? BackTranslatedText = null,
        string? BackTranslateTargetLabel = null);

    private sealed class TranslationHistoryRecord(
        Guid Id,
        SessionCacheKey Key,
        string SourceText,
        LanguageCode SourceLanguage,
        LanguageCode TargetLanguage,
        LanguageCode EffectiveTargetLanguage,
        LanguageCode DetectedSourceLanguage,
        bool MergeLineBreaks)
    {
        private readonly Dictionary<CachedCardKey, CachedCardResult> _results = [];

        public Guid Id { get; } = Id;
        public SessionCacheKey Key { get; } = Key;
        public string SourceText { get; } = SourceText;
        public LanguageCode SourceLanguage { get; } = SourceLanguage;
        public LanguageCode TargetLanguage { get; } = TargetLanguage;
        public LanguageCode EffectiveTargetLanguage { get; } = EffectiveTargetLanguage;
        public LanguageCode DetectedSourceLanguage { get; set; } = DetectedSourceLanguage;
        public bool MergeLineBreaks { get; } = MergeLineBreaks;
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
        public int ResultCount => _results.Count;

        public CachedCardResult? TryGetResult(string providerId, PopupMode mode, PolishStyle style)
        {
            var normalizedStyle = mode == PopupMode.Polish ? style : PolishStyle.General;
            return _results.TryGetValue(new CachedCardKey(providerId, mode, normalizedStyle), out var value)
                ? value
                : null;
        }

        public void SetResult(string providerId, PopupMode mode, PolishStyle style, CachedCardResult result)
        {
            var normalizedStyle = mode == PopupMode.Polish ? style : PolishStyle.General;
            _results[new CachedCardKey(providerId, mode, normalizedStyle)] = result;
        }
    }

    private void NotifyLayoutChanged()
    {
        ContentLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ConsumeCoalescedStreamAsync(
        IAsyncEnumerable<ProviderStreamUpdate> stream,
        Action<ProviderStreamUpdate> applyOnUi,
        CancellationToken ct)
    {
        var coalescer = new StreamingUpdateCoalescer();
        using var flushTimerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var flushTimerToken = flushTimerCts.Token;
        var drainLock = new Lock();

        void DrainReadyUpdates()
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (_dispatcher is null)
            {
                lock (drainLock)
                {
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    foreach (var ready in coalescer.DrainReady())
                    {
                        applyOnUi(ready);
                    }
                }
            }
            else
            {
                foreach (var ready in coalescer.DrainReady())
                {
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    applyOnUi(ready);
                }
            }
        }

        void ScheduleDelayedFlush()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(coalescer.Window, flushTimerToken).ConfigureAwait(false);
                    var flushResult = coalescer.FlushDue();
                    if (flushResult.HasReadyUpdates && !flushTimerToken.IsCancellationRequested)
                    {
                        EnqueueUi(DrainReadyUpdates);
                    }

                    if (flushResult.ShouldRescheduleDelayedFlush && !flushTimerToken.IsCancellationRequested)
                    {
                        ScheduleDelayedFlush();
                    }
                }
                catch (OperationCanceledException)
                {
                    // Stream finished or was cancelled before delayed flush needed to fire.
                }
            }, CancellationToken.None);
        }

        try
        {
            await foreach (var update in stream.WithCancellation(ct))
            {
                var decision = coalescer.Push(update);
                if (decision.ShouldDrainNow)
                {
                    EnqueueUi(DrainReadyUpdates);
                }
                else if (decision.ShouldScheduleDelayedFlush)
                {
                    ScheduleDelayedFlush();
                }
            }

            try
            {
                flushTimerCts.Cancel();
            }
            catch
            {
                // Ignore cancellation race.
            }

            if (coalescer.FlushAll())
            {
                EnqueueUi(DrainReadyUpdates);
            }
        }
        finally
        {
            try
            {
                flushTimerCts.Cancel();
            }
            catch
            {
                // Ignore cancellation race on cleanup.
            }
        }
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

public readonly record struct CoalescePushResult(
    bool ShouldDrainNow,
    bool ShouldScheduleDelayedFlush);

public readonly record struct CoalesceFlushResult(
    bool HasReadyUpdates,
    bool ShouldRescheduleDelayedFlush);

/// <summary>
/// Coalesces rapid <see cref="ProviderStreamState.Streaming"/> updates per provider card within a short
/// throttle window (default 25ms) to minimize UI layout churn while immediately delivering first-token arrival,
/// reasoning-state transitions, non-streaming lifecycle states, and stream-end flushes without dropping data.
/// </summary>
public sealed class StreamingUpdateCoalescer
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMilliseconds(25);

    private readonly Lock _sync = new();
    private readonly TimeSpan _window;
    private readonly Func<TimeSpan> _clock;
    private readonly Dictionary<string, CardState> _cards = new(StringComparer.Ordinal);
    private readonly Queue<ProviderStreamUpdate> _ready = new();
    private bool _delayedFlushScheduled;

    public StreamingUpdateCoalescer(
        TimeSpan? coalesceWindow = null,
        Func<TimeSpan>? clock = null)
    {
        _window = coalesceWindow ?? DefaultWindow;
        _clock = clock ?? DefaultClock;
    }

    public TimeSpan Window => _window;

    public CoalescePushResult Push(ProviderStreamUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var now = _clock();

        lock (_sync)
        {
            if (!_cards.TryGetValue(update.ProviderInstanceId, out var slot))
            {
                slot = new CardState();
                _cards[update.ProviderInstanceId] = slot;
            }

            if (update.State != ProviderStreamState.Streaming)
            {
                var pending = slot.PendingUpdate;
                slot.PendingUpdate = null;

                if (update.State == ProviderStreamState.Pending)
                {
                    slot.HasEmittedNonEmptyStreamingChunk = false;
                    slot.HasFlushedStreaming = false;
                    slot.LastEmittedIsReasoning = null;
                    _ready.Enqueue(update);
                }
                else
                {
                    var finalUpdate = update;
                    if (pending is not null)
                    {
                        if (finalUpdate.DetectedSourceLanguage is null or { IsAuto: true } &&
                            pending.DetectedSourceLanguage is { IsAuto: false })
                        {
                            finalUpdate = finalUpdate with { DetectedSourceLanguage = pending.DetectedSourceLanguage };
                        }

                        if (finalUpdate.TargetLanguage.IsAuto && !pending.TargetLanguage.IsAuto)
                        {
                            finalUpdate = finalUpdate with { TargetLanguage = pending.TargetLanguage };
                        }

                        if (string.IsNullOrEmpty(finalUpdate.AccumulatedText) &&
                            (!string.IsNullOrEmpty(pending.AccumulatedText) || !string.IsNullOrEmpty(pending.DeltaText)))
                        {
                            _ready.Enqueue(pending);
                        }
                    }

                    _ready.Enqueue(finalUpdate);
                }

                return new CoalescePushResult(ShouldDrainNow: true, ShouldScheduleDelayedFlush: false);
            }

            var merged = MergeStreamingUpdate(slot.PendingUpdate, update);
            var hasText = !string.IsNullOrEmpty(merged.AccumulatedText) || !string.IsNullOrEmpty(merged.DeltaText);
            var isFirstNonEmptyChunk = !slot.HasEmittedNonEmptyStreamingChunk && hasText;
            var isReasoningTransition = slot.LastEmittedIsReasoning != merged.IsReasoning;
            var windowElapsed = !slot.HasFlushedStreaming || (now - slot.LastFlushTimestamp) >= _window;

            if (isFirstNonEmptyChunk || isReasoningTransition || windowElapsed)
            {
                PromoteLocked(slot, merged, now);
                return new CoalescePushResult(ShouldDrainNow: true, ShouldScheduleDelayedFlush: false);
            }

            slot.PendingUpdate = merged;
            var shouldSchedule = !_delayedFlushScheduled;
            if (shouldSchedule)
            {
                _delayedFlushScheduled = true;
            }

            return new CoalescePushResult(ShouldDrainNow: false, ShouldScheduleDelayedFlush: shouldSchedule);
        }
    }

    public CoalesceFlushResult FlushDue()
    {
        var now = _clock();
        lock (_sync)
        {
            _delayedFlushScheduled = false;
            var promoted = false;
            var stillPending = false;

            foreach (var slot in _cards.Values)
            {
                if (slot.PendingUpdate is not { } pending)
                {
                    continue;
                }

                if ((now - slot.LastFlushTimestamp) >= _window)
                {
                    PromoteLocked(slot, pending, now);
                    promoted = true;
                }
                else
                {
                    stillPending = true;
                }
            }

            var shouldReschedule = stillPending && !_delayedFlushScheduled;
            if (shouldReschedule)
            {
                _delayedFlushScheduled = true;
            }

            return new CoalesceFlushResult(
                HasReadyUpdates: promoted || _ready.Count > 0,
                ShouldRescheduleDelayedFlush: shouldReschedule);
        }
    }

    public bool FlushAll()
    {
        var now = _clock();
        lock (_sync)
        {
            _delayedFlushScheduled = false;
            foreach (var slot in _cards.Values)
            {
                if (slot.PendingUpdate is { } pending)
                {
                    PromoteLocked(slot, pending, now);
                }
            }

            return _ready.Count > 0;
        }
    }

    public List<ProviderStreamUpdate> DrainReady()
    {
        lock (_sync)
        {
            if (_ready.Count == 0)
            {
                return [];
            }

            var batch = new List<ProviderStreamUpdate>(_ready.Count);
            while (_ready.Count > 0)
            {
                batch.Add(_ready.Dequeue());
            }

            return batch;
        }
    }

    private void PromoteLocked(CardState slot, ProviderStreamUpdate update, TimeSpan now)
    {
        slot.PendingUpdate = null;
        slot.HasFlushedStreaming = true;
        slot.LastFlushTimestamp = now;
        slot.LastEmittedIsReasoning = update.IsReasoning;
        if (!string.IsNullOrEmpty(update.AccumulatedText) || !string.IsNullOrEmpty(update.DeltaText))
        {
            slot.HasEmittedNonEmptyStreamingChunk = true;
        }

        _ready.Enqueue(update);
    }

    private static ProviderStreamUpdate MergeStreamingUpdate(
        ProviderStreamUpdate? existing,
        ProviderStreamUpdate incoming)
    {
        if (existing is null)
        {
            return incoming;
        }

        var detectedLanguage = incoming.DetectedSourceLanguage is { IsAuto: false }
            ? incoming.DetectedSourceLanguage
            : existing.DetectedSourceLanguage;

        var targetLanguage = !incoming.TargetLanguage.IsAuto
            ? incoming.TargetLanguage
            : existing.TargetLanguage;

        if (!string.IsNullOrEmpty(incoming.AccumulatedText))
        {
            if (detectedLanguage == incoming.DetectedSourceLanguage &&
                targetLanguage == incoming.TargetLanguage)
            {
                return incoming;
            }

            return incoming with
            {
                DetectedSourceLanguage = detectedLanguage,
                TargetLanguage = targetLanguage,
            };
        }

        if (!string.IsNullOrEmpty(existing.AccumulatedText))
        {
            var combinedAccumulated = string.IsNullOrEmpty(incoming.DeltaText)
                ? existing.AccumulatedText
                : string.Concat(existing.AccumulatedText, incoming.DeltaText);
            return incoming with
            {
                AccumulatedText = combinedAccumulated,
                DetectedSourceLanguage = detectedLanguage,
                TargetLanguage = targetLanguage,
            };
        }

        var combinedDelta = string.IsNullOrEmpty(existing.DeltaText)
            ? incoming.DeltaText
            : string.IsNullOrEmpty(incoming.DeltaText)
                ? existing.DeltaText
                : string.Concat(existing.DeltaText, incoming.DeltaText);

        return incoming with
        {
            DeltaText = combinedDelta,
            DetectedSourceLanguage = detectedLanguage,
            TargetLanguage = targetLanguage,
        };
    }

    private static TimeSpan DefaultClock() => Stopwatch.GetElapsedTime(0);

    private sealed class CardState
    {
        public bool HasEmittedNonEmptyStreamingChunk;
        public bool HasFlushedStreaming;
        public bool? LastEmittedIsReasoning;
        public TimeSpan LastFlushTimestamp;
        public ProviderStreamUpdate? PendingUpdate;
    }
}

public enum InputGuardReason
{
    None = 0,
    LongText = 1,
    UrlOrEmail = 2,
    FilePath = 3,
    JsonOrMarkup = 4,
    StackTrace = 5,
    CodeSnippet = 6,
}

public sealed record TranslationHistoryItemView(
    Guid Id,
    string SourceText,
    string PreviewText,
    string LanguagePairLabel,
    DateTimeOffset Timestamp,
    int CachedResultCount);
