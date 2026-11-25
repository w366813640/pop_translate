using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using PopTranslate.Win32.Windowing;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;

namespace PopTranslate.App.Services;

/// <summary>
/// Manages the persistent Windows 11 system tray icon and context menu via pure Win32 Shell_NotifyIconW.
/// Builds fresh native Win32 menus on every right-click so state updates (mute 30m, hover toggle, hotkeys)
/// can never invalidate or break subsequent right-click popups.
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    public const int CommandInputTranslate = 101;
    public const int CommandReplaceTranslate = 102;
    public const int CommandPolishInput = 103;
    public const int CommandScreenshotTranslate = 104;
    public const int CommandSilentOcr = 105;
    public const int CommandToggleHoverIcon = 201;
    public const int CommandToggleHoverMute = 202;
    public const int CommandToggleClipboardListener = 203;
    public const int CommandSettings = 301;
    public const int CommandRestartAsAdmin = 302;
    public const int CommandExit = 401;

    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(3);

    private readonly Lock _stateLock = new();
    private readonly Win32TrayIconService _backend;
    private DispatcherQueueTimer? _createRetryTimer;

    private string _latestToolTipText = "PopTranslate 翻译";
    private bool _isHoverIconEnabled = true;
    private bool _isHoverMuted;
    private bool _isClipboardListenerEnabled;
    private bool _isRunningAsAdministrator;
    private bool _hasLoggedCreationSuccess;
    private bool _disposed;

    public TrayIconHost()
        : this(new Win32TrayIconService())
    {
    }

    public TrayIconHost(Win32TrayIconService backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));

        InputTranslateCommand = new RelayCommand(() => InputTranslateRequested?.Invoke(this, EventArgs.Empty));
        ReplaceTranslateCommand = new RelayCommand(() => ReplaceTranslateRequested?.Invoke(this, EventArgs.Empty));
        PolishInputCommand = new RelayCommand(() => PolishInputRequested?.Invoke(this, EventArgs.Empty));
        ScreenshotTranslateCommand = new RelayCommand(() => ScreenshotTranslateRequested?.Invoke(this, EventArgs.Empty));
        SilentOcrCommand = new RelayCommand(() => SilentOcrRequested?.Invoke(this, EventArgs.Empty));
        ToggleHoverIconCommand = new RelayCommand(ExecuteToggleHoverIcon);
        ToggleHoverMuteCommand = new RelayCommand(ExecuteToggleHoverMute);
        ToggleClipboardListenerCommand = new RelayCommand(ExecuteToggleClipboardListener);
        SettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
        RestartAsAdministratorCommand = new RelayCommand(() =>
        {
            if (!IsRunningAsAdministrator)
            {
                RestartAsAdministratorRequested?.Invoke(this, EventArgs.Empty);
            }
        });
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke(this, EventArgs.Empty));

        _backend.LeftClickRequested += (_, _) => InputTranslateCommand.Execute(null);
        _backend.DoubleClickRequested += (_, _) => SettingsCommand.Execute(null);
        _backend.MenuCommandSelected += (_, commandId) => DispatchMenuCommand(commandId);
    }

    public event EventHandler? InputTranslateRequested;
    public event EventHandler? ReplaceTranslateRequested;
    public event EventHandler? PolishInputRequested;
    public event EventHandler? ScreenshotTranslateRequested;
    public event EventHandler? SilentOcrRequested;
    public event EventHandler<bool>? HoverIconToggled;
    public event EventHandler<bool>? HoverMuteToggled;
    public event EventHandler<bool>? ClipboardListenerToggled;
    public event EventHandler? SettingsRequested;
    public event EventHandler? RestartAsAdministratorRequested;
    public event EventHandler? ExitRequested;

    public IRelayCommand InputTranslateCommand { get; }
    public IRelayCommand ReplaceTranslateCommand { get; }
    public IRelayCommand PolishInputCommand { get; }
    public IRelayCommand ScreenshotTranslateCommand { get; }
    public IRelayCommand SilentOcrCommand { get; }
    public IRelayCommand ToggleHoverIconCommand { get; }
    public IRelayCommand ToggleHoverMuteCommand { get; }
    public IRelayCommand ToggleClipboardListenerCommand { get; }
    public IRelayCommand SettingsCommand { get; }
    public IRelayCommand RestartAsAdministratorCommand { get; }
    public IRelayCommand ExitCommand { get; }

    public bool IsTrayIconCreated => _backend.IsCreated;

    public bool IsHoverIconEnabled
    {
        get { lock (_stateLock) { return _isHoverIconEnabled; } }
    }

    public bool IsHoverMuted
    {
        get { lock (_stateLock) { return _isHoverMuted; } }
    }

    public bool IsClipboardListenerEnabled
    {
        get { lock (_stateLock) { return _isClipboardListenerEnabled; } }
    }

    public bool IsRunningAsAdministrator
    {
        get { lock (_stateLock) { return _isRunningAsAdministrator; } }
    }

    public string InputTranslateLabel { get; private set; } = "输入翻译";
    public string ReplaceTranslateLabel { get; private set; } = "替换翻译";
    public string PolishInputLabel { get; private set; } = "文本润色";
    public string ScreenshotTranslateLabel { get; private set; } = "截图翻译";
    public string SilentOcrLabel { get; private set; } = "静默截图 OCR";

    public void SyncMenuState(
        AppSettings settings,
        bool isRunningAsAdministrator,
        bool isHoverMuted = false,
        bool isClipboardListenerEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_stateLock)
        {
            _isRunningAsAdministrator = isRunningAsAdministrator;
            _isHoverIconEnabled = settings.Selection.EnableHoverIcon;
            _isHoverMuted = isHoverMuted;
            _isClipboardListenerEnabled = isClipboardListenerEnabled;
            _latestToolTipText = BuildToolTipText(isRunningAsAdministrator, isHoverMuted, isClipboardListenerEnabled);

            InputTranslateLabel = FormatMenuLabel("打开翻译窗", settings.Hotkeys.SelectionTranslate);
            ReplaceTranslateLabel = FormatMenuLabel("替换翻译", settings.Hotkeys.ReplaceTranslate);
            PolishInputLabel = FormatMenuLabel("文本润色", settings.Hotkeys.SelectionPolish);
            ScreenshotTranslateLabel = FormatMenuLabel("截图翻译", settings.Hotkeys.ScreenshotTranslate);
            SilentOcrLabel = FormatMenuLabel("静默截图 OCR", settings.Hotkeys.SilentOcr);
        }
    }

    public IReadOnlyList<NativeContextMenuItem> BuildMenuItems()
    {
        lock (_stateLock)
        {
            var muteText = _isHoverMuted ? "暂停划词 30 分钟 (已暂停)" : "暂停划词 30 分钟";
            var adminText = _isRunningAsAdministrator ? "已以管理员身份运行" : "以管理员身份重启";

            return
            [
                new NativeContextMenuItem(CommandInputTranslate, InputTranslateLabel),
                new NativeContextMenuItem(CommandReplaceTranslate, ReplaceTranslateLabel),
                new NativeContextMenuItem(CommandPolishInput, PolishInputLabel),
                new NativeContextMenuItem(CommandScreenshotTranslate, ScreenshotTranslateLabel),
                new NativeContextMenuItem(CommandSilentOcr, SilentOcrLabel),
                new NativeContextMenuItem(0, IsSeparator: true),
                new NativeContextMenuItem(CommandToggleHoverIcon, "划词悬浮图标", IsChecked: _isHoverIconEnabled),
                new NativeContextMenuItem(CommandToggleHoverMute, muteText, IsChecked: _isHoverMuted),
                new NativeContextMenuItem(CommandToggleClipboardListener, "监听剪贴板翻译", IsChecked: _isClipboardListenerEnabled),
                new NativeContextMenuItem(0, IsSeparator: true),
                new NativeContextMenuItem(CommandSettings, "设置"),
                new NativeContextMenuItem(CommandRestartAsAdmin, adminText, IsEnabled: !_isRunningAsAdministrator),
                new NativeContextMenuItem(0, IsSeparator: true),
                new NativeContextMenuItem(CommandExit, "退出"),
            ];
        }
    }

    public void DispatchMenuCommand(int commandId)
    {
        if (_disposed)
        {
            return;
        }

        switch (commandId)
        {
            case CommandInputTranslate:
                InputTranslateCommand.Execute(null);
                break;
            case CommandReplaceTranslate:
                ReplaceTranslateCommand.Execute(null);
                break;
            case CommandPolishInput:
                PolishInputCommand.Execute(null);
                break;
            case CommandScreenshotTranslate:
                ScreenshotTranslateCommand.Execute(null);
                break;
            case CommandSilentOcr:
                SilentOcrCommand.Execute(null);
                break;
            case CommandToggleHoverIcon:
                ToggleHoverIconCommand.Execute(null);
                break;
            case CommandToggleHoverMute:
                ToggleHoverMuteCommand.Execute(null);
                break;
            case CommandToggleClipboardListener:
                ToggleClipboardListenerCommand.Execute(null);
                break;
            case CommandSettings:
                SettingsCommand.Execute(null);
                break;
            case CommandRestartAsAdmin:
                RestartAsAdministratorCommand.Execute(null);
                break;
            case CommandExit:
                ExitCommand.Execute(null);
                break;
        }
    }

    public void Initialize(
        AppSettings settings,
        bool isRunningAsAdministrator,
        DispatcherQueue? uiDispatcher = null,
        bool isHoverMuted = false,
        bool isClipboardListenerEnabled = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        SyncMenuState(settings, isRunningAsAdministrator, isHoverMuted, isClipboardListenerEnabled);

        if (!TryApplyTrayIcon())
        {
            StartRetryTimer(uiDispatcher);
        }
    }

    public void UpdateState(
        AppSettings settings,
        bool isRunningAsAdministrator,
        bool isHoverMuted = false,
        bool isClipboardListenerEnabled = false)
    {
        if (_disposed)
        {
            return;
        }

        SyncMenuState(settings, isRunningAsAdministrator, isHoverMuted, isClipboardListenerEnabled);
        TryApplyTrayIcon();
    }

    private bool TryApplyTrayIcon()
    {
        if (_disposed)
        {
            return false;
        }

        string toolTip;
        lock (_stateLock)
        {
            toolTip = _latestToolTipText;
        }

        try
        {
            var ok = _backend.CreateOrUpdate(toolTip, AppAssetHelper.IconPath, BuildMenuItems);
            if (ok)
            {
                StopRetryTimer();
                if (!_hasLoggedCreationSuccess)
                {
                    _hasLoggedCreationSuccess = true;
                    AppDiagnosticsLogger.Info("Tray", "System tray icon registered (Shell_NotifyIconW succeeded).");
                }

                return true;
            }

            AppDiagnosticsLogger.Warn(
                "Tray",
                $"Shell_NotifyIconW did not succeed yet (Win32 error {_backend.LastWin32Error}); retry scheduled.");
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("Tray", "Tray icon registration threw an exception.", ex);
        }

        return false;
    }

    private void ExecuteToggleHoverIcon()
    {
        bool targetState;
        lock (_stateLock)
        {
            targetState = !_isHoverIconEnabled;
            _isHoverIconEnabled = targetState;
        }

        HoverIconToggled?.Invoke(this, targetState);
    }

    private void ExecuteToggleHoverMute()
    {
        bool targetState;
        lock (_stateLock)
        {
            targetState = !_isHoverMuted;
            _isHoverMuted = targetState;
        }

        HoverMuteToggled?.Invoke(this, targetState);
    }

    private void ExecuteToggleClipboardListener()
    {
        bool targetState;
        lock (_stateLock)
        {
            targetState = !_isClipboardListenerEnabled;
            _isClipboardListenerEnabled = targetState;
        }

        ClipboardListenerToggled?.Invoke(this, targetState);
    }

    private static string BuildToolTipText(
        bool isRunningAsAdministrator,
        bool isHoverMuted,
        bool isClipboardListenerEnabled)
    {
        var baseText = isRunningAsAdministrator ? "PopTranslate 翻译 (管理员)" : "PopTranslate 翻译";
        if (isHoverMuted && isClipboardListenerEnabled)
        {
            return $"{baseText} · 划词已暂停 · 剪贴板监听中";
        }

        if (isHoverMuted)
        {
            return $"{baseText} · 划词已暂停";
        }

        if (isClipboardListenerEnabled)
        {
            return $"{baseText} · 剪贴板监听中";
        }

        return baseText;
    }

    private void StartRetryTimer(DispatcherQueue? dispatcher)
    {
        if (_disposed || dispatcher is null || _createRetryTimer is not null)
        {
            return;
        }

        _createRetryTimer = dispatcher.CreateTimer();
        _createRetryTimer.Interval = RetryInterval;
        _createRetryTimer.IsRepeating = true;
        _createRetryTimer.Tick += (_, _) => TryApplyTrayIcon();
        _createRetryTimer.Start();
    }

    private void StopRetryTimer()
    {
        if (_createRetryTimer is null)
        {
            return;
        }

        _createRetryTimer.Stop();
        _createRetryTimer = null;
    }

    private static string FormatMenuLabel(string title, HotkeyGesture? gesture) =>
        gesture is null || gesture.IsEmpty ? title : $"{title} ({gesture.DisplayText})";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopRetryTimer();
        _backend.Dispose();
    }
}
