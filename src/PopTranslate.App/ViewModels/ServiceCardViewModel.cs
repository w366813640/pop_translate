using PopTranslate.App.Services;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;

namespace PopTranslate.App.ViewModels;

public sealed partial class ServiceCardViewModel : ObservableObject
{
    public static IReadOnlyList<(PolishStyle Style, string DisplayLabel)> PolishStylePresets { get; } =
    [
        (PolishStyle.General, "通用自然"),
        (PolishStyle.Academic, "学术严谨"),
        (PolishStyle.Business, "商务得体"),
        (PolishStyle.Casual, "口语地道"),
        (PolishStyle.Concise, "精简凝练"),
        (PolishStyle.CodeNaming, "代码命名"),
        (PolishStyle.Custom, "自定义 Prompt"),
    ];

    private readonly Func<string, Task> _retryCallback;
    private readonly Func<string, Task>? _backTranslateCallback;
    private readonly Action<string>? _cancelBackTranslateCallback;
    private readonly Func<string, Task>? _writeBackCallback;
    private readonly Func<string, Task>? _toggleSpeakCallback;
    private readonly Action? _flyoutOpenedCallback;
    private readonly Action? _flyoutClosedCallback;
    private readonly ITextDiffService? _textDiffService;
    private readonly Action _layoutChangedCallback;
    private readonly DispatcherQueue? _dispatcher;

    private CancellationTokenSource? _copiedFeedbackCts;
    private CancellationTokenSource? _backCopiedFeedbackCts;
    private CancellationTokenSource? _writeBackFeedbackCts;
    private string? _lastDiffedRevisedText;

    public ServiceCardViewModel(
        ProviderInstanceConfig config,
        PopupMode mode,
        string sourceText,
        Func<string, Task> retryCallback,
        Func<string, Task>? backTranslateCallback,
        Action<string>? cancelBackTranslateCallback,
        ITextDiffService? textDiffService,
        Action layoutChangedCallback,
        bool showDiffByDefault = false,
        DispatcherQueue? dispatcher = null,
        Func<string, Task>? writeBackCallback = null,
        Func<string, Task>? toggleSpeakCallback = null,
        bool hasSourceWindow = false,
        string? cardId = null,
        PolishStyle initialPolishStyle = PolishStyle.General,
        bool isWaitingOnDemand = false,
        Action? flyoutOpenedCallback = null,
        Action? flyoutClosedCallback = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _retryCallback = retryCallback;
        _backTranslateCallback = backTranslateCallback;
        _cancelBackTranslateCallback = cancelBackTranslateCallback;
        _textDiffService = textDiffService;
        _layoutChangedCallback = layoutChangedCallback;
        _dispatcher = dispatcher;
        _writeBackCallback = writeBackCallback;
        _toggleSpeakCallback = toggleSpeakCallback;
        _flyoutOpenedCallback = flyoutOpenedCallback;
        _flyoutClosedCallback = flyoutClosedCallback;

        CardId = string.IsNullOrWhiteSpace(cardId) ? config.Id : cardId;
        ProviderInstanceId = config.Id;
        DisplayName = string.IsNullOrWhiteSpace(config.DisplayName) ? config.Kind.ToString() : config.DisplayName;
        ProviderKind = config.Kind;
        Mode = mode;
        SourceText = sourceText ?? string.Empty;
        IsCollapsed = config.IsCollapsedByDefault;
        CardPolishStyle = initialPolishStyle;
        ShowDiff = mode == PopupMode.Polish && showDiffByDefault;
        HasSourceWindow = hasSourceWindow;
        IsWaitingOnDemand = mode == PopupMode.Polish && isWaitingOnDemand;

        if (IsWaitingOnDemand)
        {
            State = ProviderStreamState.Cancelled;
            StatusText = string.Empty;
        }
        else
        {
            State = ProviderStreamState.Pending;
            StatusText = "准备中...";
        }
    }

    public event EventHandler? DiffSegmentsChanged;

    public string CardId { get; }

    public string ProviderInstanceId { get; }

    public string DisplayName { get; }

    public ProviderKind ProviderKind { get; }

