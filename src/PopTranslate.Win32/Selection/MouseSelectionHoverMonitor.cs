using System.Collections.Concurrent;
using System.Drawing;
using System.Threading.Channels;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PopTranslate.Win32.Selection;

public sealed class MouseSelectionHoverMonitor : ISelectionHoverMonitor
{
    private const uint WmQuit = 0x0012;
    private const uint WmKeyDown = 0x0100;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonDown = 0x0204;
    private const uint WmMButtonDown = 0x0207;
    private const uint WmMouseWheel = 0x020A;
    private const uint WmMouseHWheel = 0x020E;
    private const uint LlmhfInjected = 0x00000001;
    private const uint LlkhfInjected = 0x00000010;
    private const int MinDragDurationMs = 40;
    private const int MaxDragDurationMs = 10_000;
    private const int DoubleClickRadiusPx = 6;
    private const int SelectionSettleDelayMs = 35;

    private static readonly HashSet<string> KnownSelectableCanvasClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Chrome_WidgetWin_1",
        "Chrome_RenderWidgetHostHWND",
        "MozillaWindowClass",
        "AcrobatSDIWindow",
        "AVL_AVView",
        "SUMATRA_PDF_FRAME",
        "OpusApp",
        "ApplicationFrameWindow",
        "WinUIDesktopWin32WindowClass",
        "EdgeUiInputTopWndClass",
        "Intermediate D3D Window",
    };

    private readonly ITextSelectionService _textSelectionService;
    private readonly IForegroundAppGuard _foregroundAppGuard;
    private readonly IWindowHelperService _windowHelper;
    private readonly ConcurrentDictionary<nint, byte> _ignoredWindows = new();
    private readonly object _lifecycleLock = new();
    private readonly HOOKPROC _mouseHookDelegate;
    private readonly HOOKPROC _keyboardHookDelegate;

    private volatile SelectionConfig _config = new();
    private Channel<HookEvent>? _eventChannel;
    private CancellationTokenSource? _consumerCts;
    private Task? _consumerTask;
    private Thread? _hookThread;
    private uint _hookThreadNativeId;
    private volatile bool _isRunning;
    private bool _disposed;

    public MouseSelectionHoverMonitor(
        ITextSelectionService textSelectionService,
        IForegroundAppGuard foregroundAppGuard,
        IWindowHelperService windowHelper)
    {
        _textSelectionService = textSelectionService ?? throw new ArgumentNullException(nameof(textSelectionService));
        _foregroundAppGuard = foregroundAppGuard ?? throw new ArgumentNullException(nameof(foregroundAppGuard));
        _windowHelper = windowHelper ?? throw new ArgumentNullException(nameof(windowHelper));

        _mouseHookDelegate = MouseHookProc;
        _keyboardHookDelegate = KeyboardHookProc;
    }

    public bool IsRunning => _isRunning;

    public event EventHandler<HoverSelectionEventArgs>? SelectionDetected;

    public event EventHandler? DismissRequested;

    public static bool IsDragSelectionGesture(
        ScreenPoint downPoint,
        long downTimestampMs,
        ScreenPoint upPoint,
        long upTimestampMs,
        int minDragDistancePixels)
    {
        var duration = upTimestampMs - downTimestampMs;
        if (duration < MinDragDurationMs || duration > MaxDragDurationMs)
        {
            return false;
        }

        var dx = (double)(upPoint.X - downPoint.X);
        var dy = (double)(upPoint.Y - downPoint.Y);
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var threshold = Math.Max(4, minDragDistancePixels);
        return distance >= threshold;
    }

    public static bool IsDoubleClickSelectionGesture(
        ScreenPoint firstUpPoint,
        long firstUpTimestampMs,
        ScreenPoint secondUpPoint,
        long secondUpTimestampMs,
        int maxDoubleClickTimeMs,
        int maxRadiusPx = DoubleClickRadiusPx)
    {
        if (firstUpTimestampMs <= 0)
        {
            return false;
        }

        var deltaMs = secondUpTimestampMs - firstUpTimestampMs;
        if (deltaMs < 0 || deltaMs > Math.Max(150, maxDoubleClickTimeMs))
        {
            return false;
        }

        var dx = secondUpPoint.X - firstUpPoint.X;
        var dy = secondUpPoint.Y - firstUpPoint.Y;
        return dx * dx + dy * dy <= maxRadiusPx * maxRadiusPx;
    }

    public static bool IsKnownSelectableCanvasClass(string? className)
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            return false;
        }

        return KnownSelectableCanvasClasses.Contains(className.Trim());
    }

    public void Start(SelectionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _config = config.Clone();

        lock (_lifecycleLock)
        {
            if (_isRunning)
            {
                return;
            }

            _eventChannel = Channel.CreateUnbounded<HookEvent>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
            _consumerCts = new CancellationTokenSource();
            var reader = _eventChannel.Reader;
            var consumerToken = _consumerCts.Token;
            _consumerTask = Task.Run(() => ConsumeHookEventsAsync(reader, consumerToken), CancellationToken.None);

            using var readyEvent = new ManualResetEventSlim(false);
            Exception? startupError = null;

            var thread = new Thread(() => RunHookLoop(readyEvent, ex => startupError = ex))
            {
                IsBackground = true,
                Name = "PopTranslate.SelectionHoverHook",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            readyEvent.Wait(TimeSpan.FromSeconds(5));
            if (startupError is not null)
            {
                CleanupConsumerLocked();
                throw new InvalidOperationException("Failed to install low-level selection hover hooks.", startupError);
            }

            _hookThread = thread;
            _isRunning = true;
        }
    }

    public void UpdateConfig(SelectionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config.Clone();
    }

    public void Stop()
    {
        Thread? threadToJoin;
        uint nativeThreadId;
        Task? consumerTaskToWait;

        lock (_lifecycleLock)
        {
            if (!_isRunning && _hookThread is null)
            {
                return;
            }

            _isRunning = false;
            threadToJoin = _hookThread;
            nativeThreadId = _hookThreadNativeId;
            _hookThread = null;
            _hookThreadNativeId = 0;

            _eventChannel?.Writer.TryComplete();
            _consumerCts?.Cancel();
            consumerTaskToWait = _consumerTask;
            _consumerTask = null;
        }

        if (nativeThreadId != 0)
        {
            PInvoke.PostThreadMessage(nativeThreadId, WmQuit, default, default);
        }

        if (threadToJoin is not null && threadToJoin.ManagedThreadId != Environment.CurrentManagedThreadId)
        {
            threadToJoin.Join(TimeSpan.FromSeconds(3));
        }

        try
        {
            consumerTaskToWait?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Ignore expected cancellation on shutdown.
        }

        lock (_lifecycleLock)
        {
            _consumerCts?.Dispose();
            _consumerCts = null;
            _eventChannel = null;
        }
    }

    public void RegisterIgnoredWindow(nint windowHandle)
    {
        if (windowHandle != nint.Zero)
        {
            _ignoredWindows[windowHandle] = 0;
        }
    }

    public void UnregisterIgnoredWindow(nint windowHandle)
    {
        if (windowHandle != nint.Zero)
        {
            _ignoredWindows.TryRemove(windowHandle, out _);
        }
    }

    public bool IsWindowIgnored(nint windowHandle) =>
        windowHandle != nint.Zero && _ignoredWindows.ContainsKey(windowHandle);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    private void CleanupConsumerLocked()
    {
        _eventChannel?.Writer.TryComplete();
        _consumerCts?.Cancel();
        _consumerCts?.Dispose();
        _consumerCts = null;
        _consumerTask = null;
        _eventChannel = null;
    }

    private unsafe void RunHookLoop(ManualResetEventSlim readyEvent, Action<Exception> reportError)
    {
        HHOOK mouseHook = HHOOK.Null;
        HHOOK keyboardHook = HHOOK.Null;

        try
        {
            _hookThreadNativeId = PInvoke.GetCurrentThreadId();

            // Force creation of the thread message queue before signaling readiness.
            PInvoke.PeekMessage(out _, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);

            var hModule = new HINSTANCE((nint)PInvoke.GetModuleHandle((PCWSTR)null));

            mouseHook = PInvoke.SetWindowsHookEx(
                WINDOWS_HOOK_ID.WH_MOUSE_LL,
                _mouseHookDelegate,
                hModule,
                0);

            if (mouseHook == HHOOK.Null)
            {
                throw new InvalidOperationException("SetWindowsHookExW(WH_MOUSE_LL) returned NULL.");
            }

            keyboardHook = PInvoke.SetWindowsHookEx(
                WINDOWS_HOOK_ID.WH_KEYBOARD_LL,
                _keyboardHookDelegate,
                hModule,
                0);

            readyEvent.Set();

            while (PInvoke.GetMessage(out var msg, HWND.Null, 0, 0) > 0)
            {
                PInvoke.TranslateMessage(msg);
                PInvoke.DispatchMessage(msg);
            }
        }
        catch (Exception ex)
        {
            reportError(ex);
            readyEvent.Set();
        }
        finally
        {
            if (keyboardHook != HHOOK.Null)
            {
                PInvoke.UnhookWindowsHookEx(keyboardHook);
            }

            if (mouseHook != HHOOK.Null)
            {
                PInvoke.UnhookWindowsHookEx(mouseHook);
            }
        }
    }

    // CRITICAL: Must return in microseconds — ZERO UIA, clipboard, locking, or I/O here!
    private unsafe LRESULT MouseHookProc(int nCode, WPARAM wParam, LPARAM lParam)
    {
        if (nCode >= 0)
        {
            var msg = (uint)wParam.Value;
            if (msg is WmLButtonDown or WmLButtonUp or WmRButtonDown or WmMButtonDown or WmMouseWheel or WmMouseHWheel)
            {
                var info = (MSLLHOOKSTRUCT*)lParam.Value;
                if (info is not null && (info->flags & LlmhfInjected) == 0)
                {
                    var kind = msg switch
                    {
                        WmLButtonDown => HookEventKind.LeftButtonDown,
                        WmLButtonUp => HookEventKind.LeftButtonUp,
                        WmRButtonDown => HookEventKind.RightButtonDown,
                        WmMButtonDown => HookEventKind.MiddleButtonDown,
                        _ => HookEventKind.MouseWheel,
                    };

                    _eventChannel?.Writer.TryWrite(new HookEvent(
                        kind,
                        info->pt.X,
                        info->pt.Y,
                        0,
                        Environment.TickCount64));
                }
            }
        }

        return PInvoke.CallNextHookEx(HHOOK.Null, nCode, wParam, lParam);
    }

    // CRITICAL: Must return in microseconds — ZERO blocking work here!
    private unsafe LRESULT KeyboardHookProc(int nCode, WPARAM wParam, LPARAM lParam)
    {
        if (nCode >= 0)
        {
            var msg = (uint)wParam.Value;
            if (msg is WmKeyDown or WmSysKeyDown)
            {
                var info = (KBDLLHOOKSTRUCT*)lParam.Value;
                if (info is not null && ((uint)info->flags & LlkhfInjected) == 0)
                {
                    _eventChannel?.Writer.TryWrite(new HookEvent(
                        HookEventKind.KeyDown,
                        0,
                        0,
                        info->vkCode,
                        Environment.TickCount64));
                }
            }
        }

        return PInvoke.CallNextHookEx(HHOOK.Null, nCode, wParam, lParam);
    }

    private async Task ConsumeHookEventsAsync(ChannelReader<HookEvent> reader, CancellationToken cancellationToken)
    {
        bool hasActiveDown = false;
        bool downWasIgnored = false;
        ScreenPoint downPoint = default;
        long downTimestampMs = 0;

        ScreenPoint lastClickPoint = default;
        long lastClickTimestampMs = 0;

        CancellationTokenSource? pendingValidationCts = null;

        try
        {
            await foreach (var evt in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (evt.Kind)
                {
                    case HookEventKind.LeftButtonDown:
                    {
                        var point = new ScreenPoint(evt.X, evt.Y);
                        var rootHwnd = GetRootWindowAtPoint(point);
                        if (IsWindowIgnored(rootHwnd))
                        {
                            hasActiveDown = false;
                            downWasIgnored = true;
                            break;
                        }

                        CancelPendingValidation(ref pendingValidationCts);
                        hasActiveDown = true;
                        downWasIgnored = false;
                        downPoint = point;
                        downTimestampMs = evt.TimestampMs;
                        break;
                    }

                    case HookEventKind.LeftButtonUp:
                    {
                        if (downWasIgnored)
                        {
                            downWasIgnored = false;
                            hasActiveDown = false;
                            break;
                        }

                        var upPoint = new ScreenPoint(evt.X, evt.Y);
                        var rootHwnd = GetRootWindowAtPoint(upPoint);
                        if (IsWindowIgnored(rootHwnd))
                        {
                            hasActiveDown = false;
                            break;
                        }

                        var currentConfig = _config;
                        var isDrag = hasActiveDown && IsDragSelectionGesture(
                            downPoint,
                            downTimestampMs,
                            upPoint,
                            evt.TimestampMs,
                            currentConfig.MinDragDistancePixels);

                        hasActiveDown = false;

                        var doubleClickTime = (int)PInvoke.GetDoubleClickTime();
                        var isDoubleClick = !isDrag && IsDoubleClickSelectionGesture(
                            lastClickPoint,
                            lastClickTimestampMs,
                            upPoint,
                            evt.TimestampMs,
                            doubleClickTime);

                        if (isDrag || isDoubleClick)
                        {
                            lastClickTimestampMs = 0;
                            if (currentConfig.EnableHoverIcon)
                            {
                                CancelPendingValidation(ref pendingValidationCts);
                                pendingValidationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                                var validationToken = pendingValidationCts.Token;
                                _ = Task.Run(
                                    () => ValidateAndRaiseHoverSelectionAsync(upPoint, currentConfig, validationToken),
                                    CancellationToken.None);
                            }
                        }
                        else
                        {
                            lastClickPoint = upPoint;
                            lastClickTimestampMs = evt.TimestampMs;
                            RaiseDismissRequested();
                        }

                        break;
                    }

                    case HookEventKind.RightButtonDown:
                    case HookEventKind.MiddleButtonDown:
                    case HookEventKind.MouseWheel:
                    {
                        var point = new ScreenPoint(evt.X, evt.Y);
                        var rootHwnd = GetRootWindowAtPoint(point);
                        if (!IsWindowIgnored(rootHwnd))
                        {
                            CancelPendingValidation(ref pendingValidationCts);
                            RaiseDismissRequested();
                        }

                        break;
                    }

                    case HookEventKind.KeyDown:
                    {
                        if (IsModifierVirtualKey(evt.VirtualKey))
                        {
                            break;
                        }

                        if (evt.VirtualKey == (uint)VIRTUAL_KEY.VK_ESCAPE)
                        {
                            CancelPendingValidation(ref pendingValidationCts);
                            RaiseDismissRequested();
                            break;
                        }

                        var fgHwnd = _windowHelper.GetForegroundWindowHandle();
                        if (!IsWindowIgnored(fgHwnd))
                        {
                            CancelPendingValidation(ref pendingValidationCts);
                            RaiseDismissRequested();
                        }

                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Clean shutdown.
        }
        finally
        {
            CancelPendingValidation(ref pendingValidationCts);
        }
    }

    private async Task ValidateAndRaiseHoverSelectionAsync(
        ScreenPoint anchorPoint,
        SelectionConfig config,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SelectionSettleDelayMs, cancellationToken).ConfigureAwait(false);

            if (!config.EnableHoverIcon)
            {
                return;
            }

            var foregroundHwnd = _windowHelper.GetForegroundWindowHandle();
            if (foregroundHwnd == nint.Zero || IsWindowIgnored(foregroundHwnd))
            {
                return;
            }

            if (_foregroundAppGuard.IsForegroundAppSuppressed(config, out _, out var processName))
            {
                return;
            }

            // ZERO-INVASIVE: Strictly UIA-only validation here — NEVER sends Ctrl+C!
            var uiaText = await _textSelectionService
                .TryReadViaUiAutomationAsync(foregroundHwnd, anchorPoint, cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(uiaText))
            {
                RaiseSelectionDetected(new HoverSelectionEventArgs(
                    previewText: uiaText,
                    hasUiaText: true,
                    anchorPoint: anchorPoint,
                    sourceWindowHandle: foregroundHwnd,
                    sourceProcessName: processName));
                return;
            }

            // Fallback heuristic for browsers/PDF canvases that do not expose UIA TextPattern:
            // still NEVER sends Ctrl+C until the user explicitly clicks the floating hover icon!
            var foregroundClass = GetWindowClassName(foregroundHwnd);
            var hoveredClass = GetWindowClassName(GetWindowAtPoint(anchorPoint));
            if (IsKnownSelectableCanvasClass(foregroundClass) || IsKnownSelectableCanvasClass(hoveredClass))
            {
                RaiseSelectionDetected(new HoverSelectionEventArgs(
                    previewText: null,
                    hasUiaText: false,
                    anchorPoint: anchorPoint,
                    sourceWindowHandle: foregroundHwnd,
                    sourceProcessName: processName));
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer mouse/keyboard event or shutdown.
        }
        catch
        {
            // Never crash the background hover worker.
        }
    }

    private void RaiseSelectionDetected(HoverSelectionEventArgs args)
    {
        try
        {
            SelectionDetected?.Invoke(this, args);
        }
        catch
        {
            // Keep monitor resilient against subscriber exceptions.
        }
    }

    private void RaiseDismissRequested()
    {
        try
        {
            DismissRequested?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Keep monitor resilient against subscriber exceptions.
        }
    }

    private static void CancelPendingValidation(ref CancellationTokenSource? cts)
    {
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        finally
        {
            cts.Dispose();
            cts = null;
        }
    }

    private static bool IsModifierVirtualKey(uint vk) =>
        (VIRTUAL_KEY)vk is
            VIRTUAL_KEY.VK_SHIFT or
            VIRTUAL_KEY.VK_LSHIFT or
            VIRTUAL_KEY.VK_RSHIFT or
            VIRTUAL_KEY.VK_CONTROL or
            VIRTUAL_KEY.VK_LCONTROL or
            VIRTUAL_KEY.VK_RCONTROL or
            VIRTUAL_KEY.VK_MENU or
            VIRTUAL_KEY.VK_LMENU or
            VIRTUAL_KEY.VK_RMENU or
            VIRTUAL_KEY.VK_LWIN or
            VIRTUAL_KEY.VK_RWIN;

    private static nint GetWindowAtPoint(ScreenPoint point) =>
        (nint)PInvoke.WindowFromPoint(new Point(point.X, point.Y));

    private static nint GetRootWindowAtPoint(ScreenPoint point)
    {
        var hwnd = PInvoke.WindowFromPoint(new Point(point.X, point.Y));
        if (hwnd == HWND.Null)
        {
            return nint.Zero;
        }

        var root = PInvoke.GetAncestor(hwnd, GET_ANCESTOR_FLAGS.GA_ROOT);
        return root != HWND.Null ? (nint)root : (nint)hwnd;
    }

    private static unsafe string? GetWindowClassName(nint windowHandle)
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

        Span<char> buffer = stackalloc char[256];
        fixed (char* pBuffer = buffer)
        {
            var len = PInvoke.GetClassName(hwnd, pBuffer, buffer.Length);
            return len > 0 ? new string(buffer[..len]) : null;
        }
    }

    private enum HookEventKind : byte
    {
        LeftButtonDown = 1,
        LeftButtonUp = 2,
        RightButtonDown = 3,
        MiddleButtonDown = 4,
        MouseWheel = 5,
        KeyDown = 6,
    }

    private readonly record struct HookEvent(
        HookEventKind Kind,
        int X,
        int Y,
        uint VirtualKey,
        long TimestampMs);
}
