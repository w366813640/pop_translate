using PopTranslate.Core.Models;

namespace PopTranslate.Core.Contracts;

public interface ISelectionHoverMonitor : IDisposable
{
    bool IsRunning { get; }

    event EventHandler<HoverSelectionEventArgs>? SelectionDetected;

    event EventHandler? DismissRequested;

    void Start(SelectionConfig config);

    void UpdateConfig(SelectionConfig config);

    void Stop();

    void RegisterIgnoredWindow(nint windowHandle);

    void UnregisterIgnoredWindow(nint windowHandle);
}
