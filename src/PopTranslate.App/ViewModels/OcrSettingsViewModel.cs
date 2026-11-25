using System.Collections.ObjectModel;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;

namespace PopTranslate.App.ViewModels;

public sealed record OcrLanguageOption(string LanguageTag, string DisplayLabel)
{
    public override string ToString() => DisplayLabel;
}

public sealed partial class OcrSettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _settingsStore;
    private readonly ISecretProtector _secretProtector;
    private readonly IOcrOrchestrator _ocrOrchestrator;
    private readonly IModelDiscoveryService _modelDiscoveryService;
    private CancellationTokenSource? _autoSaveDebounceCts;
    private bool _isLoading;
    private bool _isDirty;
    private bool _suppressDiscoveredModelSync;

    public OcrSettingsViewModel(
        ISettingsStore settingsStore,
        ISecretProtector secretProtector,
        IOcrOrchestrator ocrOrchestrator,
        IModelDiscoveryService modelDiscoveryService)
    {
        _settingsStore = settingsStore;
        _secretProtector = secretProtector;
        _ocrOrchestrator = ocrOrchestrator;
        _modelDiscoveryService = modelDiscoveryService;

        _isLoading = true;
        try
        {
            PreferredLanguageOptions.Add(new OcrLanguageOption(string.Empty, "自动（跟随 Windows 用户语言）"));
            SelectedPreferredLanguage = PreferredLanguageOptions[0];
        }
        finally
        {
            _isLoading = false;
        }
    }

    public ObservableCollection<OcrLanguagePackInfo> InstalledLanguagePacks { get; } = [];

    public ObservableCollection<OcrLanguageOption> PreferredLanguageOptions { get; } = [];

    public ObservableCollection<string> DiscoveredModels { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWindowsNativeSelected))]
    [NotifyPropertyChangedFor(nameof(IsVisionLlmSelected))]
    public partial int ActiveProviderIndex { get; set; }

    [ObservableProperty]
    public partial int PromptModeIndex { get; set; }

    [ObservableProperty]
    public partial OcrLanguageOption SelectedPreferredLanguage { get; set; }

    [ObservableProperty]
    public partial bool AutoMergeOcrLineBreaks { get; set; } = true;

    [ObservableProperty]
    public partial string VisionBaseUrl { get; set; } = "https://api.openai.com/v1";

    [ObservableProperty]
    public partial string VisionModel { get; set; } = "gpt-4o-mini";

    [ObservableProperty]
    public partial string? SelectedDiscoveredModel { get; set; }

    [ObservableProperty]
    public partial string VisionApiKeyPlaintext { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string VisionPrompt { get; set; } = OcrConfig.DefaultVisionOcrPrompt;

    [ObservableProperty]
    public partial double TimeoutSeconds { get; set; } = 45;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMissingLanguagePacksWarning))]
    public partial bool HasInstalledLanguagePacks { get; set; } = true;

    [ObservableProperty]
    public partial bool IsRefreshingLanguages { get; set; }

    [ObservableProperty]
    public partial bool IsTestingOcr { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFetchModels))]
    public partial bool IsFetchingModels { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFeedback))]
    public partial string? FeedbackMessage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FeedbackSeverity))]
    public partial bool IsFeedbackError { get; set; }

    public bool IsWindowsNativeSelected => ActiveProviderIndex == (int)OcrProviderKind.WindowsNative;

    public bool IsVisionLlmSelected => ActiveProviderIndex == (int)OcrProviderKind.VisionLlm;

    public bool CanFetchModels => !IsFetchingModels;

    public bool HasDiscoveredModels => DiscoveredModels.Count > 0;

    public bool ShowMissingLanguagePacksWarning => !HasInstalledLanguagePacks && !IsRefreshingLanguages;

    public bool HasFeedback => !string.IsNullOrWhiteSpace(FeedbackMessage);

    public InfoBarSeverity FeedbackSeverity => IsFeedbackError ? InfoBarSeverity.Error : InfoBarSeverity.Success;

    public async Task LoadFromAsync(AppSettings settings)
    {
        CancelAutoSaveDebounce();
        _isLoading = true;
        try
        {
            ActiveProviderIndex = (int)settings.Ocr.ActiveProvider;
            PromptModeIndex = (int)settings.Ocr.PromptMode;
            AutoMergeOcrLineBreaks = settings.Ocr.AutoMergeOcrLineBreaks;
            VisionBaseUrl = settings.Ocr.VisionBaseUrl;
            VisionModel = settings.Ocr.VisionModel;
            VisionPrompt = string.IsNullOrWhiteSpace(settings.Ocr.VisionPrompt)
                ? OcrConfig.DefaultVisionOcrPrompt
                : settings.Ocr.VisionPrompt;
            TimeoutSeconds = Math.Clamp(settings.Ocr.TimeoutSeconds, 5, 180);

            VisionApiKeyPlaintext = string.Empty;
            if (!string.IsNullOrWhiteSpace(settings.Ocr.EncryptedVisionApiKey) &&
                _secretProtector.TryUnprotect(settings.Ocr.EncryptedVisionApiKey, out var decrypted))
            {
                VisionApiKeyPlaintext = decrypted;
            }

            ClearDiscoveredModels();
            _isDirty = false;
            FeedbackMessage = null;
            IsFeedbackError = false;
        }
        finally
        {
            _isLoading = false;
        }

        await RefreshWindowsLanguagesAsync(settings.Ocr.PreferredWindowsOcrLanguageTag);
    }

    public async Task FlushIfDirtyAsync()
    {
        CancelAutoSaveDebounce();
        if (!_isDirty || _isLoading)
        {
            return;
        }

        await SaveOcrSettingsCoreAsync(successMessage: null);
    }

    [RelayCommand]
    private Task RefreshLanguagesAsync() =>
        RefreshWindowsLanguagesAsync(_settingsStore.Current.Ocr.PreferredWindowsOcrLanguageTag);

    [RelayCommand]
    private Task SaveOcrSettingsAsync() =>
        SaveOcrSettingsCoreAsync("OCR 配置已安全保存并实时生效。");

    [RelayCommand]
    private async Task ReuseFirstLlmCredentialsAsync()
    {
        var providers = _settingsStore.Current.Providers ?? [];
        var candidate = providers
            .OrderBy(p => p.SortOrder)
            .FirstOrDefault(p => p.Kind == ProviderKind.OpenAiCompatible && !string.IsNullOrWhiteSpace(p.EncryptedApiKey))
            ?? providers
                .OrderBy(p => p.SortOrder)
                .FirstOrDefault(p => p.Kind == ProviderKind.OpenAiCompatible && p.Enabled && !string.IsNullOrWhiteSpace(p.BaseUrl))
            ?? providers
                .OrderBy(p => p.SortOrder)
                .FirstOrDefault(p => p.Kind == ProviderKind.OpenAiCompatible && !string.IsNullOrWhiteSpace(p.BaseUrl));

        if (candidate is null)
        {
            IsFeedbackError = true;
            FeedbackMessage = "未找到已配置的 OpenAI 兼容大模型实例，请先前往「翻译服务」页添加并配置。";
            return;
        }

        var decryptedKey = string.Empty;
        if (!string.IsNullOrWhiteSpace(candidate.EncryptedApiKey) &&
            _secretProtector.TryUnprotect(candidate.EncryptedApiKey, out var plain))
        {
            decryptedKey = plain;
        }

        _isLoading = true;
        try
        {
            VisionBaseUrl = candidate.BaseUrl;
            if (!string.IsNullOrWhiteSpace(candidate.Model))
            {
                VisionModel = candidate.Model;
            }

            VisionApiKeyPlaintext = decryptedKey;
        }
        finally
        {
            _isLoading = false;
        }

        ClearDiscoveredModels();
        await SaveOcrSettingsCoreAsync($"已复用「{candidate.DisplayName}」的接口地址与凭证并保存。");
    }

    [RelayCommand]
    private async Task FetchModelsAsync()
    {
        if (IsFetchingModels)
        {
            return;
        }

        var baseUrl = VisionBaseUrl?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            IsFeedbackError = true;
            FeedbackMessage = "请先填写 Vision API Base URL 后再拉取模型列表。";
            return;
        }

        IsFetchingModels = true;
        IsFeedbackError = false;
        FeedbackMessage = "正在从当前接口拉取可用模型列表...";

        try
        {
            CancelAutoSaveDebounce();
            await SaveOcrSettingsCoreAsync(successMessage: null);

            var models = await _modelDiscoveryService.FetchModelsAsync(baseUrl, VisionApiKeyPlaintext);
            _suppressDiscoveredModelSync = true;
            try
            {
                DiscoveredModels.Clear();
                foreach (var model in models)
                {
                    if (!string.IsNullOrWhiteSpace(model))
                    {
                        DiscoveredModels.Add(model);
                    }
                }

                OnPropertyChanged(nameof(HasDiscoveredModels));

                if (DiscoveredModels.Count == 0)
                {
                    SelectedDiscoveredModel = null;
                    IsFeedbackError = true;
                    FeedbackMessage = "服务端未返回可用模型列表，请手动填写视觉模型名称。";
                    return;
                }

                if (string.IsNullOrWhiteSpace(VisionModel))
                {
                    VisionModel = DiscoveredModels[0];
                }

                SelectedDiscoveredModel = DiscoveredModels.FirstOrDefault(
                    m => string.Equals(m, VisionModel, StringComparison.Ordinal));
            }
            finally
            {
                _suppressDiscoveredModelSync = false;
            }

            IsFeedbackError = false;
            FeedbackMessage = $"成功拉取 {DiscoveredModels.Count} 个模型，可在下拉框中选择支持多模态视觉的模型。";
        }
        catch (Exception ex)
        {
            IsFeedbackError = true;
            FeedbackMessage = $"拉取模型列表失败：{ex.Message}";
        }
        finally
        {
            IsFetchingModels = false;
        }
    }

    [RelayCommand]
    private async Task TestActiveOcrProviderAsync()
    {
        if (IsTestingOcr)
        {
            return;
        }

        // Always persist current OCR edits before running test so a passing check never leaves edits unsaved.
        await SaveOcrSettingsCoreAsync(successMessage: null);

        IsTestingOcr = true;
        IsFeedbackError = false;
        FeedbackMessage = "正在检测当前 OCR 引擎可用性...";

        try
        {
            var config = BuildCurrentConfig();
            var result = await _ocrOrchestrator.TestActiveProviderAsync(config);
            IsFeedbackError = !result.IsSuccess;
            FeedbackMessage = result.IsSuccess
                ? $"OCR 检测通过 ({Math.Round(result.Latency.TotalMilliseconds)} ms)：{result.Message}"
                : $"OCR 检测失败：{result.Message}";
        }
        catch (Exception ex)
        {
            IsFeedbackError = true;
            FeedbackMessage = $"OCR 检测异常：{ex.Message}";
        }
        finally
        {
            IsTestingOcr = false;
        }
    }

    [RelayCommand]
    private void ResetVisionPrompt()
    {
        VisionPrompt = OcrConfig.DefaultVisionOcrPrompt;
    }

    partial void OnActiveProviderIndexChanged(int value)
    {
        if (!_isLoading)
        {
            _ = SaveOcrSettingsAsync();
        }
    }

    partial void OnPromptModeIndexChanged(int value)
    {
        if (!_isLoading)
        {
            _ = SaveOcrSettingsAsync();
        }
    }

    partial void OnAutoMergeOcrLineBreaksChanged(bool value)
    {
        if (!_isLoading)
        {
            _ = SaveOcrSettingsAsync();
        }
    }

    partial void OnSelectedPreferredLanguageChanged(OcrLanguageOption value)
    {
        if (!_isLoading && value is not null)
        {
            _ = SaveOcrSettingsAsync();
        }
    }

    partial void OnSelectedDiscoveredModelChanged(string? value)
    {
        if (_suppressDiscoveredModelSync || _isLoading || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        VisionModel = value;
    }

    partial void OnVisionBaseUrlChanged(string value) => MarkDirtyIfReady();

    partial void OnVisionModelChanged(string value)
    {
        MarkDirtyIfReady();
        if (!_suppressDiscoveredModelSync && DiscoveredModels.Count > 0)
        {
            _suppressDiscoveredModelSync = true;
            try
            {
                SelectedDiscoveredModel = DiscoveredModels.FirstOrDefault(
                    m => string.Equals(m, value, StringComparison.Ordinal));
            }
            finally
            {
                _suppressDiscoveredModelSync = false;
            }
        }
    }

    partial void OnVisionApiKeyPlaintextChanged(string value) => MarkDirtyIfReady();

    partial void OnVisionPromptChanged(string value) => MarkDirtyIfReady();

    partial void OnTimeoutSecondsChanged(double value) => MarkDirtyIfReady();

    private void MarkDirtyIfReady()
    {
        if (!_isLoading)
        {
            _isDirty = true;
            ScheduleAutoSave();
        }
    }

    private void ScheduleAutoSave()
    {
        CancelAutoSaveDebounce();
        var cts = new CancellationTokenSource();
        _autoSaveDebounceCts = cts;
        _ = PersistDebouncedAsync(cts.Token);
    }

    private async Task PersistDebouncedAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(250, ct);
            if (!ct.IsCancellationRequested && _isDirty)
            {
                await SaveOcrSettingsCoreAsync(successMessage: null);
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
            try
            {
                _autoSaveDebounceCts.Cancel();
            }
            catch
            {
                // Ignore cancellation race.
            }

            _autoSaveDebounceCts.Dispose();
            _autoSaveDebounceCts = null;
        }
    }

    private void ClearDiscoveredModels()
    {
        _suppressDiscoveredModelSync = true;
        try
        {
            DiscoveredModels.Clear();
            SelectedDiscoveredModel = null;
            OnPropertyChanged(nameof(HasDiscoveredModels));
        }
        finally
        {
            _suppressDiscoveredModelSync = false;
        }
    }

    private async Task SaveOcrSettingsCoreAsync(string? successMessage)
    {
        CancelAutoSaveDebounce();
        try
        {
            var updated = _settingsStore.Current.Clone();
            updated.Ocr = BuildCurrentConfig();
            await _settingsStore.SaveAsync(updated);
            _isDirty = false;
            if (!string.IsNullOrWhiteSpace(successMessage))
            {
                IsFeedbackError = false;
                FeedbackMessage = successMessage;
            }
        }
        catch (Exception ex)
        {
            IsFeedbackError = true;
            FeedbackMessage = $"保存 OCR 配置失败：{ex.Message}";
        }
    }

    private async Task RefreshWindowsLanguagesAsync(string? preferredTag)
    {
        IsRefreshingLanguages = true;
        try
        {
            var packs = await _ocrOrchestrator.GetInstalledWindowsOcrLanguagesAsync();
            _isLoading = true;
            try
            {
                InstalledLanguagePacks.Clear();
                PreferredLanguageOptions.Clear();
                PreferredLanguageOptions.Add(new OcrLanguageOption(string.Empty, "自动（跟随 Windows 用户语言）"));

                foreach (var pack in packs)
                {
                    InstalledLanguagePacks.Add(pack);
                    PreferredLanguageOptions.Add(new OcrLanguageOption(
                        pack.LanguageTag,
                        $"{pack.DisplayName} ({pack.LanguageTag})"));
                }

                HasInstalledLanguagePacks = InstalledLanguagePacks.Count > 0;
                SelectedPreferredLanguage = PreferredLanguageOptions.FirstOrDefault(
                        x => string.Equals(x.LanguageTag, preferredTag ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                    ?? PreferredLanguageOptions[0];
            }
            finally
            {
                _isLoading = false;
            }
        }
        catch (Exception ex)
        {
            HasInstalledLanguagePacks = false;
            IsFeedbackError = true;
            FeedbackMessage = $"读取 Windows OCR 语言包失败：{ex.Message}";
        }
        finally
        {
            IsRefreshingLanguages = false;
            OnPropertyChanged(nameof(ShowMissingLanguagePacksWarning));
        }
    }

    private OcrConfig BuildCurrentConfig() => new()
    {
        ActiveProvider = ActiveProviderIndex == (int)OcrProviderKind.VisionLlm
            ? OcrProviderKind.VisionLlm
            : OcrProviderKind.WindowsNative,
        PromptMode = PromptModeIndex switch
        {
            (int)OcrPromptMode.MarkdownTable => OcrPromptMode.MarkdownTable,
            (int)OcrPromptMode.LatexFormula => OcrPromptMode.LatexFormula,
            _ => OcrPromptMode.PlainText,
        },
        PreferredWindowsOcrLanguageTag = SelectedPreferredLanguage?.LanguageTag ?? string.Empty,
        AutoMergeOcrLineBreaks = AutoMergeOcrLineBreaks,
        VisionBaseUrl = VisionBaseUrl?.Trim() ?? string.Empty,
        VisionModel = VisionModel?.Trim() ?? string.Empty,
        EncryptedVisionApiKey = string.IsNullOrWhiteSpace(VisionApiKeyPlaintext)
            ? string.Empty
            : _secretProtector.Protect(VisionApiKeyPlaintext.Trim()),
        VisionPrompt = string.IsNullOrWhiteSpace(VisionPrompt)
            ? OcrConfig.DefaultVisionOcrPrompt
            : VisionPrompt,
        TimeoutSeconds = double.IsNaN(TimeoutSeconds) ? 45 : Math.Clamp((int)Math.Round(TimeoutSeconds), 5, 180),
    };
}
