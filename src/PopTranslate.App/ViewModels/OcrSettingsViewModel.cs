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
    private bool _isLoading;

    public OcrSettingsViewModel(
        ISettingsStore settingsStore,
        ISecretProtector secretProtector,
        IOcrOrchestrator ocrOrchestrator)
    {
        _settingsStore = settingsStore;
        _secretProtector = secretProtector;
        _ocrOrchestrator = ocrOrchestrator;

        PreferredLanguageOptions.Add(new OcrLanguageOption(string.Empty, "自动（跟随 Windows 用户语言）"));
        SelectedPreferredLanguage = PreferredLanguageOptions[0];
    }

    public ObservableCollection<OcrLanguagePackInfo> InstalledLanguagePacks { get; } = [];

    public ObservableCollection<OcrLanguageOption> PreferredLanguageOptions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWindowsNativeSelected))]
    [NotifyPropertyChangedFor(nameof(IsVisionLlmSelected))]
    public partial int ActiveProviderIndex { get; set; }

    [ObservableProperty]
    public partial OcrLanguageOption SelectedPreferredLanguage { get; set; }

    [ObservableProperty]
    public partial bool AutoMergeOcrLineBreaks { get; set; } = true;

    [ObservableProperty]
    public partial string VisionBaseUrl { get; set; } = "https://api.openai.com/v1";

    [ObservableProperty]
    public partial string VisionModel { get; set; } = "gpt-4o-mini";

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
    [NotifyPropertyChangedFor(nameof(HasFeedback))]
    public partial string? FeedbackMessage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FeedbackSeverity))]
    public partial bool IsFeedbackError { get; set; }

    public bool IsWindowsNativeSelected => ActiveProviderIndex == (int)OcrProviderKind.WindowsNative;

    public bool IsVisionLlmSelected => ActiveProviderIndex == (int)OcrProviderKind.VisionLlm;

    public bool ShowMissingLanguagePacksWarning => !HasInstalledLanguagePacks && !IsRefreshingLanguages;

    public bool HasFeedback => !string.IsNullOrWhiteSpace(FeedbackMessage);

    public InfoBarSeverity FeedbackSeverity => IsFeedbackError ? InfoBarSeverity.Error : InfoBarSeverity.Success;

    public async Task LoadFromAsync(AppSettings settings)
    {
        _isLoading = true;
        try
        {
            ActiveProviderIndex = (int)settings.Ocr.ActiveProvider;
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

            FeedbackMessage = null;
            IsFeedbackError = false;
        }
        finally
        {
            _isLoading = false;
        }

        await RefreshWindowsLanguagesAsync(settings.Ocr.PreferredWindowsOcrLanguageTag);
    }

    [RelayCommand]
    private Task RefreshLanguagesAsync() =>
        RefreshWindowsLanguagesAsync(_settingsStore.Current.Ocr.PreferredWindowsOcrLanguageTag);

    [RelayCommand]
    private async Task SaveOcrSettingsAsync()
    {
        try
        {
            var updated = _settingsStore.Current.Clone();
            updated.Ocr = BuildCurrentConfig();
            await _settingsStore.SaveAsync(updated);
            IsFeedbackError = false;
            FeedbackMessage = "OCR 配置已安全保存并实时生效。";
        }
        catch (Exception ex)
        {
            IsFeedbackError = true;
            FeedbackMessage = $"保存 OCR 配置失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task TestActiveOcrProviderAsync()
    {
        if (IsTestingOcr)
        {
            return;
        }

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
