using System.Diagnostics;
using System.Runtime.InteropServices;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PopTranslate.Win32.Guards;

public sealed class ForegroundAppGuard : IForegroundAppGuard
{
    private static readonly HashSet<string> ShellDesktopClassNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
        "Windows.UI.Core.CoreWindow",
    };

    public bool IsForegroundAppSuppressed(SelectionConfig config, out string? reason, out string? processName)
    {
        ArgumentNullException.ThrowIfNull(config);

        var foregroundWindow = (nint)PInvoke.GetForegroundWindow();
        processName = GetProcessNameForWindow(foregroundWindow);
        return EvaluateSuppression(foregroundWindow, processName, config, out reason);
    }

    public bool EvaluateSuppression(
        nint foregroundWindow,
        string? processName,
        SelectionConfig config,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (IsProcessBlacklisted(processName, config.ProcessBlacklist))
        {
            reason = $"Foreground process '{processName}' is in the blacklist.";
            return true;
        }

        if (config.QuietModeInFullscreen && IsFullscreenAppActive(foregroundWindow))
        {
            reason = "A fullscreen application is currently active.";
            return true;
        }

        reason = null;
        return false;
    }

    public bool IsProcessBlacklisted(string? processName, IEnumerable<string> blacklist)
    {
        var normalizedTarget = NormalizeProcessName(processName);
        if (string.IsNullOrEmpty(normalizedTarget) || blacklist is null)
        {
            return false;
        }

        foreach (var entry in blacklist)
        {
            var normalizedEntry = NormalizeProcessName(entry);
            if (!string.IsNullOrEmpty(normalizedEntry) &&
                string.Equals(normalizedTarget, normalizedEntry, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public unsafe bool IsFullscreenAppActive(nint foregroundWindow)
    {
        if (foregroundWindow == nint.Zero)
        {
            return false;
        }

        var hwnd = new HWND(foregroundWindow);
        if (!PInvoke.IsWindow(hwnd))
        {
            return false;
        }

        if (IsDesktopOrShellWindow(hwnd))
        {
            return false;
        }

        if (PInvoke.SHQueryUserNotificationState(out var state).Succeeded)
        {
            if (state is QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY
                or QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN
                or QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE)
            {
                return true;
            }
        }

        if (!PInvoke.IsWindowVisible(hwnd) || PInvoke.IsIconic(hwnd))
        {
            return false;
        }

        if (!PInvoke.GetWindowRect(hwnd, out var windowRect))
        {
            return false;
        }

        var monitor = PInvoke.MonitorFromWindow(hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        if (monitor == HMONITOR.Null)
        {
            return false;
        }

        var monitorInfo = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (!PInvoke.GetMonitorInfo(monitor, ref monitorInfo))
        {
            return false;
        }

        var bounds = monitorInfo.rcMonitor;
        var coversEntireMonitor =
            windowRect.left <= bounds.left &&
            windowRect.top <= bounds.top &&
            windowRect.right >= bounds.right &&
            windowRect.bottom >= bounds.bottom;

        if (!coversEntireMonitor)
        {
            return false;
        }

        // Avoid treating a standard titled window maximized over an auto-hidden taskbar as fullscreen.
        var work = monitorInfo.rcWork;
        var taskbarVisible =
            work.left != bounds.left ||
            work.top != bounds.top ||
            work.right != bounds.right ||
            work.bottom != bounds.bottom;

        if (taskbarVisible)
        {
            return true;
        }

        var style = (WINDOW_STYLE)(nuint)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        var hasCaptionAndFrame = style.HasFlag(WINDOW_STYLE.WS_CAPTION) && style.HasFlag(WINDOW_STYLE.WS_THICKFRAME);
        return !hasCaptionAndFrame;
    }

    public unsafe string? GetProcessNameForWindow(nint windowHandle)
    {
        if (windowHandle == nint.Zero)
        {
            return null;
        }

        var hwnd = new HWND(windowHandle);
        if (!PInvoke.IsWindow(hwnd))
        {
            return null;
        }

        uint processId = 0;
        var threadId = PInvoke.GetWindowThreadProcessId(hwnd, &processId);
        if (threadId == 0 || processId == 0)
        {
            return null;
        }

        // PROCESS_QUERY_LIMITED_INFORMATION works across elevation boundaries for reading image paths.
        var processHandle = PInvoke.OpenProcess(
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION,
            false,
            processId);

        if (!processHandle.IsNull)
        {
            try
            {
                Span<char> buffer = stackalloc char[1024];
                uint size = (uint)buffer.Length;
                fixed (char* pBuffer = buffer)
                {
                    if (PInvoke.QueryFullProcessImageName(processHandle, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, pBuffer, &size) && size > 0)
                    {
                        var fullPath = new string(buffer[..(int)size]);
                        var fileName = Path.GetFileName(fullPath);
                        if (!string.IsNullOrWhiteSpace(fileName))
                        {
                            return fileName;
                        }
                    }
                }
            }
            finally
            {
                PInvoke.CloseHandle(processHandle);
            }
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return string.IsNullOrWhiteSpace(process.ProcessName)
                ? null
                : $"{process.ProcessName}.exe";
        }
        catch
        {
            return null;
        }
    }

    internal static string NormalizeProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var trimmed = name.Trim().Trim('"');
        var fileName = Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName[..^4];
        }

        return fileName.Trim();
    }

    private static unsafe bool IsDesktopOrShellWindow(HWND hwnd)
    {
        if (hwnd == PInvoke.GetDesktopWindow() || hwnd == PInvoke.GetShellWindow())
        {
            return true;
        }

        Span<char> classNameBuffer = stackalloc char[256];
        fixed (char* pClassName = classNameBuffer)
        {
            var length = PInvoke.GetClassName(hwnd, pClassName, classNameBuffer.Length);
            if (length > 0)
            {
                var className = new string(classNameBuffer[..length]);
                if (ShellDesktopClassNames.Contains(className))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
