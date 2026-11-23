using PopTranslate.Core.Models;

namespace PopTranslate.Core.Contracts;

public interface ITextSelectionService
{
    Task<SelectionCapturedEventArgs> CaptureSelectionAsync(
        SelectionTriggerSource triggerSource,
        SelectionConfig config,
        nint? preferredTargetWindow = null,
        CancellationToken cancellationToken = default);

    Task<string?> TryReadViaUiAutomationAsync(
        nint? targetWindow = null,
        ScreenPoint? probePoint = null,
        CancellationToken cancellationToken = default);
}
