using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PopTranslate.Win32.Hotkeys;

public sealed class GlobalHotkeyService : IGlobalHotkeyService
{
    private const uint WmHotkey = 0x0312;
    private const uint WmDestroy = 0x0002;
    private const uint WmAppExecute = 0x8001;
    private const uint WmAppShutdown = 0x8002;
    private const int ErrorHotkeyAlreadyRegistered = 1409;
    private static readonly HWND HwndMessage = new(-3);

    private readonly object _lifecycleLock = new();
    private readonly ConcurrentQueue<Action> _pendingActions = new();
    private readonly Dictionary<int, (HotkeyAction Action, HotkeyGesture Gesture)> _activeHotkeys = new();
    private readonly string _windowClassName = $"PopTranslate_HotkeyWindow_{Guid.NewGuid():N}";
    private readonly WNDPROC _wndProcDelegate;

    private Thread? _pumpThread;
    private int _pumpThreadId;
    private HWND _hwnd;
    private bool _disposed;

    public GlobalHotkeyService()
    {
        _wndProcDelegate = WindowProc;
    }

    public event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;

    public static uint ToWin32Modifiers(HotkeyModifiers modifiers) =>
        (uint)modifiers | (uint)HOT_KEY_MODIFIERS.MOD_NOREPEAT;

    public static string FormatActionDisplayName(HotkeyAction action) => action switch
    {
        HotkeyAction.SelectionTranslate => "划词翻译",
        HotkeyAction.ScreenshotTranslate => "截图翻译",
        HotkeyAction.SelectionPolish => "划词润色",
        HotkeyAction.SilentOcr => "静默截图 OCR",
        HotkeyAction.ReplaceTranslate => "替换翻译",
        _ => action.ToString(),
    };

    public IReadOnlyDictionary<HotkeyAction, HotkeyRegistrationResult> ApplyHotkeys(HotkeyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ObjectDisposedException.ThrowIf(_disposed, this);

        EnsurePumpStarted();
        var snapshot = config.Clone();

        return InvokeOnPumpThread(() => ApplyHotkeysOnPumpThread(snapshot));
    }

    public void UnregisterAll()
    {
        if (_disposed)
        {
            return;
        }

        lock (_lifecycleLock)
        {
            if (_pumpThread is null || _hwnd == HWND.Null)
            {
                return;
            }
        }

        InvokeOnPumpThread(() =>
        {
            UnregisterAllOnPumpThread();
            return true;
        });
    }

    public void Dispose()
    {
        Thread? threadToJoin;
        HWND hwndToClose;

        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            threadToJoin = _pumpThread;
            hwndToClose = _hwnd;
        }

        if (hwndToClose != HWND.Null)
        {
            PInvoke.PostMessage(hwndToClose, WmAppShutdown, default, default);
        }

        if (threadToJoin is not null && Environment.CurrentManagedThreadId != _pumpThreadId)
        {
            threadToJoin.Join(TimeSpan.FromSeconds(3));
        }

