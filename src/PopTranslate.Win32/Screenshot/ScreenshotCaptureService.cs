using System.Runtime.InteropServices;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PopTranslate.Win32.Screenshot;

public sealed class ScreenshotCaptureService : IScreenshotCaptureService
{
    private const uint WmQuit = 0x0012;
    private static readonly DPI_AWARENESS_CONTEXT DpiAwarenessPerMonitorV2 = new(-4);

    private readonly IWindowHelperService _windowHelper;
    private readonly object _stateLock = new();

    private volatile bool _isCaptureActive;
    private volatile bool _cancelRequested;
    private uint _activeOverlayThreadId;
    private int _lastSessionPaintCount;

    public ScreenshotCaptureService(IWindowHelperService windowHelper)
    {
        _windowHelper = windowHelper ?? throw new ArgumentNullException(nameof(windowHelper));
    }

    public bool IsCaptureActive => _isCaptureActive;

    public int LastSessionPaintCount => Volatile.Read(ref _lastSessionPaintCount);

    public async Task<CapturedScreenshot?> CaptureRegionAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        lock (_stateLock)
        {
            if (_isCaptureActive)
            {
                return null;
            }

            _isCaptureActive = true;
            _cancelRequested = false;
            Volatile.Write(ref _lastSessionPaintCount, 0);
        }

        try
        {
            var monitors = _windowHelper.GetMonitors();
            if (monitors.Count == 0)
            {
                return null;
            }

            var capturedMonitors = ScreenCaptureGdi.CaptureAllMonitors(monitors);
            try
            {
                if (capturedMonitors.Count == 0 || cancellationToken.IsCancellationRequested)
                {
                    return null;
                }

                var cursorPos = _windowHelper.GetCursorPosition();
                var selection = await RunOverlaySelectionAsync(capturedMonitors, cursorPos, cancellationToken)
                    .ConfigureAwait(false);

                if (selection is null || cancellationToken.IsCancellationRequested)
                {
                    return null;
                }

                return await ScreenCaptureGdi.CropAndEncodeAsync(
                        selection.Value.MonitorBitmap,
                        selection.Value.LocalSelectionRect,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                foreach (var bitmap in capturedMonitors)
                {
                    bitmap.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            lock (_stateLock)
            {
                _activeOverlayThreadId = 0;
                _isCaptureActive = false;
            }
        }
    }

    public void CancelActiveCapture()
    {
        uint threadId;
        lock (_stateLock)
        {
            _cancelRequested = true;
            threadId = _activeOverlayThreadId;
        }

        if (threadId != 0)
        {
            PInvoke.PostThreadMessage(threadId, WmQuit, default, default);
        }
    }

    private Task<(CapturedMonitorBitmap MonitorBitmap, ScreenRect LocalSelectionRect)?> RunOverlaySelectionAsync(
        IReadOnlyList<CapturedMonitorBitmap> capturedMonitors,
        ScreenPoint initialCursorPos,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<(CapturedMonitorBitmap, ScreenRect)?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            RunOverlayMessageLoop(capturedMonitors, initialCursorPos, cancellationToken, tcs);
        })
        {
            IsBackground = true,
            Name = "PopTranslate.ScreenshotOverlay",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return tcs.Task;
    }

    private unsafe void RunOverlayMessageLoop(
        IReadOnlyList<CapturedMonitorBitmap> capturedMonitors,
        ScreenPoint initialCursorPos,
        CancellationToken cancellationToken,
        TaskCompletionSource<(CapturedMonitorBitmap, ScreenRect)?> completion)
    {
        PInvoke.SetThreadDpiAwarenessContext(DpiAwarenessPerMonitorV2);

        var nativeThreadId = PInvoke.GetCurrentThreadId();
        PInvoke.PeekMessage(out _, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);

        lock (_stateLock)
        {
            _activeOverlayThreadId = nativeThreadId;
        }

        using var cancelRegistration = cancellationToken.Register(() =>
        {
            PInvoke.PostThreadMessage(nativeThreadId, WmQuit, default, default);
        });

        var className = $"PopTranslate_FrozenOverlay_{Guid.NewGuid():N}";
        var hInstance = new HINSTANCE((nint)PInvoke.GetModuleHandle((PCWSTR)null));
        var windowsByHwnd = new Dictionary<nint, FrozenMonitorOverlayWindow>(capturedMonitors.Count);
        var overlays = new List<FrozenMonitorOverlayWindow>(capturedMonitors.Count);
        (CapturedMonitorBitmap, ScreenRect)? result = null;
        bool classRegistered = false;

        WNDPROC wndProc = (hwnd, msg, wParam, lParam) =>
        {
            if (windowsByHwnd.TryGetValue((nint)hwnd, out var overlay))
            {
                return overlay.HandleMessage(hwnd, msg, wParam, lParam);
            }

            return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
        };

        void FinishAndQuit((CapturedMonitorBitmap, ScreenRect)? selection)
        {
            result = selection;
            PInvoke.PostQuitMessage(0);
        }

        try
        {
            if (_cancelRequested || cancellationToken.IsCancellationRequested)
            {
                completion.TrySetResult(null);
                return;
            }

            fixed (char* pClassName = className)
            {
                var wndClass = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = wndProc,
                    hInstance = hInstance,
                    hCursor = PInvoke.LoadCursor(HINSTANCE.Null, new PCWSTR((char*)32515)), // IDC_CROSS
                    lpszClassName = pClassName,
                };

                if (PInvoke.RegisterClassEx(wndClass) == 0)
                {
                    completion.TrySetResult(null);
                    return;
                }

                classRegistered = true;
            }

            FrozenMonitorOverlayWindow? focusCandidate = null;
            foreach (var captured in capturedMonitors)
            {
                var overlay = new FrozenMonitorOverlayWindow(
                    captured,
                    onCompleted: (bmp, rect) => FinishAndQuit((bmp, rect)),
                    onCancelled: () => FinishAndQuit(null),
                    onPainted: () => Interlocked.Increment(ref _lastSessionPaintCount));

                overlay.Create(className, hInstance);
                overlays.Add(overlay);
                windowsByHwnd[(nint)overlay.Handle] = overlay;
                overlay.Show();

                if (captured.Monitor.Bounds.Contains(initialCursorPos))
                {
                    focusCandidate = overlay;
                }
            }

            focusCandidate ??= overlays.FirstOrDefault();
            if (focusCandidate is not null && focusCandidate.Handle != HWND.Null)
            {
                PInvoke.SetForegroundWindow(focusCandidate.Handle);
            }

            while (PInvoke.GetMessage(out var msg, HWND.Null, 0, 0) > 0)
            {
                PInvoke.TranslateMessage(msg);
                PInvoke.DispatchMessage(msg);
            }

            completion.TrySetResult(result);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
        finally
        {
            lock (_stateLock)
            {
                if (_activeOverlayThreadId == nativeThreadId)
                {
                    _activeOverlayThreadId = 0;
                }
            }

            foreach (var overlay in overlays)
            {
                overlay.Dispose();
            }

            windowsByHwnd.Clear();

            if (classRegistered)
            {
                fixed (char* pClassName = className)
                {
                    PInvoke.UnregisterClass(pClassName, hInstance);
                }
            }

            GC.KeepAlive(wndProc);
        }
    }
}
