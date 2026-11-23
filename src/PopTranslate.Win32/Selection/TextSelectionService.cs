using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;

namespace PopTranslate.Win32.Selection;

public sealed class TextSelectionService : ITextSelectionService
{
    private readonly UiAutomationSelectionReader _uiaReader;
    private readonly ClipboardFallbackSelectionReader _clipboardFallbackReader;
    private readonly IWindowHelperService _windowHelper;
    private readonly IForegroundAppGuard _foregroundAppGuard;

    public TextSelectionService(
        UiAutomationSelectionReader uiaReader,
        ClipboardFallbackSelectionReader clipboardFallbackReader,
        IWindowHelperService windowHelper,
        IForegroundAppGuard foregroundAppGuard)
    {
        _uiaReader = uiaReader ?? throw new ArgumentNullException(nameof(uiaReader));
        _clipboardFallbackReader = clipboardFallbackReader ?? throw new ArgumentNullException(nameof(clipboardFallbackReader));
        _windowHelper = windowHelper ?? throw new ArgumentNullException(nameof(windowHelper));
        _foregroundAppGuard = foregroundAppGuard ?? throw new ArgumentNullException(nameof(foregroundAppGuard));
    }

    public async Task<SelectionCapturedEventArgs> CaptureSelectionAsync(
        SelectionTriggerSource triggerSource,
        SelectionConfig config,
        nint? preferredTargetWindow = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var anchorPoint = _windowHelper.GetCursorPosition();
        var targetWindow = preferredTargetWindow is > 0
            ? preferredTargetWindow.Value
            : _windowHelper.GetForegroundWindowHandle();
        var processName = _foregroundAppGuard.GetProcessNameForWindow(targetWindow);

        var uiaText = await TryReadViaUiAutomationAsync(targetWindow, anchorPoint, cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(uiaText))
        {
            return new SelectionCapturedEventArgs(
                text: uiaText,
                anchorPoint: anchorPoint,
                triggerSource: triggerSource,
                captureMethod: SelectionCaptureMethod.UiAutomation,
                sourceProcessName: processName,
                isSuccess: true);
        }

        var allowClipboardFallback =
            config.EnableClipboardFallback &&
            triggerSource is SelectionTriggerSource.Hotkey or SelectionTriggerSource.HoverIcon;

        if (!allowClipboardFallback)
        {
            return new SelectionCapturedEventArgs(
                text: string.Empty,
                anchorPoint: anchorPoint,
                triggerSource: triggerSource,
                captureMethod: SelectionCaptureMethod.None,
                sourceProcessName: processName,
                isSuccess: false,
                failureReason: "No selected text detected via UI Automation and clipboard fallback is disabled.");
        }

        if (_foregroundAppGuard.IsProcessBlacklisted(processName, config.ProcessBlacklist))
        {
            return new SelectionCapturedEventArgs(
                text: string.Empty,
                anchorPoint: anchorPoint,
                triggerSource: triggerSource,
                captureMethod: SelectionCaptureMethod.None,
                sourceProcessName: processName,
                isSuccess: false,
                failureReason: $"Skipped Ctrl+C clipboard fallback because process '{processName}' is in the blacklist.");
        }

        var fallbackText = await _clipboardFallbackReader
            .TryCaptureViaCtrlCAsync(config.ClipboardWaitTimeoutMs, cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(fallbackText))
        {
            return new SelectionCapturedEventArgs(
                text: fallbackText,
                anchorPoint: anchorPoint,
                triggerSource: triggerSource,
                captureMethod: SelectionCaptureMethod.ClipboardFallback,
                sourceProcessName: processName,
                isSuccess: true);
        }

        return new SelectionCapturedEventArgs(
            text: string.Empty,
            anchorPoint: anchorPoint,
            triggerSource: triggerSource,
            captureMethod: SelectionCaptureMethod.None,
            sourceProcessName: processName,
            isSuccess: false,
            failureReason: "No selected text could be captured.");
    }

    public Task<string?> TryReadViaUiAutomationAsync(
        nint? targetWindow = null,
        ScreenPoint? probePoint = null,
        CancellationToken cancellationToken = default)
    {
        return _uiaReader.TryReadSelectionTextAsync(targetWindow, probePoint, cancellationToken);
    }
}