        _hwnd = HWND.Null;
        _pumpThread = null;
    }

    private void EnsurePumpStarted()
    {
        lock (_lifecycleLock)
        {
            if (_pumpThread is not null)
            {
                return;
            }

            using var readyEvent = new ManualResetEventSlim(false);
            Exception? startupError = null;

            var thread = new Thread(() => RunMessagePump(readyEvent, ex => startupError = ex))
            {
                IsBackground = true,
                Name = "PopTranslate.GlobalHotkeyPump",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            readyEvent.Wait(TimeSpan.FromSeconds(5));
            if (startupError is not null)
            {
                throw new InvalidOperationException("Failed to start global hotkey message window.", startupError);
            }

            if (_hwnd == HWND.Null)
            {
                throw new InvalidOperationException("Timed out waiting for global hotkey message window initialization.");
            }

            _pumpThread = thread;
        }
    }

    private unsafe void RunMessagePump(ManualResetEventSlim readyEvent, Action<Exception> reportError)
    {
        _pumpThreadId = Environment.CurrentManagedThreadId;
        HINSTANCE hInstance = new((nint)PInvoke.GetModuleHandle((PCWSTR)null));
        bool classRegistered = false;

        try
        {
            fixed (char* pClassName = _windowClassName)
            {
                var wndClass = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = _wndProcDelegate,
                    hInstance = hInstance,
                    lpszClassName = pClassName,
                };

                if (PInvoke.RegisterClassEx(wndClass) == 0)
                {
                    var err = Marshal.GetLastPInvokeError();
                    throw new InvalidOperationException($"RegisterClassExW failed with Win32 error {err}.");
                }

                classRegistered = true;

                _hwnd = PInvoke.CreateWindowEx(
                    WINDOW_EX_STYLE.WS_EX_NOACTIVATE,
                    pClassName,
                    null,
                    0,
                    0,
                    0,
                    0,
                    0,
                    HwndMessage,
                    HMENU.Null,
                    hInstance,
                    null);

                if (_hwnd == HWND.Null)
                {
                    var err = Marshal.GetLastPInvokeError();
                    throw new InvalidOperationException($"CreateWindowExW(HWND_MESSAGE) failed with Win32 error {err}.");
                }
            }

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
            UnregisterAllOnPumpThread();
            if (_hwnd != HWND.Null)
            {
                PInvoke.DestroyWindow(_hwnd);
                _hwnd = HWND.Null;
            }

            if (classRegistered)
            {
                fixed (char* pClassName = _windowClassName)
                {
                    PInvoke.UnregisterClass(pClassName, hInstance);
                }
            }
        }
    }

    private LRESULT WindowProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            case WmHotkey:
                HandleHotkeyMessage((int)wParam.Value);
                return new LRESULT(0);

            case WmAppExecute:
                DrainPendingActions();
                return new LRESULT(0);

            case WmAppShutdown:
                UnregisterAllOnPumpThread();
                PInvoke.DestroyWindow(hwnd);
                _hwnd = HWND.Null;
                return new LRESULT(0);

            case WmDestroy:
                PInvoke.PostQuitMessage(0);
                return new LRESULT(0);

            default:
                return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private void HandleHotkeyMessage(int hotkeyId)
    {
        if (!_activeHotkeys.TryGetValue(hotkeyId, out var registration))
        {
            return;
        }

        var cursorPos = PInvoke.GetCursorPos(out var pt)
            ? new ScreenPoint(pt.X, pt.Y)
            : new ScreenPoint(0, 0);
        var foregroundWindow = (nint)PInvoke.GetForegroundWindow();

        var eventArgs = new HotkeyPressedEventArgs(
            registration.Action,
            registration.Gesture,
            cursorPos,
            foregroundWindow);

        var handler = HotkeyPressed;
        if (handler is null)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                handler.Invoke(this, eventArgs);
            }
            catch
            {
                // Prevent subscriber exceptions from tearing down the hotkey dispatch thread.
            }
        });
    }

    private IReadOnlyDictionary<HotkeyAction, HotkeyRegistrationResult> ApplyHotkeysOnPumpThread(HotkeyConfig config)
    {
        UnregisterAllOnPumpThread();

        var results = new Dictionary<HotkeyAction, HotkeyRegistrationResult>(5);
        var seenGestures = new Dictionary<(HotkeyModifiers, uint), HotkeyAction>();

        RegisterSingle(HotkeyAction.SelectionTranslate, config.SelectionTranslate, 1, seenGestures, results);
        RegisterSingle(HotkeyAction.ScreenshotTranslate, config.ScreenshotTranslate, 3, seenGestures, results);
        RegisterSingle(HotkeyAction.SelectionPolish, config.SelectionPolish, 4, seenGestures, results);
        RegisterSingle(HotkeyAction.SilentOcr, config.SilentOcr, 5, seenGestures, results);
        RegisterSingle(HotkeyAction.ReplaceTranslate, config.ReplaceTranslate, 6, seenGestures, results);

        return results;
    }

    private void RegisterSingle(
        HotkeyAction action,
        HotkeyGesture? gesture,
        int hotkeyId,
        Dictionary<(HotkeyModifiers, uint), HotkeyAction> seenGestures,
        Dictionary<HotkeyAction, HotkeyRegistrationResult> results)
    {
        if (gesture is null || gesture.IsEmpty)
        {
            results[action] = new HotkeyRegistrationResult(action, Succeeded: true);
            return;
        }

        var key = (gesture.Modifiers, gesture.VirtualKey);
        if (seenGestures.TryGetValue(key, out var existingAction))
        {
            var conflictDisplayName = FormatActionDisplayName(existingAction);
            results[action] = new HotkeyRegistrationResult(
                action,
                Succeeded: false,
                ErrorMessage: $"快捷键「{gesture.DisplayText}」与「{conflictDisplayName} ({existingAction})」冲突。");
            return;
        }

        var win32Modifiers = (HOT_KEY_MODIFIERS)ToWin32Modifiers(gesture.Modifiers);
        if (PInvoke.RegisterHotKey(_hwnd, hotkeyId, win32Modifiers, gesture.VirtualKey))
        {
            _activeHotkeys[hotkeyId] = (action, gesture);
            seenGestures[key] = action;
            results[action] = new HotkeyRegistrationResult(action, Succeeded: true);
        }
        else
        {
            var err = Marshal.GetLastPInvokeError();
            var errorMessage = err == ErrorHotkeyAlreadyRegistered
                ? $"快捷键「{gesture.DisplayText}」已被其他程序占用 (Win32 error {err})。"
                : $"注册快捷键「{gesture.DisplayText}」失败 (Win32 error {err})。";

            results[action] = new HotkeyRegistrationResult(action, Succeeded: false, ErrorMessage: errorMessage);
        }
    }

    private void UnregisterAllOnPumpThread()
    {
        if (_hwnd == HWND.Null || _activeHotkeys.Count == 0)
        {
            _activeHotkeys.Clear();
            return;
        }

        foreach (var hotkeyId in _activeHotkeys.Keys)
        {
            PInvoke.UnregisterHotKey(_hwnd, hotkeyId);
        }

        _activeHotkeys.Clear();
    }

    private void DrainPendingActions()
    {
        while (_pendingActions.TryDequeue(out var action))
        {
            action();
        }
    }

    private T InvokeOnPumpThread<T>(Func<T> func)
    {
        if (Environment.CurrentManagedThreadId == _pumpThreadId)
        {
            return func();
        }

        T? result = default;
        Exception? captured = null;
        using var done = new ManualResetEventSlim(false);

        _pendingActions.Enqueue(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
            finally
            {
                done.Set();
            }
        });

        if (!PInvoke.PostMessage(_hwnd, WmAppExecute, default, default))
        {
            throw new InvalidOperationException("Failed to post message to global hotkey window.");
        }

        if (!done.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("Timed out waiting for global hotkey pump thread.");
        }

        if (captured is not null)
        {
            throw new InvalidOperationException("Global hotkey operation failed.", captured);
        }

        return result!;
    }
}
