using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using PopTranslate.Core.Contracts;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PopTranslate.Win32.Windowing;

/// <summary>
/// Pure Win32 system tray icon and native popup context menu host backed by Shell_NotifyIconW.
/// Keyed strictly on (hWnd, uID) without NIF_GUID so moving portable directories never triggers
/// Windows Shell path-binding rejections, and rebuilds fresh HMENUs on every right-click.
/// </summary>
public sealed partial class Win32TrayIconService : IDisposable
{
    private const uint TrayIconUid = 1;
    private const uint WmNull = 0x0000;
    private const uint WmDestroy = 0x0002;
    private const uint WmContextMenu = 0x007B;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmAppExecute = 0x8021;
    private const uint WmAppShutdown = 0x8022;
    private const uint WmTrayCallback = 0x8023;

    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x00000010;
    private const nint IdiApplication = 32512;

    private const uint MfString = 0x00000000;
    private const uint MfGrayed = 0x00000001;
    private const uint MfDisabled = 0x00000002;
    private const uint MfChecked = 0x00000008;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmNoNotify = 0x0080;
    private const uint TpmReturnCmd = 0x0100;

    private readonly object _lifecycleLock = new();
    private readonly ConcurrentQueue<Action> _pendingActions = new();
    private readonly string _windowClassName = $"PopTranslate_TrayWindow_{Guid.NewGuid():N}";
    private readonly WNDPROC _wndProcDelegate;
    private readonly Func<uint, string, bool>? _shellNotifyOverride;

    private Thread? _pumpThread;
    private int _pumpThreadId;
    private HWND _hwnd;
    private HICON _hIcon;
    private bool _ownsIcon;
    private string? _loadedIconPath;
    private string _toolTipText = "PopTranslate 翻译";
    private Func<IReadOnlyList<NativeContextMenuItem>>? _menuItemsProvider;
    private uint _wmTaskbarCreated;
    private volatile bool _isCreated;
    private bool _isMenuOpen;
    private long _lastMenuClosedTick;
    private long _lastDoubleClickTick;
    private int _lastWin32Error;
    private bool _disposed;

    public Win32TrayIconService(Func<uint, string, bool>? shellNotifyOverride = null)
    {
        _wndProcDelegate = WindowProc;
        _shellNotifyOverride = shellNotifyOverride;
    }

    public event EventHandler? LeftClickRequested;
    public event EventHandler? DoubleClickRequested;
    public event EventHandler<int>? MenuCommandSelected;

    public bool IsCreated => _isCreated;

    public bool IsMessageWindowReady => _hwnd != HWND.Null;

    public bool HasLoadedIcon => _hIcon != HICON.Null;

    public int LastWin32Error => Volatile.Read(ref _lastWin32Error);

    public static bool IsShellTrayAvailable() =>
        PInvoke.FindWindow("Shell_TrayWnd", null) != HWND.Null;

