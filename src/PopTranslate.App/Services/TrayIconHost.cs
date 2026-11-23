using System.Drawing;
using PopTranslate.Core.Models;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace PopTranslate.App.Services;

/// <summary>
/// Manages the persistent Windows 11 system tray icon and context menu using H.NotifyIcon.WinUI.
/// Resilient to Explorer / Shell_TrayWnd unavailability at early startup with automatic retry.
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(3);

    private TaskbarIcon? _taskbarIcon;
    private Icon? _loadedIcon;
    private MenuFlyoutItem? _inputTranslateItem;
    private MenuFlyoutItem? _polishInputItem;
    private MenuFlyoutItem? _screenshotTranslateItem;
    private ToggleMenuFlyoutItem? _hoverIconToggleItem;
    private MenuFlyoutItem? _restartAsAdminItem;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _createRetryTimer;
    private string _latestToolTipText = "PopTranslate 翻译";
    private bool _isUpdatingState;
    private bool _disposed;

    public event EventHandler? InputTranslateRequested;
    public event EventHandler? PolishInputRequested;
    public event EventHandler? ScreenshotTranslateRequested;
    public event EventHandler<bool>? HoverIconToggled;
    public event EventHandler? SettingsRequested;
    public event EventHandler? RestartAsAdministratorRequested;
    public event EventHandler? ExitRequested;

    public void Initialize(
        AppSettings settings,
        bool isRunningAsAdministrator,
        DispatcherQueue? uiDispatcher = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_taskbarIcon is not null)
        {
            UpdateState(settings, isRunningAsAdministrator);
            return;
        }

        _latestToolTipText = isRunningAsAdministrator ? "PopTranslate 翻译 (管理员)" : "PopTranslate 翻译";

        var flyout = new MenuFlyout();

        _inputTranslateItem = new MenuFlyoutItem
        {
            Text = FormatMenuLabel("输入翻译", settings.Hotkeys.InputTranslate),
            Icon = new FontIcon { Glyph = "\uE8C1" },
        };
        _inputTranslateItem.Click += (_, _) => InputTranslateRequested?.Invoke(this, EventArgs.Empty);

        _polishInputItem = new MenuFlyoutItem
        {
            Text = "文本润色",
            Icon = new FontIcon { Glyph = "\uE70F" },
        };
        _polishInputItem.Click += (_, _) => PolishInputRequested?.Invoke(this, EventArgs.Empty);

        _screenshotTranslateItem = new MenuFlyoutItem
        {
            Text = FormatMenuLabel("截图翻译", settings.Hotkeys.ScreenshotTranslate),
            Icon = new FontIcon { Glyph = "\uE722" },
        };
        _screenshotTranslateItem.Click += (_, _) => ScreenshotTranslateRequested?.Invoke(this, EventArgs.Empty);

        _hoverIconToggleItem = new ToggleMenuFlyoutItem
        {
            Text = "划词悬浮图标",
            IsChecked = settings.Selection.EnableHoverIcon,
            Icon = new FontIcon { Glyph = "\uE7C9" },
        };
        _hoverIconToggleItem.Click += (_, _) =>
        {
            if (_isUpdatingState || _hoverIconToggleItem is null)
            {
                return;
            }

            HoverIconToggled?.Invoke(this, _hoverIconToggleItem.IsChecked);
        };

        var settingsItem = new MenuFlyoutItem
        {
            Text = "设置",
            Icon = new FontIcon { Glyph = "\uE713" },
        };
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        _restartAsAdminItem = new MenuFlyoutItem
        {
            Text = isRunningAsAdministrator ? "已以管理员身份运行" : "以管理员身份重启",
            IsEnabled = !isRunningAsAdministrator,
            Icon = new FontIcon { Glyph = "\uE7EF" },
        };
        _restartAsAdminItem.Click += (_, _) => RestartAsAdministratorRequested?.Invoke(this, EventArgs.Empty);

        var exitItem = new MenuFlyoutItem
        {
            Text = "退出",
            Icon = new FontIcon { Glyph = "\uE7E8" },
        };
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        flyout.Items.Add(_inputTranslateItem);
        flyout.Items.Add(_polishInputItem);
        flyout.Items.Add(_screenshotTranslateItem);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(_hoverIconToggleItem);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(settingsItem);
        flyout.Items.Add(_restartAsAdminItem);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(exitItem);

        _taskbarIcon = new TaskbarIcon
        {
            ToolTipText = _latestToolTipText,
            ContextMenuMode = ContextMenuMode.PopupMenu,
            NoLeftClickDelay = true,
            ContextFlyout = flyout,
            LeftClickCommand = new RelayCommand(() => InputTranslateRequested?.Invoke(this, EventArgs.Empty)),
            DoubleClickCommand = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty)),
        };

        TryLoadTrayIcon(_taskbarIcon);

        if (!TryForceCreateTrayIcon())
        {
            StartRetryTimer(uiDispatcher ?? DispatcherQueue.GetForCurrentThread());
        }
    }

    public void UpdateState(AppSettings settings, bool isRunningAsAdministrator)
    {
        if (_disposed || _taskbarIcon is null)
        {
            return;
        }

        _isUpdatingState = true;
        try
        {
            _latestToolTipText = isRunningAsAdministrator ? "PopTranslate 翻译 (管理员)" : "PopTranslate 翻译";

            if (_taskbarIcon.IsCreated)
            {
                try
                {
                    _taskbarIcon.ToolTipText = _latestToolTipText;
                }
                catch
                {
                    // Ignore transient Shell_NotifyIconW errors if Explorer is restarting.
                }
            }

            if (_inputTranslateItem is not null)
            {
                _inputTranslateItem.Text = FormatMenuLabel("输入翻译", settings.Hotkeys.InputTranslate);
            }

            if (_screenshotTranslateItem is not null)
            {
                _screenshotTranslateItem.Text = FormatMenuLabel("截图翻译", settings.Hotkeys.ScreenshotTranslate);
            }

            if (_hoverIconToggleItem is not null)
            {
                _hoverIconToggleItem.IsChecked = settings.Selection.EnableHoverIcon;
            }

            if (_restartAsAdminItem is not null)
            {
                _restartAsAdminItem.Text = isRunningAsAdministrator ? "已以管理员身份运行" : "以管理员身份重启";
                _restartAsAdminItem.IsEnabled = !isRunningAsAdministrator;
            }
        }
        finally
        {
            _isUpdatingState = false;
        }
    }

    private bool TryForceCreateTrayIcon()
    {
        if (_disposed || _taskbarIcon is null)
        {
            return false;
        }

        if (_taskbarIcon.IsCreated)
        {
            StopRetryTimer();
            return true;
        }

        try
        {
            // Keep full responsiveness (do not throttle with Efficiency Mode) while resident in tray.
            _taskbarIcon.ForceCreate(enablesEfficiencyMode: false);

            if (_taskbarIcon.IsCreated)
            {
                StopRetryTimer();
                try
                {
                    _taskbarIcon.ToolTipText = _latestToolTipText;
                }
                catch
                {
                    // Ignore tooltip sync failure on immediate creation.
                }

                return true;
            }
        }
        catch
        {
            // Shell_TrayWnd may not be ready yet (e.g., early logon auto-start or non-shell session).
            // App stays alive with hotkeys/hover/popups active while timer retries tray creation.
        }

        return false;
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
        _createRetryTimer.Tick += (_, _) => TryForceCreateTrayIcon();
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

    private void TryLoadTrayIcon(TaskbarIcon taskbarIcon)
    {
        try
        {
            var iconPath = AppAssetHelper.IconPath;
            if (File.Exists(iconPath))
            {
                _loadedIcon = new Icon(iconPath);
                taskbarIcon.Icon = _loadedIcon;
            }
        }
        catch
        {
            // Fallback gracefully if icon resource cannot be loaded.
        }
    }

    private static string FormatMenuLabel(string title, HotkeyGesture gesture) =>
        gesture.IsEmpty ? title : $"{title} ({gesture.DisplayText})";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopRetryTimer();

        try
        {
            _taskbarIcon?.Dispose();
        }
        catch
        {
            // Ignore disposal errors during process teardown.
        }

        _taskbarIcon = null;

        _loadedIcon?.Dispose();
        _loadedIcon = null;
    }
}