    public PopupMode Mode { get; }

    public string SourceText { get; }

    public LanguageCode? DetectedSourceLanguage { get; set; }

    public LanguageCode TargetLanguage { get; set; } = LanguageCode.Auto;

    public bool IsTranslateMode => Mode == PopupMode.Translate;

    public bool IsPolishMode => Mode == PopupMode.Polish;

    public string ProviderBadgeText => ProviderKind switch
    {
        ProviderKind.OpenAiCompatible => "AI",
        ProviderKind.DeepL => "DL",
        _ => "译",
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CardPolishStyleLabel))]
    [NotifyPropertyChangedFor(nameof(OnDemandSummaryText))]
    public partial PolishStyle CardPolishStyle { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFullCard))]
    [NotifyPropertyChangedFor(nameof(IsLoading))]
    [NotifyPropertyChangedFor(nameof(ShowStreamingPlaceholder))]
    [NotifyPropertyChangedFor(nameof(ShowContextualActionRow))]
    public partial bool IsWaitingOnDemand { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText))]
    [NotifyPropertyChangedFor(nameof(HasMarkdownSyntax))]
    [NotifyPropertyChangedFor(nameof(ShowStreamingPlaceholder))]
    [NotifyPropertyChangedFor(nameof(CanBackTranslate))]
    [NotifyPropertyChangedFor(nameof(CanToggleDiff))]
    [NotifyPropertyChangedFor(nameof(CanWriteBack))]
    [NotifyPropertyChangedFor(nameof(ShowPlainText))]
    [NotifyPropertyChangedFor(nameof(ShowFormattedMarkdown))]
    [NotifyPropertyChangedFor(nameof(ShowDiffText))]
    [NotifyPropertyChangedFor(nameof(ShowContextualActionRow))]
    public partial string TranslatedText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(ShowStreamingPlaceholder))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading))]
    [NotifyPropertyChangedFor(nameof(IsStreaming))]
    [NotifyPropertyChangedFor(nameof(HasMarkdownSyntax))]
    [NotifyPropertyChangedFor(nameof(ShowStreamingPlaceholder))]
    [NotifyPropertyChangedFor(nameof(CanBackTranslate))]
    [NotifyPropertyChangedFor(nameof(CanWriteBack))]
    [NotifyPropertyChangedFor(nameof(ShowPlainText))]
    [NotifyPropertyChangedFor(nameof(ShowFormattedMarkdown))]
    [NotifyPropertyChangedFor(nameof(ShowDiffText))]
    public partial ProviderStreamState State { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StreamingPlaceholderText))]
    public partial bool IsReasoning { get; set; }

    [ObservableProperty]
    public partial bool IsAutoCopied { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWriteBackButton))]
    [NotifyPropertyChangedFor(nameof(ShowTranslateWriteBackButton))]
    [NotifyPropertyChangedFor(nameof(ShowPolishWriteBackButton))]
    [NotifyPropertyChangedFor(nameof(CanWriteBack))]
    public partial bool HasSourceWindow { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WriteBackGlyph))]
    [NotifyPropertyChangedFor(nameof(WriteBackLabel))]
    [NotifyPropertyChangedFor(nameof(WriteBackTooltip))]
    public partial bool JustWroteBack { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeakGlyph))]
    [NotifyPropertyChangedFor(nameof(SpeakTooltip))]
    public partial bool IsSpeaking { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExpanded))]
    [NotifyPropertyChangedFor(nameof(ChevronGlyph))]
    public partial bool IsCollapsed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyGlyph))]
    [NotifyPropertyChangedFor(nameof(CopyTooltip))]
    public partial bool JustCopied { get; set; }

    // Polish local diff state
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDiffOff))]
    [NotifyPropertyChangedFor(nameof(ShowPlainText))]
    [NotifyPropertyChangedFor(nameof(ShowFormattedMarkdown))]
    [NotifyPropertyChangedFor(nameof(ShowDiffText))]
    public partial bool ShowDiff { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<DiffSegment> DiffSegments { get; set; } = [];

    // Back-translation state (Translate mode only)
    [ObservableProperty]
    public partial bool IsBackTranslateVisible { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackTranslatedText))]
    [NotifyPropertyChangedFor(nameof(ShowBackTranslatePlaceholder))]
    public partial string BackTranslatedText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBackTranslating))]
    [NotifyPropertyChangedFor(nameof(ShowBackTranslatePlaceholder))]
    public partial ProviderStreamState? BackTranslateState { get; set; }

    [ObservableProperty]
    public partial string BackTranslateStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackTranslateError))]
    [NotifyPropertyChangedFor(nameof(ShowBackTranslatePlaceholder))]
    public partial string? BackTranslateErrorMessage { get; set; }

    [ObservableProperty]
    public partial string BackTranslateTargetLabel { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackTranslateCopyGlyph))]
    [NotifyPropertyChangedFor(nameof(BackTranslateCopyTooltip))]
    public partial bool BackTranslateJustCopied { get; set; }

    public bool ShowFullCard => !IsWaitingOnDemand;

    public bool IsExpanded => !IsCollapsed;

    public bool IsDiffOff => !ShowDiff;

    public string CardPolishStyleLabel => FormatPolishStyleLabel(CardPolishStyle);

    public string OnDemandSummaryText => $"{DisplayName} · {CardPolishStyleLabel}";

    public bool IsLoading => !IsWaitingOnDemand && State is ProviderStreamState.Pending or ProviderStreamState.Streaming;

    public bool IsStreaming => !IsWaitingOnDemand && State == ProviderStreamState.Streaming;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool HasText => !string.IsNullOrEmpty(TranslatedText);

    public bool ShowContextualActionRow => HasText && !IsWaitingOnDemand;

    public bool HasMarkdownSyntax => !IsLoading && ContainsInlineMarkdown(TranslatedText);

    public bool ShowStreamingPlaceholder => IsLoading && !HasText && !HasError;

    public bool CanBackTranslate => IsTranslateMode && HasText && !IsLoading;

    public bool CanToggleDiff => IsPolishMode && HasText;

    public bool ShowWriteBackButton => HasSourceWindow;

    public bool ShowTranslateWriteBackButton => HasSourceWindow && IsTranslateMode;

    public bool ShowPolishWriteBackButton => HasSourceWindow && IsPolishMode;

    public bool CanWriteBack => HasSourceWindow && HasText && !IsLoading;

    /// <summary>
    /// Deferred Polish Diff: only shown once streaming finishes (!IsLoading) so live SSE streaming
    /// renders smoothly without O(N^2) Myers recalculations or flashing strikethroughs.
    /// </summary>
    public bool ShowDiffText => HasText && IsPolishMode && ShowDiff && !IsLoading;

    public bool ShowFormattedMarkdown => HasText && !IsLoading && !ShowDiffText && HasMarkdownSyntax;

    public bool ShowPlainText => HasText && !ShowDiffText && !ShowFormattedMarkdown;

    public bool IsBackTranslating => BackTranslateState is ProviderStreamState.Pending or ProviderStreamState.Streaming;

    public bool HasBackTranslatedText => !string.IsNullOrEmpty(BackTranslatedText);

    public bool HasBackTranslateError => !string.IsNullOrWhiteSpace(BackTranslateErrorMessage);

    public bool ShowBackTranslatePlaceholder => IsBackTranslating && !HasBackTranslatedText && !HasBackTranslateError;

    public string StreamingPlaceholderText => IsReasoning
        ? "思考中..."
        : (IsPolishMode ? "正在润色文本..." : "正在等待译文返回...");

    public string ChevronGlyph => IsCollapsed ? "\uE70D" : "\uE70E";

    public string CopyGlyph => JustCopied ? "\uE73E" : "\uE8C8";

    public string CopyTooltip => JustCopied ? "已复制" : (IsPolishMode ? "复制润色结果" : "复制译文");

    public string WriteBackGlyph => JustWroteBack ? "\uE73E" : "\uE7A7";

    public string WriteBackLabel => JustWroteBack ? "已回填替换" : "回填替换原文";

    public string WriteBackTooltip => JustWroteBack
        ? "已回填替换"
        : "回填替换目标窗口选中文本 (Ctrl+Shift+Enter)";

    public string SpeakGlyph => IsSpeaking ? "\uE71A" : "\uE767";

    public string SpeakTooltip => IsSpeaking
        ? "停止朗读"
        : (IsPolishMode ? "朗读润色结果" : "朗读译文");

    public string RetryTooltip => IsPolishMode ? "重新润色" : "重新翻译";

    public string BackTranslateCopyGlyph => BackTranslateJustCopied ? "\uE73E" : "\uE8C8";

    public string BackTranslateCopyTooltip => BackTranslateJustCopied ? "已复制回译" : "复制回译";

    public static string FormatPolishStyleLabel(PolishStyle style) => style switch
    {
        PolishStyle.General => "通用自然",
        PolishStyle.Academic => "学术严谨",
        PolishStyle.Business => "商务得体",
        PolishStyle.Casual => "口语地道",
        PolishStyle.Concise => "精简凝练",
        PolishStyle.CodeNaming => "代码命名",
        PolishStyle.Custom => "自定义 Prompt",
        _ => "通用自然",
    };

    public void NotifyFlyoutOpened() => _flyoutOpenedCallback?.Invoke();

    public void NotifyFlyoutClosed() => _flyoutClosedCallback?.Invoke();

    public async Task SelectPolishStyleAsync(PolishStyle nextStyle)
    {
        if (!IsPolishMode)
        {
            return;
        }

        var styleChanged = CardPolishStyle != nextStyle;
        CardPolishStyle = nextStyle;

        if (styleChanged || IsWaitingOnDemand || !IsLoading)
        {
            await _retryCallback(CardId);
        }
    }

    public void ResetForRetry()
    {
        IsWaitingOnDemand = false;
        TranslatedText = string.Empty;
        ErrorMessage = null;
        IsReasoning = false;
        IsAutoCopied = false;
        IsSpeaking = false;
        State = ProviderStreamState.Pending;
        StatusText = IsPolishMode ? "润色中..." : "翻译中...";
        IsCollapsed = false;
        _lastDiffedRevisedText = null;
        DiffSegments = [];
        DiffSegmentsChanged?.Invoke(this, EventArgs.Empty);
        ResetBackTranslationCore();
        _layoutChangedCallback();
    }

    public void ApplyUpdate(ProviderStreamUpdate update)
    {
        IsWaitingOnDemand = false;
        IsReasoning = update.State == ProviderStreamState.Streaming && update.IsReasoning;
        State = update.State;

        if (update.DetectedSourceLanguage is { IsAuto: false } detected)
        {
            DetectedSourceLanguage = detected;
        }

        if (!update.TargetLanguage.IsAuto)
        {
            TargetLanguage = update.TargetLanguage;
        }

        if (!string.IsNullOrEmpty(update.AccumulatedText))
        {
            TranslatedText = update.AccumulatedText;
        }
        else if (!string.IsNullOrEmpty(update.DeltaText))
        {
            TranslatedText += update.DeltaText;
        }

        switch (update.State)
        {
            case ProviderStreamState.Pending:
                ErrorMessage = null;
                StatusText = IsPolishMode ? "润色中..." : "翻译中...";
                break;

            case ProviderStreamState.Streaming:
                ErrorMessage = null;
                StatusText = update.IsReasoning ? "思考中..." : "生成中...";
                break;

            case ProviderStreamState.Completed:
                ErrorMessage = null;
                StatusText = FormatElapsed(update.Elapsed);
                RecomputeDiffIfNeeded();
                break;

            case ProviderStreamState.Failed:
                ErrorMessage = string.IsNullOrWhiteSpace(update.ErrorMessage)
                    ? (IsPolishMode
                        ? "润色请求失败，请检查模型配置或网络连接。"
                        : "翻译请求失败，请检查配置或网络连接。")
                    : update.ErrorMessage;
                StatusText = "失败";
                IsCollapsed = false;
                RecomputeDiffIfNeeded();
                break;

            case ProviderStreamState.Cancelled:
                StatusText = "已取消";
                RecomputeDiffIfNeeded();
                break;
        }

        _layoutChangedCallback();
    }

    public void BeginBackTranslation(LanguageCode backTargetLanguage, bool expandCard = false)
    {
        IsBackTranslateVisible = true;
        if (expandCard)
        {
            IsCollapsed = false;
        }

        BackTranslatedText = string.Empty;
        BackTranslateErrorMessage = null;
        BackTranslateState = ProviderStreamState.Pending;
        BackTranslateStatusText = "回译中...";
        BackTranslateTargetLabel = backTargetLanguage.IsAuto
            ? "回译"
            : $"回译 → {backTargetLanguage.NativeName}";

        _layoutChangedCallback();
    }

    public void ApplyBackTranslationUpdate(ProviderStreamUpdate update)
    {
        IsBackTranslateVisible = true;
        BackTranslateState = update.State;

        if (!update.TargetLanguage.IsAuto)
        {
            BackTranslateTargetLabel = $"回译 → {update.TargetLanguage.NativeName}";
        }

        if (!string.IsNullOrEmpty(update.AccumulatedText))
        {
            BackTranslatedText = update.AccumulatedText;
        }
        else if (!string.IsNullOrEmpty(update.DeltaText))
        {
            BackTranslatedText += update.DeltaText;
        }

        switch (update.State)
        {
            case ProviderStreamState.Pending:
                BackTranslateErrorMessage = null;
                BackTranslateStatusText = "回译中...";
                break;

            case ProviderStreamState.Streaming:
                BackTranslateErrorMessage = null;
                BackTranslateStatusText = update.IsReasoning ? "思考中..." : "流式回译中...";
                break;

            case ProviderStreamState.Completed:
                BackTranslateErrorMessage = null;
                BackTranslateStatusText = FormatElapsed(update.Elapsed);
                break;

            case ProviderStreamState.Failed:
                BackTranslateErrorMessage = string.IsNullOrWhiteSpace(update.ErrorMessage)
                    ? "回译请求失败，请重试。"
                    : update.ErrorMessage;
                BackTranslateStatusText = "失败";
                break;

            case ProviderStreamState.Cancelled:
                BackTranslateStatusText = "已取消";
                break;
        }

        _layoutChangedCallback();
    }

    public void ResetBackTranslation()
    {
        ResetBackTranslationCore();
        _layoutChangedCallback();
    }

    public void MarkWroteBack()
    {
        _writeBackFeedbackCts?.Cancel();
        _writeBackFeedbackCts?.Dispose();
        _writeBackFeedbackCts = new CancellationTokenSource();
        var ct = _writeBackFeedbackCts.Token;

        JustWroteBack = true;
        _ = ResetFeedbackBadgeAsync(ct, FeedbackKind.WriteBack);
    }

    partial void OnTranslatedTextChanged(string value)
    {
        if (!IsLoading && IsPolishMode && ShowDiff)
        {
            RecomputeDiffIfNeeded();
        }
    }

    partial void OnShowDiffChanged(bool value)
    {
        if (IsPolishMode && value && !IsLoading)
        {
            RecomputeDiffIfNeeded();
        }
        else
        {
            DiffSegmentsChanged?.Invoke(this, EventArgs.Empty);
        }

        _layoutChangedCallback();
    }

    [RelayCommand]
    private void ToggleCollapse()
    {
        if (IsWaitingOnDemand)
        {
            return;
        }

        IsCollapsed = !IsCollapsed;
        _layoutChangedCallback();
    }

    [RelayCommand]
    private void ToggleShowDiff()
    {
        if (CanToggleDiff)
        {
            ShowDiff = !ShowDiff;
        }
    }

    [RelayCommand]
    private async Task RunOnDemandPolishAsync()
    {
        if (IsPolishMode)
        {
            await _retryCallback(CardId);
        }
    }

    [RelayCommand]
    public void CopyResult()
    {
        if (!ClipboardLoopGuard.TryCopyText(TranslatedText))
        {
            return;
        }

        _copiedFeedbackCts?.Cancel();
        _copiedFeedbackCts?.Dispose();
        _copiedFeedbackCts = new CancellationTokenSource();
        var ct = _copiedFeedbackCts.Token;

        JustCopied = true;
        _ = ResetFeedbackBadgeAsync(ct, FeedbackKind.MainCopy);
    }

    [RelayCommand]
    private async Task WriteBackAsync()
    {
        if (_writeBackCallback is not null && CanWriteBack)
        {
            await _writeBackCallback(CardId);
        }
    }

    [RelayCommand]
    private async Task ToggleSpeakAsync()
    {
        if (_toggleSpeakCallback is not null && HasText)
        {
            await _toggleSpeakCallback(CardId);
        }
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        await _retryCallback(CardId);
    }

    [RelayCommand]
    private async Task BackTranslateAsync()
    {
        if (_backTranslateCallback is not null && CanBackTranslate)
        {
            await _backTranslateCallback(CardId);
        }
    }

    [RelayCommand]
    private void CopyBackTranslation()
    {
        if (!ClipboardLoopGuard.TryCopyText(BackTranslatedText))
        {
            return;
        }

        _backCopiedFeedbackCts?.Cancel();
        _backCopiedFeedbackCts?.Dispose();
        _backCopiedFeedbackCts = new CancellationTokenSource();
        var ct = _backCopiedFeedbackCts.Token;

        BackTranslateJustCopied = true;
        _ = ResetFeedbackBadgeAsync(ct, FeedbackKind.BackTranslationCopy);
    }

    [RelayCommand]
    private void DismissBackTranslation()
    {
        _cancelBackTranslateCallback?.Invoke(CardId);
        ResetBackTranslation();
    }

    private void ResetBackTranslationCore()
    {
        IsBackTranslateVisible = false;
        BackTranslatedText = string.Empty;
        BackTranslateErrorMessage = null;
        BackTranslateState = null;
        BackTranslateStatusText = string.Empty;
        BackTranslateTargetLabel = string.Empty;
        BackTranslateJustCopied = false;
    }

    private void RecomputeDiffIfNeeded()
    {
        if (!IsPolishMode || !ShowDiff || IsLoading)
        {
            return;
        }

        var revised = TranslatedText ?? string.Empty;
        if (string.IsNullOrEmpty(revised))
        {
            if (DiffSegments.Count > 0 || _lastDiffedRevisedText is not null)
            {
                _lastDiffedRevisedText = null;
                DiffSegments = [];
                DiffSegmentsChanged?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        if (string.Equals(_lastDiffedRevisedText, revised, StringComparison.Ordinal))
        {
            return;
        }

        _lastDiffedRevisedText = revised;
        DiffSegments = _textDiffService is not null
            ? _textDiffService.ComputeDiff(SourceText, revised)
            : [new DiffSegment(DiffOperation.Equal, revised)];
        DiffSegmentsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ResetFeedbackBadgeAsync(CancellationToken ct, FeedbackKind kind)
    {
        try
        {
            await Task.Delay(1500, ct);
            void ClearFlag()
            {
                switch (kind)
                {
                    case FeedbackKind.MainCopy:
                        JustCopied = false;
                        break;
                    case FeedbackKind.BackTranslationCopy:
                        BackTranslateJustCopied = false;
                        break;
                    case FeedbackKind.WriteBack:
                        JustWroteBack = false;
                        break;
                }
            }

            if (_dispatcher is not null)
            {
                _dispatcher.TryEnqueue(ClearFlag);
            }
            else
            {
                ClearFlag();
            }
        }
        catch (OperationCanceledException)
        {
            // Replaced by newer action.
        }
    }

    private static bool ContainsInlineMarkdown(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return text.Contains('`', StringComparison.Ordinal) ||
               text.Contains("**", StringComparison.Ordinal);
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalMilliseconds <= 0)
        {
            return "完成";
        }

        return elapsed.TotalSeconds >= 1.0
            ? $"{elapsed.TotalSeconds:0.0}s"
            : $"{Math.Round(elapsed.TotalMilliseconds):0}ms";
    }

    private enum FeedbackKind
    {
        MainCopy = 0,
        BackTranslationCopy = 1,
        WriteBack = 2,
    }
}
