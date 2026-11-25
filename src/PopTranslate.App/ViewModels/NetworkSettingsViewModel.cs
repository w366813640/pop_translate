using System.Diagnostics;
using PopTranslate.App.Services;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;

namespace PopTranslate.App.ViewModels;

public sealed partial class NetworkSettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _settingsStore;
    private readonly ISecretProtector _secretProtector;
    private readonly IStartupPrivilegeService _startupPrivilegeService;
    private CancellationTokenSource? _autoSaveDebounceCts;
    private bool _isLoading;
    private bool _isDirty;

    public NetworkSettingsViewModel(
        ISettingsStore settingsStore,
        ISecretProtector secretProtector,
        IStartupPrivilegeService startupPrivilegeService)
    {
        _settingsStore = settingsStore;
        _secretProtector = secretProtector;
        _startupPrivilegeService = startupPrivilegeService;
    }

    public event EventHandler? RestartAsAdministratorRequested;

    public event EventHandler<bool>? ElevatedStartupToggled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomProxyMode))]
    public partial int ProxyModeIndex { get; set; }

    [ObservableProperty]
    public partial string Host { get; set; } = "127.0.0.1";

    [ObservableProperty]
    public partial double Port { get; set; } = 7890;

    [ObservableProperty]
    public partial string Username { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PasswordPlaintext { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool BypassLocalAddresses { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProxyFeedback))]
    public partial string? ProxyFeedbackMessage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProxyFeedbackSeverity))]
    public partial bool IsProxyFeedbackError { get; set; }

    // Privilege & Elevated Task Scheduler section
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrivilegeStatusLabel))]
    [NotifyPropertyChangedFor(nameof(PrivilegeStatusDescription))]
    [NotifyPropertyChangedFor(nameof(CanRestartAsAdministrator))]
    public partial bool IsRunningAsAdministrator { get; set; }

    [ObservableProperty]
    public partial bool IsElevatedStartupTaskEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsUpdatingElevatedTask { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrivilegeFeedback))]
    public partial string? PrivilegeFeedbackMessage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrivilegeFeedbackSeverity))]
    public partial bool IsPrivilegeFeedbackError { get; set; }

    public bool IsCustomProxyMode => ProxyModeIndex is (int)ProxyMode.Http or (int)ProxyMode.Socks5;

    public bool HasProxyFeedback => !string.IsNullOrWhiteSpace(ProxyFeedbackMessage);

    public InfoBarSeverity ProxyFeedbackSeverity => IsProxyFeedbackError ? InfoBarSeverity.Error : InfoBarSeverity.Success;

    public bool HasPrivilegeFeedback => !string.IsNullOrWhiteSpace(PrivilegeFeedbackMessage);

    public InfoBarSeverity PrivilegeFeedbackSeverity => IsPrivilegeFeedbackError ? InfoBarSeverity.Error : InfoBarSeverity.Success;

    public bool CanRestartAsAdministrator => !IsRunningAsAdministrator;

    public string PrivilegeStatusLabel => IsRunningAsAdministrator ? "管理员 (Administrator)" : "标准用户 (Standard)";

    public string PrivilegeStatusDescription => IsRunningAsAdministrator
        ? "当前已具备管理员权限，可正常捕获管理员终端、IDE 或任务管理器等高权限窗口中的划词文本，并可配置任务计划免 UAC 自启。"
        : "默认以标准用户安全运行。如需在管理员权限窗口中划词取词，或配置免 UAC 提权开机自启，请先以管理员身份重启 PopTranslate。";

    public async Task LoadFromAsync(AppSettings settings)
    {
        _isLoading = true;
        try
        {
            ProxyModeIndex = (int)settings.Proxy.Mode;
            Host = string.IsNullOrWhiteSpace(settings.Proxy.Host) ? "127.0.0.1" : settings.Proxy.Host;
            Port = Math.Clamp(settings.Proxy.Port, 1, 65535);
            Username = settings.Proxy.Username;
            BypassLocalAddresses = settings.Proxy.BypassLocalAddresses;

            PasswordPlaintext = string.Empty;
            if (!string.IsNullOrWhiteSpace(settings.Proxy.EncryptedPassword) &&
                _secretProtector.TryUnprotect(settings.Proxy.EncryptedPassword, out var decrypted))
            {
                PasswordPlaintext = decrypted;
            }

            try
            {
                IsRunningAsAdministrator = _startupPrivilegeService.IsRunningAsAdministrator();
            }
            catch
            {
                IsRunningAsAdministrator = false;
            }

            IsElevatedStartupTaskEnabled = settings.UseElevatedStartupTask;
            _isDirty = false;
            ProxyFeedbackMessage = null;
            IsProxyFeedbackError = false;
            PrivilegeFeedbackMessage = null;
            IsPrivilegeFeedbackError = false;
        }
        finally
        {
            _isLoading = false;
        }

        // Query Task Scheduler off the UI thread so opening Settings never stalls on schtasks.exe.
        bool taskEnabled;
        try
        {
            taskEnabled = await Task.Run(() => _startupPrivilegeService.IsElevatedStartupTaskEnabled());
        }
        catch
        {
            taskEnabled = settings.UseElevatedStartupTask;
        }

        _isLoading = true;
        try
        {
            IsElevatedStartupTaskEnabled = taskEnabled;
        }
        finally
        {
            _isLoading = false;
        }
    }

    public void SyncElevatedStartupFromExternal(bool enabled)
    {
        _isLoading = true;
        try
        {
            IsElevatedStartupTaskEnabled = enabled;
        }
        finally
        {
            _isLoading = false;
        }
    }

    public async Task FlushIfDirtyAsync()
    {
        CancelAutoSaveDebounce();
        if (!_isDirty || _isLoading)
        {
            return;
        }

        await SaveProxySettingsCoreAsync(successMessage: null);
    }

    [RelayCommand]
    private Task SaveProxySettingsAsync()
    {
        CancelAutoSaveDebounce();
        return SaveProxySettingsCoreAsync("网络代理配置已保存并立即生效。");
    }

    [RelayCommand]
    private void RestartAsAdministrator()
    {
        RestartAsAdministratorRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void OpenLogsDirectory()
    {
        try
        {
            var logsDir = AppDiagnosticsLogger.LogDirectoryPath;
            Directory.CreateDirectory(logsDir);
            using var _ = Process.Start("explorer.exe", $"\"{logsDir}\"");
        }
        catch (Exception ex)
        {
            IsPrivilegeFeedbackError = true;
            PrivilegeFeedbackMessage = $"打开运行日志目录失败：{ex.Message}";
        }
    }

    partial void OnProxyModeIndexChanged(int value)
    {
        if (!_isLoading)
        {
            _ = SaveProxySettingsAsync();
        }
    }

    partial void OnHostChanged(string value) => MarkDirtyIfReady();

    partial void OnPortChanged(double value) => MarkDirtyIfReady();

    partial void OnUsernameChanged(string value) => MarkDirtyIfReady();

    partial void OnPasswordPlaintextChanged(string value) => MarkDirtyIfReady();

    partial void OnBypassLocalAddressesChanged(bool value)
    {
        if (!_isLoading)
        {
            _ = SaveProxySettingsCoreAsync(successMessage: null);
        }
    }

    partial void OnIsElevatedStartupTaskEnabledChanged(bool value)
    {
        if (_isLoading || IsUpdatingElevatedTask)
        {
            return;
        }

        _ = ApplyElevatedStartupTaskAsync(value);
    }

    private void MarkDirtyIfReady()
    {
        if (!_isLoading)
        {
            _isDirty = true;
            CancelAutoSaveDebounce();
            var cts = new CancellationTokenSource();
            _autoSaveDebounceCts = cts;
            _ = PersistDebouncedAsync(cts.Token);
        }
    }

    private async Task PersistDebouncedAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(250, ct);
            if (!ct.IsCancellationRequested && _isDirty)
            {
                await SaveProxySettingsCoreAsync(successMessage: null);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by newer edit or immediate flush.
        }
    }

    private void CancelAutoSaveDebounce()
    {
        if (_autoSaveDebounceCts is not null)
        {
            try { _autoSaveDebounceCts.Cancel(); } catch { }
            _autoSaveDebounceCts.Dispose();
            _autoSaveDebounceCts = null;
        }
    }

    private async Task SaveProxySettingsCoreAsync(string? successMessage)
    {
        try
        {
            var updated = _settingsStore.Current.Clone();
            updated.Proxy.Mode = ProxyModeIndex switch
            {
                (int)ProxyMode.Direct => ProxyMode.Direct,
                (int)ProxyMode.Http => ProxyMode.Http,
                (int)ProxyMode.Socks5 => ProxyMode.Socks5,
                _ => ProxyMode.System,
            };
            updated.Proxy.Host = Host?.Trim() ?? string.Empty;
            updated.Proxy.Port = double.IsNaN(Port) ? 7890 : Math.Clamp((int)Math.Round(Port), 1, 65535);
            updated.Proxy.Username = Username?.Trim() ?? string.Empty;
            updated.Proxy.EncryptedPassword = string.IsNullOrWhiteSpace(PasswordPlaintext)
                ? string.Empty
                : _secretProtector.Protect(PasswordPlaintext);
            updated.Proxy.BypassLocalAddresses = BypassLocalAddresses;

            await _settingsStore.SaveAsync(updated);
            _isDirty = false;
            if (!string.IsNullOrWhiteSpace(successMessage))
            {
                IsProxyFeedbackError = false;
                ProxyFeedbackMessage = successMessage;
            }
        }
        catch (Exception ex)
        {
            IsProxyFeedbackError = true;
            ProxyFeedbackMessage = $"保存代理配置失败：{ex.Message}";
        }
    }

    private async Task ApplyElevatedStartupTaskAsync(bool enabled)
    {
        IsUpdatingElevatedTask = true;
        try
        {
            var exePath = AppAssetHelper.ResolveEntryExecutablePath();
            var result = await _startupPrivilegeService.SetElevatedStartupTaskAsync(enabled, exePath);

            if (!result.Succeeded)
            {
                // Revert toggle without re-triggering recursion.
                _isLoading = true;
                try
                {
                    IsElevatedStartupTaskEnabled = !enabled;
                }
                finally
                {
                    _isLoading = false;
                }

                IsPrivilegeFeedbackError = true;
                PrivilegeFeedbackMessage = result.RequiresElevation
                    ? "配置任务计划免 UAC 提权自启需要管理员权限，请先点击「以管理员身份重启」。"
                    : (string.IsNullOrWhiteSpace(result.Message) ? "配置提权开机自启任务失败。" : result.Message);
                return;
            }

            if (enabled && !string.IsNullOrWhiteSpace(exePath))
            {
                try
                {
                    _startupPrivilegeService.SetStandardStartup(false, exePath);
                }
                catch
                {
                    // Best-effort HKCU cleanup alongside task registration.
                }
            }

            var updated = _settingsStore.Current.Clone();
            updated.UseElevatedStartupTask = enabled;
            if (enabled)
            {
                updated.LaunchAtStartup = false;
            }

            await _settingsStore.SaveAsync(updated);
            ElevatedStartupToggled?.Invoke(this, enabled);

            IsPrivilegeFeedbackError = false;
            PrivilegeFeedbackMessage = enabled
                ? "已通过 Windows 任务计划程序创建免 UAC 提权开机自启任务（标准开机自启已同步互斥关闭）。"
                : "已移除免 UAC 提权开机自启任务。";
        }
        catch (Exception ex)
        {
            _isLoading = true;
            try
            {
                IsElevatedStartupTaskEnabled = !enabled;
            }
            finally
            {
                _isLoading = false;
            }

            IsPrivilegeFeedbackError = true;
            PrivilegeFeedbackMessage = $"操作提权开机自启失败：{ex.Message}";
        }
        finally
        {
            IsUpdatingElevatedTask = false;
        }
    }
}
