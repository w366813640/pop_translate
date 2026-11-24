using System.Collections.ObjectModel;
using System.Diagnostics;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PopTranslate.App.ViewModels;

public sealed partial class HotkeysSettingsViewModel : ObservableObject
{
    private static readonly HashSet<string> IgnoredSystemWindowProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe",
        "ApplicationFrameHost.exe",
        "TextInputHost.exe",
        "SearchHost.exe",
        "StartMenuExperienceHost.exe",
        "ShellExperienceHost.exe",
        "SystemSettings.exe",
        "LockApp.exe",
        "dwm.exe",
        "sihost.exe",
        "taskhostw.exe",
        "ctfmon.exe",
        "PopTranslate.exe",
    };

    private readonly ISettingsStore _settingsStore;
    private bool _isLoading;

    public HotkeysSettingsViewModel(ISettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public ObservableCollection<string> ProcessBlacklist { get; } = [];

    [ObservableProperty]
    public partial HotkeyGesture SelectionTranslateGesture { get; set; } = HotkeyConfig.DefaultSelectionTranslate;

    [ObservableProperty]
    public partial HotkeyGesture InputTranslateGesture { get; set; } = HotkeyConfig.DefaultInputTranslate;

    [ObservableProperty]
    public partial HotkeyGesture ScreenshotTranslateGesture { get; set; } = HotkeyConfig.DefaultScreenshotTranslate;

    [ObservableProperty]
    public partial HotkeyGesture SilentOcrGesture { get; set; } = HotkeyConfig.DefaultSilentOcr;

    [ObservableProperty]
    public partial HotkeyGesture SelectionPolishGesture { get; set; } = HotkeyConfig.DefaultSelectionPolish;

    [ObservableProperty]
    public partial string? SelectionHotkeyError { get; set; }

    [ObservableProperty]
    public partial string? InputHotkeyError { get; set; }

    [ObservableProperty]
    public partial string? ScreenshotHotkeyError { get; set; }

    [ObservableProperty]
    public partial string? SilentOcrHotkeyError { get; set; }

    [ObservableProperty]
    public partial string? SelectionPolishHotkeyError { get; set; }

    [ObservableProperty]
    public partial bool EnableDoubleTapCopy { get; set; }

    [ObservableProperty]
    public partial bool EnableHoverIcon { get; set; } = true;

    [ObservableProperty]
    public partial bool RequireCtrlForHover { get; set; }

    [ObservableProperty]
    public partial bool EnableClipboardFallback { get; set; } = true;

    [ObservableProperty]
    public partial bool QuietModeInFullscreen { get; set; } = true;

    [ObservableProperty]
    public partial double HoverIconAutoDismissMs { get; set; } = 4000;

    [ObservableProperty]
    public partial double MinDragDistancePixels { get; set; } = 12;

    [ObservableProperty]
    public partial double ClipboardWaitTimeoutMs { get; set; } = 250;

    [ObservableProperty]
    public partial string NewBlacklistProcessName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    public partial string? StatusMessage { get; set; }

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public void LoadFrom(AppSettings settings)
    {
        _isLoading = true;
        try
        {
            SelectionTranslateGesture = settings.Hotkeys.SelectionTranslate;
            InputTranslateGesture = settings.Hotkeys.InputTranslate;
            ScreenshotTranslateGesture = settings.Hotkeys.ScreenshotTranslate;
            SilentOcrGesture = settings.Hotkeys.SilentOcr;
            SelectionPolishGesture = settings.Hotkeys.SelectionPolish;
            EnableDoubleTapCopy = settings.Hotkeys.EnableDoubleTapCopy;

            EnableHoverIcon = settings.Selection.EnableHoverIcon;
            RequireCtrlForHover = settings.Selection.RequireCtrlForHover;
            EnableClipboardFallback = settings.Selection.EnableClipboardFallback;
            QuietModeInFullscreen = settings.Selection.QuietModeInFullscreen;
            HoverIconAutoDismissMs = Math.Clamp(settings.Selection.HoverIconAutoDismissMs, 1500, 15000);
            MinDragDistancePixels = Math.Clamp(settings.Selection.MinDragDistancePixels, 4, 80);
            ClipboardWaitTimeoutMs = Math.Clamp(settings.Selection.ClipboardWaitTimeoutMs, 100, 1000);

            ProcessBlacklist.Clear();
            foreach (var item in settings.Selection.ProcessBlacklist)
            {
                if (!string.IsNullOrWhiteSpace(item))
                {
                    ProcessBlacklist.Add(item.Trim());
                }
            }

            StatusMessage = null;
        }
        finally
        {
            _isLoading = false;
        }
    }

    public void UpdateRegistrationResults(IReadOnlyDictionary<HotkeyAction, HotkeyRegistrationResult> results)
    {
        SelectionHotkeyError = ExtractError(results, HotkeyAction.SelectionTranslate);
        InputHotkeyError = ExtractError(results, HotkeyAction.InputTranslate);
        ScreenshotHotkeyError = ExtractError(results, HotkeyAction.ScreenshotTranslate);
        SilentOcrHotkeyError = ExtractError(results, HotkeyAction.SilentOcr);
        SelectionPolishHotkeyError = ExtractError(results, HotkeyAction.SelectionPolish);
    }

    public void UpdateGesture(HotkeyAction action, HotkeyGesture gesture)
    {
        switch (action)
        {
            case HotkeyAction.SelectionTranslate:
                SelectionTranslateGesture = gesture;
                break;
            case HotkeyAction.InputTranslate:
                InputTranslateGesture = gesture;
                break;
            case HotkeyAction.ScreenshotTranslate:
                ScreenshotTranslateGesture = gesture;
                break;
            case HotkeyAction.SilentOcr:
                SilentOcrGesture = gesture;
                break;
            case HotkeyAction.SelectionPolish:
                SelectionPolishGesture = gesture;
                break;
        }

        SaveIfReady();
    }

    public IReadOnlyList<string> GetRunningWindowProcessNames()
    {
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentPid = Environment.ProcessId;
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return [];
        }

        foreach (var proc in processes)
        {
            try
            {
                if (proc.Id == currentPid || proc.MainWindowHandle == nint.Zero)
                {
                    continue;
                }

                var rawName = proc.ProcessName?.Trim();
                if (string.IsNullOrWhiteSpace(rawName))
                {
                    continue;
                }

                var exeName = rawName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? rawName
                    : $"{rawName}.exe";

                if (IgnoredSystemWindowProcesses.Contains(exeName))
                {
                    continue;
                }

                if (ProcessBlacklist.Any(x => string.Equals(x, exeName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                discovered.Add(exeName);
            }
            catch
            {
                // Ignore protected or exiting processes.
            }
            finally
            {
                proc.Dispose();
            }
        }

        return discovered.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    [RelayCommand]
    private void ResetDefaultHotkeys()
    {
        SelectionTranslateGesture = HotkeyConfig.DefaultSelectionTranslate;
        InputTranslateGesture = HotkeyConfig.DefaultInputTranslate;
        ScreenshotTranslateGesture = HotkeyConfig.DefaultScreenshotTranslate;
        SilentOcrGesture = HotkeyConfig.DefaultSilentOcr;
        SelectionPolishGesture = HotkeyConfig.DefaultSelectionPolish;
        StatusMessage = "已恢复默认全局快捷键（Alt+D / Alt+A / Alt+S / Alt+Shift+S / Alt+P）。";
        SaveIfReady();
    }

    [RelayCommand]
    private void AddBlacklistProcess()
    {
        var raw = NewBlacklistProcessName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        var normalized = raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? raw : $"{raw}.exe";
        if (!ProcessBlacklist.Any(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            ProcessBlacklist.Add(normalized);
            SaveIfReady();
        }

        NewBlacklistProcessName = string.Empty;
    }

    [RelayCommand]
    private void AddRunningBlacklistProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return;
        }

        var raw = processName.Trim();
        var normalized = raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? raw : $"{raw}.exe";
        if (!ProcessBlacklist.Any(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            ProcessBlacklist.Add(normalized);
            StatusMessage = $"已将「{normalized}」加入划词忽略黑名单。";
            SaveIfReady();
        }
    }

    [RelayCommand]
    private void RemoveBlacklistProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return;
        }

        if (ProcessBlacklist.Remove(processName))
        {
            SaveIfReady();
        }
    }

    [RelayCommand]
    private void ResetDefaultBlacklist()
    {
        ProcessBlacklist.Clear();
        foreach (var item in SelectionConfig.DefaultProcessBlacklist)
        {
            ProcessBlacklist.Add(item);
        }

        StatusMessage = "已恢复默认进程黑名单。";
        SaveIfReady();
    }

    partial void OnEnableDoubleTapCopyChanged(bool value) => SaveIfReady();
    partial void OnEnableHoverIconChanged(bool value) => SaveIfReady();
    partial void OnRequireCtrlForHoverChanged(bool value) => SaveIfReady();
    partial void OnEnableClipboardFallbackChanged(bool value) => SaveIfReady();
    partial void OnQuietModeInFullscreenChanged(bool value) => SaveIfReady();
    partial void OnHoverIconAutoDismissMsChanged(double value) => SaveIfReady();
    partial void OnMinDragDistancePixelsChanged(double value) => SaveIfReady();
    partial void OnClipboardWaitTimeoutMsChanged(double value) => SaveIfReady();

    private void SaveIfReady()
    {
        if (_isLoading)
        {
            return;
        }

        _ = PersistAsync();
    }

    private async Task PersistAsync()
    {
        try
        {
            var updated = _settingsStore.Current.Clone();
            updated.Hotkeys.SelectionTranslate = SelectionTranslateGesture;
            updated.Hotkeys.InputTranslate = InputTranslateGesture;
            updated.Hotkeys.ScreenshotTranslate = ScreenshotTranslateGesture;
            updated.Hotkeys.SilentOcr = SilentOcrGesture;
            updated.Hotkeys.SelectionPolish = SelectionPolishGesture;
            updated.Hotkeys.EnableDoubleTapCopy = EnableDoubleTapCopy;

            updated.Selection.EnableHoverIcon = EnableHoverIcon;
            updated.Selection.RequireCtrlForHover = RequireCtrlForHover;
            updated.Selection.EnableClipboardFallback = EnableClipboardFallback;
            updated.Selection.QuietModeInFullscreen = QuietModeInFullscreen;
            updated.Selection.HoverIconAutoDismissMs = double.IsNaN(HoverIconAutoDismissMs)
                ? 4000
                : Math.Clamp((int)Math.Round(HoverIconAutoDismissMs), 1500, 15000);
            updated.Selection.MinDragDistancePixels = double.IsNaN(MinDragDistancePixels)
                ? 12
                : Math.Clamp((int)Math.Round(MinDragDistancePixels), 4, 80);
            updated.Selection.ClipboardWaitTimeoutMs = double.IsNaN(ClipboardWaitTimeoutMs)
                ? 250
                : Math.Clamp((int)Math.Round(ClipboardWaitTimeoutMs), 100, 1000);
            updated.Selection.ProcessBlacklist = ProcessBlacklist.ToList();

            await _settingsStore.SaveAsync(updated);
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存快捷键与划词配置失败：{ex.Message}";
        }
    }

    private static string? ExtractError(
        IReadOnlyDictionary<HotkeyAction, HotkeyRegistrationResult> results,
        HotkeyAction action)
    {
        if (results.TryGetValue(action, out var result) && !result.Succeeded)
        {
            return string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? "快捷键注册失败，可能已被其他应用占用。"
                : result.ErrorMessage;
        }

        return null;
    }
}