    public bool CreateOrUpdate(
        string toolTipText,
        string? iconFilePath,
        Func<IReadOnlyList<NativeContextMenuItem>> menuItemsProvider)
    {
        ArgumentNullException.ThrowIfNull(menuItemsProvider);
        if (_disposed)
        {
            return false;
        }

        EnsurePumpStarted();

        return InvokeOnPumpThread(() =>
        {
            _toolTipText = toolTipText ?? "PopTranslate 翻译";
            _menuItemsProvider = menuItemsProvider;
            EnsureIconLoadedOnPumpThread(iconFilePath);
            return AddOrModifyNotifyIconOnPumpThread();
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
        _isCreated = false;
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
                Name = "PopTranslate.TrayIconPump",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            readyEvent.Wait(TimeSpan.FromSeconds(5));
            if (startupError is not null)
            {
                throw new InvalidOperationException("Failed to start native Win32 tray message window.", startupError);
            }

            if (_hwnd == HWND.Null)
            {
                throw new InvalidOperationException("Timed out waiting for native Win32 tray message window.");
            }

            _pumpThread = thread;
        }
    }

    private unsafe void RunMessagePump(ManualResetEventSlim readyEvent, Action<Exception> reportError)
    {
        _pumpThreadId = Environment.CurrentManagedThreadId;
        HINSTANCE hInstance = new((nint)PInvoke.GetModuleHandle((PCWSTR)null));
        var classRegistered = false;

        try
        {
            _wmTaskbarCreated = PInvoke.RegisterWindowMessage("TaskbarCreated");

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
                    throw new InvalidOperationException($"RegisterClassExW(TrayWindow) failed with Win32 error {err}.");
                }

                classRegistered = true;

                // Must be a hidden top-level window (HWND.Null parent, NOT HWND_MESSAGE) so it receives
                // Explorer "TaskbarCreated" broadcasts and properly dismisses TrackPopupMenuEx on outside click.
                _hwnd = PInvoke.CreateWindowEx(
                    WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE,
                    pClassName,
                    null,
                    WINDOW_STYLE.WS_POPUP,
                    0,
                    0,
                    0,
                    0,
                    HWND.Null,
                    HMENU.Null,
                    hInstance,
                    null);

                if (_hwnd == HWND.Null)
                {
                    var err = Marshal.GetLastPInvokeError();
                    throw new InvalidOperationException($"CreateWindowExW(TrayWindow) failed with Win32 error {err}.");
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
            DeleteNotifyIconOnPumpThread();
            ReleaseIconOnPumpThread();

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
        if (_wmTaskbarCreated != 0 && msg == _wmTaskbarCreated)
        {
            _isCreated = false;
            AddOrModifyNotifyIconOnPumpThread();
            return new LRESULT(0);
        }

        switch (msg)
        {
            case WmTrayCallback:
                HandleTrayMouseMessage((uint)(lParam.Value & 0xFFFF));
                return new LRESULT(0);

            case WmAppExecute:
                DrainPendingActions();
                return new LRESULT(0);

            case WmAppShutdown:
                DeleteNotifyIconOnPumpThread();
                ReleaseIconOnPumpThread();
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

    private void HandleTrayMouseMessage(uint mouseMsg)
    {
        switch (mouseMsg)
        {
            case WmRButtonUp:
            case WmContextMenu:
                ShowTrayPopupMenuOnPumpThread();
                break;

            case WmLButtonDblClk:
                _lastDoubleClickTick = Environment.TickCount64;
                DispatchAsync(() => DoubleClickRequested?.Invoke(this, EventArgs.Empty));
                break;

            case WmLButtonUp:
                if (!_isMenuOpen && Environment.TickCount64 - _lastDoubleClickTick > 250)
                {
                    DispatchAsync(() => LeftClickRequested?.Invoke(this, EventArgs.Empty));
                }
                break;
        }
    }

    private unsafe void ShowTrayPopupMenuOnPumpThread()
    {
        if (_disposed || _hwnd == HWND.Null || _isMenuOpen)
        {
            return;
        }

        if (Environment.TickCount64 - _lastMenuClosedTick < 180)
        {
            return;
        }

        var provider = _menuItemsProvider;
        if (provider is null)
        {
            return;
        }

        IReadOnlyList<NativeContextMenuItem> items;
        try
        {
            items = provider();
        }
        catch
        {
            return;
        }

        if (items.Count == 0)
        {
            return;
        }

        var hMenu = CreatePopupMenu();
        if (hMenu == nint.Zero)
        {
            return;
        }

        _isMenuOpen = true;
        int selectedCmd = 0;

        try
        {
            foreach (var item in items)
            {
                if (item.IsSeparator)
                {
                    AppendMenuW(hMenu, MfSeparator, 0, null);
                    continue;
                }

                var flags = MfString;
                if (item.IsChecked)
                {
                    flags |= MfChecked;
                }

                if (!item.IsEnabled)
                {
                    flags |= MfGrayed | MfDisabled;
                }

                AppendMenuW(hMenu, flags, (nuint)(uint)item.Id, item.Text ?? string.Empty);
            }

            if (!PInvoke.GetCursorPos(out var pt))
            {
                pt = default;
            }

            PInvoke.SetForegroundWindow(_hwnd);

            selectedCmd = TrackPopupMenuEx(
                hMenu,
                TpmRightButton | TpmReturnCmd | TpmNoNotify,
                pt.X,
                pt.Y,
                (nint)_hwnd.Value,
                nint.Zero);

            PInvoke.PostMessage(_hwnd, WmNull, default, default);
        }
        finally
        {
            DestroyMenu(hMenu);
            _lastMenuClosedTick = Environment.TickCount64;
            _isMenuOpen = false;
        }

        if (selectedCmd > 0)
        {
            var capturedCmd = selectedCmd;
            DispatchAsync(() => MenuCommandSelected?.Invoke(this, capturedCmd));
        }
    }

    private void EnsureIconLoadedOnPumpThread(string? iconFilePath)
    {
        if (_hIcon != HICON.Null &&
            string.Equals(_loadedIconPath, iconFilePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ReleaseIconOnPumpThread();
        _loadedIconPath = iconFilePath;

        var cx = Math.Max(16, PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSMICON));
        var cy = Math.Max(16, PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSMICON));

        if (!string.IsNullOrWhiteSpace(iconFilePath) && File.Exists(iconFilePath))
        {
            var loaded = LoadImageW(nint.Zero, iconFilePath, ImageIcon, cx, cy, LrLoadFromFile);
            if (loaded != nint.Zero)
            {
                _hIcon = new HICON(loaded);
                _ownsIcon = true;
                return;
            }
        }

        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
        {
            var extracted = ExtractIconW(nint.Zero, exePath, 0);
            if (extracted != nint.Zero && extracted != 1)
            {
                _hIcon = new HICON(extracted);
                _ownsIcon = true;
                return;
            }
        }

        var fallback = LoadIconW(nint.Zero, IdiApplication);
        _hIcon = new HICON(fallback);
        _ownsIcon = false;
    }

    private unsafe bool AddOrModifyNotifyIconOnPumpThread()
    {
        if (_hwnd == HWND.Null || _hIcon == HICON.Null)
        {
            return false;
        }

        var nid = BuildNotifyIconData();

        if (_isCreated)
        {
            var modified = _shellNotifyOverride is not null
                ? _shellNotifyOverride((uint)NOTIFY_ICON_MESSAGE.NIM_MODIFY, _toolTipText)
                : (bool)PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, in nid);
            if (modified)
            {
                Interlocked.Exchange(ref _lastWin32Error, 0);
                return true;
            }

            // Tray entry may have been dropped by Explorer; fall through to NIM_ADD.
            _isCreated = false;
        }

        var added = _shellNotifyOverride is not null
            ? _shellNotifyOverride((uint)NOTIFY_ICON_MESSAGE.NIM_ADD, _toolTipText)
            : (bool)PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, in nid);
        if (added)
        {
            _isCreated = true;
            Interlocked.Exchange(ref _lastWin32Error, 0);
            return true;
        }

        Interlocked.Exchange(ref _lastWin32Error, Marshal.GetLastPInvokeError());
        return false;
    }

    private unsafe void DeleteNotifyIconOnPumpThread()
    {
        if (!_isCreated || _hwnd == HWND.Null)
        {
            _isCreated = false;
            return;
        }

        if (_shellNotifyOverride is not null)
        {
            _shellNotifyOverride((uint)NOTIFY_ICON_MESSAGE.NIM_DELETE, _toolTipText);
            _isCreated = false;
            return;
        }

        var nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = TrayIconUid,
        };

        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in nid);
        _isCreated = false;
    }

