using PopTranslate.Core.Models;

namespace PopTranslate.Core.Contracts;

public interface ISelectionHoverMonitor : IDisposable
{
    bool IsRunning { get; }

    event EventHandler<HoverSelectionEventArgs>? SelectionDetected;

    event EventHandler? DismissRequested;

    event EventHandler<nint>? DoubleTapCopyDetected;

    void Start(SelectionConfig config);

    void UpdateConfig(SelectionConfig config);

    void SetDoubleTapCopyEnabled(bool enabled);

    void SetHoverIconVisible(bool isVisible) { }

    void Stop();

    void RegisterIgnoredWindow(nint windowHandle);

    void UnregisterIgnoredWindow(nint windowHandle);
}
