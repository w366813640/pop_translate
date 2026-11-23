namespace PopTranslate.Core.Models;

public sealed class SelectionCapturedEventArgs : EventArgs
{
    public SelectionCapturedEventArgs(
        string text,
        ScreenPoint anchorPoint,
        SelectionTriggerSource triggerSource,
        SelectionCaptureMethod captureMethod,
        string? sourceProcessName = null,
        bool isSuccess = true,
        string? failureReason = null)
    {
        Text = text;
        AnchorPoint = anchorPoint;
        TriggerSource = triggerSource;
        CaptureMethod = captureMethod;
        SourceProcessName = sourceProcessName;
        IsSuccess = isSuccess;
        FailureReason = failureReason;
    }

    public string Text { get; }

    public ScreenPoint AnchorPoint { get; }

    public SelectionTriggerSource TriggerSource { get; }

    public SelectionCaptureMethod CaptureMethod { get; }

    public string? SourceProcessName { get; }

    public bool IsSuccess { get; }

    public string? FailureReason { get; }
}
