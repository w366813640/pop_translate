using System.Collections.ObjectModel;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PopTranslate.App.ViewModels;

public sealed partial class GeneralSettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _settingsStore;
    private readonly IStartupPrivilegeService _startupPrivilegeService;
    private CancellationTokenSource? _promptSaveDebounceCts;
    private bool _isLoading;
    private bool _isDirty;

    public GeneralSettingsViewModel(
        ISettingsStore settingsStore,
        IStartupPrivilegeService startupPrivilegeService)
    {
        _settingsStore = settingsStore;
        _startupPrivilegeService = startupPrivilegeService;

        TargetLanguages = new ObservableCollection<LanguageOption>(
            LanguageCode.SelectableTargets.Select(l => new LanguageOption(l, l.Label)));
        PolishStyles = new ObservableCollection<PolishStyleOption>(PolishStyleOption.CreatePresets());

        PrimaryTargetLanguage = TargetLanguages.First(x => x.Code == LanguageCode.ChineseSimplified);
        SecondaryTargetLanguage = TargetLanguages.First(x => x.Code == LanguageCode.English);
        DefaultPolishStyle = PolishStyles[0];
    }

    public event EventHandler<bool>? StandardStartupToggled;

    public ObservableCollection<LanguageOption> TargetLanguages { get; }

    public ObservableCollection<PolishStyleOption> PolishStyles { get; }

    [ObservableProperty]
    public partial LanguageOption PrimaryTargetLanguage { get; set; }

    [ObservableProperty]
    public partial LanguageOption SecondaryTargetLanguage { get; set; }

    [ObservableProperty]
    public partial bool MergePdfLineBreaks { get; set; } = true;

    [ObservableProperty]
    public partial bool AutoBackTranslate { get; set; }

    [ObservableProperty]
    public partial bool EnableSingleWordDictionary { get; set; } = true;

    [ObservableProperty]
    public partial bool EnableCodeIdentifierSplit { get; set; } = true;

    [ObservableProperty]
    public partial string GlossaryText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial PolishStyleOption DefaultPolishStyle { get; set; }

    [ObservableProperty]
    public partial bool ShowPolishDiffByDefault { get; set; }

    [ObservableProperty]
    public partial string CustomPolishPrompt { get; set; } = PolishConfig.DefaultCustomPrompt;

    [ObservableProperty]
    public partial int PositionModeIndex { get; set; }

    [ObservableProperty]
    public partial double PopupWidth { get; set; } = 440;

    [ObservableProperty]
    public partial double PopupMaxHeight { get; set; } = 640;

    [ObservableProperty]
    public partial bool PinByDefault { get; set; }

    [ObservableProperty]
    public partial bool HideOnClickOutside { get; set; } = true;

    [ObservableProperty]
    public partial bool HideOnEscape { get; set; } = true;

    [ObservableProperty]
    public partial bool AutoCopyFirstResult { get; set; }

    [ObservableProperty]
    public partial int ThemeModeIndex { get; set; }

    [ObservableProperty]
    public partial bool LaunchAtStartup { get; set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    public void LoadFrom(AppSettings settings)
    {
        CancelPromptDebounce();
        _isLoading = true;
        try
        {
            PrimaryTargetLanguage = TargetLanguages.FirstOrDefault(x => x.Code == settings.PrimaryTargetLanguage)
                ?? TargetLanguages[0];
            SecondaryTargetLanguage = TargetLanguages.FirstOrDefault(x => x.Code == settings.SecondaryTargetLanguage)
                ?? TargetLanguages.FirstOrDefault(x => x.Code == LanguageCode.English)
                ?? TargetLanguages[0];

            MergePdfLineBreaks = settings.MergePdfLineBreaks;
            AutoBackTranslate = settings.AutoBackTranslate;
            EnableSingleWordDictionary = settings.EnableSingleWordDictionary;
            EnableCodeIdentifierSplit = settings.EnableCodeIdentifierSplit;
            GlossaryText = FormatGlossaryText(settings.Glossary);

            var polish = settings.Polish ?? new PolishConfig();
            DefaultPolishStyle = PolishStyles.FirstOrDefault(x => x.Style == polish.DefaultStyle)
                ?? PolishStyles[0];
            ShowPolishDiffByDefault = polish.ShowDiffByDefault;
            CustomPolishPrompt = string.IsNullOrWhiteSpace(polish.CustomPrompt)
                ? PolishConfig.DefaultCustomPrompt
                : polish.CustomPrompt;

            PositionModeIndex = (int)settings.Window.PositionMode;
            PopupWidth = Math.Clamp(settings.Window.PopupWidth, 340, 760);
            PopupMaxHeight = Math.Clamp(settings.Window.PopupMaxHeight, 300, 1000);
            PinByDefault = settings.Window.PinByDefault;
            HideOnClickOutside = settings.Window.HideOnClickOutside;
            HideOnEscape = settings.Window.HideOnEscape;
            AutoCopyFirstResult = settings.Window.AutoCopyFirstResult;
            ThemeModeIndex = (int)settings.Window.Theme;

            try
            {
                LaunchAtStartup = _startupPrivilegeService.IsStandardStartupEnabled();
            }
            catch
            {
                LaunchAtStartup = settings.LaunchAtStartup;
            }

            _isDirty = false;
            StatusMessage = null;
        }
        finally
        {
            _isLoading = false;
        }
    }

    public void SyncStandardStartupFromExternal(bool enabled)
    {
        _isLoading = true;
        try
        {
            LaunchAtStartup = enabled;
        }
        finally
        {
            _isLoading = false;
        }
    }

    public async Task FlushIfDirtyAsync()
    {
        if (!_isDirty || _isLoading)
        {
            return;
        }

        CancelPromptDebounce();
        await PersistAsync();
    }

    [RelayCommand]
    private void ResetDefaultCustomPrompt()
    {
        CancelPromptDebounce();
        _isLoading = true;
        try
        {
            CustomPolishPrompt = PolishConfig.DefaultCustomPrompt;
        }
        finally
        {
            _isLoading = false;
        }

        StatusMessage = "已恢复默认自定义润色 Prompt。";
        SaveIfReady();
    }

    partial void OnPrimaryTargetLanguageChanged(LanguageOption value) => SaveIfReady();
    partial void OnSecondaryTargetLanguageChanged(LanguageOption value) => SaveIfReady();
    partial void OnMergePdfLineBreaksChanged(bool value) => SaveIfReady();
    partial void OnAutoBackTranslateChanged(bool value) => SaveIfReady();
    partial void OnEnableSingleWordDictionaryChanged(bool value) => SaveIfReady();
    partial void OnEnableCodeIdentifierSplitChanged(bool value) => SaveIfReady();
    partial void OnDefaultPolishStyleChanged(PolishStyleOption value) => SaveIfReady();
    partial void OnShowPolishDiffByDefaultChanged(bool value) => SaveIfReady();
    partial void OnPositionModeIndexChanged(int value) => SaveIfReady();
    partial void OnPopupWidthChanged(double value) => SaveIfReady();
    partial void OnPopupMaxHeightChanged(double value) => SaveIfReady();
    partial void OnPinByDefaultChanged(bool value) => SaveIfReady();
    partial void OnHideOnClickOutsideChanged(bool value) => SaveIfReady();
    partial void OnHideOnEscapeChanged(bool value) => SaveIfReady();
    partial void OnAutoCopyFirstResultChanged(bool value) => SaveIfReady();
    partial void OnThemeModeIndexChanged(int value) => SaveIfReady();

    partial void OnGlossaryTextChanged(string value) => ScheduleDebouncedSave();

    partial void OnCustomPolishPromptChanged(string value) => ScheduleDebouncedSave();

    partial void OnLaunchAtStartupChanged(bool value)
    {
        if (_isLoading)
        {
            return;
        }

        try
        {
            var exePath = Environment.ProcessPath ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                _startupPrivilegeService.SetStandardStartup(value, exePath);
            }

            StandardStartupToggled?.Invoke(this, value);
            StatusMessage = value
                ? "已启用当前用户开机自启（提权任务计划已同步互斥关闭）。"
                : "已关闭当前用户开机自启。";
            SaveIfReady();
        }
        catch (Exception ex)
        {
            _isLoading = true;
            try
            {
                LaunchAtStartup = !value;
            }
            finally
            {
                _isLoading = false;
            }

            StatusMessage = $"设置开机自启失败：{ex.Message}";
        }
    }

    private void ScheduleDebouncedSave()
    {
        if (_isLoading)
        {
            return;
        }

        _isDirty = true;
        CancelPromptDebounce();
        var cts = new CancellationTokenSource();
        _promptSaveDebounceCts = cts;
        _ = PersistDebouncedAsync(cts.Token);
    }

    private void SaveIfReady()
    {
        if (_isLoading)
        {
            return;
        }

        CancelPromptDebounce();
        _ = PersistAsync();
    }

    private async Task PersistDebouncedAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(300, ct);
            if (!ct.IsCancellationRequested)
            {
                await PersistAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by newer edit or immediate save.
        }
    }

    private void CancelPromptDebounce()
    {
        if (_promptSaveDebounceCts is not null)
        {
            try
            {
                _promptSaveDebounceCts.Cancel();
            }
            catch
            {
                // Ignore cancellation race.
            }

            _promptSaveDebounceCts.Dispose();
            _promptSaveDebounceCts = null;
        }
    }

    private async Task PersistAsync()
    {
        try
        {
            var updated = _settingsStore.Current.Clone();
            updated.PrimaryTargetLanguage = PrimaryTargetLanguage.Code;
            updated.SecondaryTargetLanguage = SecondaryTargetLanguage.Code;
            updated.MergePdfLineBreaks = MergePdfLineBreaks;
            updated.AutoBackTranslate = AutoBackTranslate;
            updated.EnableSingleWordDictionary = EnableSingleWordDictionary;
            updated.EnableCodeIdentifierSplit = EnableCodeIdentifierSplit;
            updated.Glossary = ParseGlossaryText(GlossaryText);
            updated.LaunchAtStartup = LaunchAtStartup;
            if (LaunchAtStartup)
            {
                updated.UseElevatedStartupTask = false;
            }

            updated.Polish ??= new PolishConfig();
            updated.Polish.DefaultStyle = DefaultPolishStyle?.Style ?? PolishStyle.General;
            updated.Polish.ShowDiffByDefault = ShowPolishDiffByDefault;
            updated.Polish.CustomPrompt = string.IsNullOrWhiteSpace(CustomPolishPrompt)
                ? PolishConfig.DefaultCustomPrompt
                : CustomPolishPrompt;

            updated.Window.PositionMode = PositionModeIndex == (int)WindowPositionMode.RememberFixedPosition
                ? WindowPositionMode.RememberFixedPosition
                : WindowPositionMode.FollowCursor;
            updated.Window.PopupWidth = double.IsNaN(PopupWidth) ? 440 : Math.Clamp(PopupWidth, 340, 760);
            updated.Window.PopupMaxHeight = double.IsNaN(PopupMaxHeight) ? 640 : Math.Clamp(PopupMaxHeight, 300, 1000);
            updated.Window.PinByDefault = PinByDefault;
            updated.Window.HideOnClickOutside = HideOnClickOutside;
            updated.Window.HideOnEscape = HideOnEscape;
            updated.Window.AutoCopyFirstResult = AutoCopyFirstResult;
            updated.Window.Theme = ThemeModeIndex switch
            {
                (int)AppThemeMode.Light => AppThemeMode.Light,
                (int)AppThemeMode.Dark => AppThemeMode.Dark,
                _ => AppThemeMode.System,
            };

            await _settingsStore.SaveAsync(updated);
            _isDirty = false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存常规设置失败：{ex.Message}";
        }
    }

    private static string FormatGlossaryText(IReadOnlyList<GlossaryEntry>? glossary)
    {
        if (glossary is null || glossary.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            Environment.NewLine,
            glossary
                .Where(g => g is not null && !string.IsNullOrWhiteSpace(g.SourceTerm) && !string.IsNullOrWhiteSpace(g.TargetTerm))
                .Select(g => $"{g.SourceTerm.Trim()} = {g.TargetTerm.Trim()}"));
    }

    private static List<GlossaryEntry> ParseGlossaryText(string? rawText)
    {
        var entries = new List<GlossaryEntry>();
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return entries;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = rawText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var sepIndex = line.IndexOf('=');
            if (sepIndex < 0)
            {
                sepIndex = line.IndexOf('＝');
            }

            if (sepIndex <= 0 || sepIndex >= line.Length - 1)
            {
                continue;
            }

            var source = line[..sepIndex].Trim();
            var target = line[(sepIndex + 1)..].Trim();
            if (source.Length == 0 || target.Length == 0)
            {
                continue;
            }

            if (seen.Add(source))
            {
                entries.Add(new GlossaryEntry
                {
                    SourceTerm = source,
                    TargetTerm = target,
                });
            }
        }

        return entries;
    }
}
