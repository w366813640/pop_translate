using System.Collections.ObjectModel;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PopTranslate.App.ViewModels;

public sealed partial class HotkeysSettingsViewModel : ObservableObject
{
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
    public partial HotkeyGesture SelectionPolishGesture { get; set; } = HotkeyConfig.DefaultSelectionPolish;

    [ObservableProperty]
    public partial string? SelectionHotkeyError { get; set; }

    [ObservableProperty]
    public partial string? InputHotkeyError { get; set; }

    [ObservableProperty]
    public partial string? ScreenshotHotkeyError { get; set; }

    [ObservableProperty]
    public partial string? SelectionPolishHotkeyError { get; set; }

    [ObservableProperty]
    public partial bool EnableHoverIcon { get; set; } = true;

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
            SelectionPolishGesture = settings.Hotkeys.SelectionPolish;

            EnableHoverIcon = settings.Selection.EnableHoverIcon;
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
            case HotkeyAction.SelectionPolish:
                SelectionPolishGesture = gesture;
                break;
        }

        SaveIfReady();
    }

    [RelayCommand]
    private void ResetDefaultHotkeys()
    {
        SelectionTranslateGesture = HotkeyConfig.DefaultSelectionTranslate;
        InputTranslateGesture = HotkeyConfig.DefaultInputTranslate;
        ScreenshotTranslateGesture = HotkeyConfig.DefaultScreenshotTranslate;
        SelectionPolishGesture = HotkeyConfig.DefaultSelectionPolish;
        StatusMessage = "已恢复默认全局快捷键（Alt+D / Alt+A / Alt+S / Alt+P）。";
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

    partial void OnEnableHoverIconChanged(bool value) => SaveIfReady();
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
            updated.Hotkeys.SelectionPolish = SelectionPolishGesture;

            updated.Selection.EnableHoverIcon = EnableHoverIcon;
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
