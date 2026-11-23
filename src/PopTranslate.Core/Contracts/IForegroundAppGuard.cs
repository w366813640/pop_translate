using PopTranslate.Core.Models;

namespace PopTranslate.Core.Contracts;

public interface IForegroundAppGuard
{
    bool IsForegroundAppSuppressed(SelectionConfig config, out string? reason, out string? processName);

    bool IsProcessBlacklisted(string? processName, IEnumerable<string> blacklist);

    bool IsFullscreenAppActive(nint foregroundWindow);

    string? GetProcessNameForWindow(nint windowHandle);
}