    private unsafe NOTIFYICONDATAW BuildNotifyIconData()
    {
        var nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = TrayIconUid,
            uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE |
                     NOTIFY_ICON_DATA_FLAGS.NIF_ICON |
                     NOTIFY_ICON_DATA_FLAGS.NIF_TIP,
            uCallbackMessage = WmTrayCallback,
            hIcon = _hIcon,
        };

        var safeTip = _toolTipText ?? string.Empty;
        var copyLen = Math.Min(safeTip.Length, 127);
        safeTip.AsSpan(0, copyLen).CopyTo(new Span<char>(&nid.szTip, 128));
        return nid;
    }

    private unsafe void ReleaseIconOnPumpThread()
    {
        if (_ownsIcon && _hIcon != HICON.Null)
        {
            DestroyIcon((nint)_hIcon.Value);
        }

        _hIcon = HICON.Null;
        _ownsIcon = false;
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
            throw new InvalidOperationException("Failed to post action to native tray message pump.");
        }

        if (!done.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("Timed out waiting for native tray message pump.");
        }

        if (captured is not null)
        {
            throw new InvalidOperationException("Native tray operation failed.", captured);
        }

        return result!;
    }

    private static void DispatchAsync(Action callback)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                callback();
            }
            catch
            {
                // Never allow subscriber exceptions to crash ThreadPool / tray callbacks.
            }
        });
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenuW(nint hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int TrackPopupMenuEx(nint hMenu, uint uFlags, int x, int y, nint hwnd, nint lptpm);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint hMenu);

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadImageW(nint hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [LibraryImport("user32.dll", EntryPoint = "LoadIconW", SetLastError = true)]
    private static partial nint LoadIconW(nint hInstance, nint lpIconName);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint hIcon);

    [LibraryImport("shell32.dll", EntryPoint = "ExtractIconW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint ExtractIconW(nint hInst, string pszExeFileName, uint nIconIndex);
}
