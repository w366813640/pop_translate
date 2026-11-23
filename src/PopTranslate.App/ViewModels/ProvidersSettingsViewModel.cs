using System.Collections.ObjectModel;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;

namespace PopTranslate.App.ViewModels;

public sealed partial class ProviderItemViewModel : ObservableObject
{
    private readonly Action<ProviderItemViewModel> _enabledChangedCallback;
    private bool _suppressEnabledCallback;

    public ProviderItemViewModel(
        ProviderInstanceConfig config,
        string decryptedApiKey,
        Action<ProviderItemViewModel> enabledChangedCallback)
    {
        _enabledChangedCallback = enabledChangedCallback;
        _suppressEnabledCallback = true;

        Id = config.Id;
        Kind = config.Kind;
        DisplayName = config.DisplayName;
        Enabled = config.Enabled;
        IsCollapsedByDefault = config.IsCollapsedByDefault;
        SortOrder = config.SortOrder;
        BaseUrl = config.BaseUrl;
        Model = config.Model;
        ApiKeyPlaintext = decryptedApiKey;
        SystemPrompt = string.IsNullOrWhiteSpace(config.SystemPrompt)
            ? ProviderInstanceConfig.DefaultOpenAiSystemPrompt
            : config.SystemPrompt;
        Temperature = config.Temperature;
        TimeoutSeconds = config.TimeoutSeconds;
        DeepLTierIndex = (int)config.DeepLTier;
        FreeEngineIndex = (int)config.FreeEngine;

        _suppressEnabledCallback = false;
    }

    public string Id { get; }

    public ProviderKind Kind { get; }

    public string KindLabel => Kind switch
    {
        ProviderKind.OpenAiCompatible => "OpenAI 兼容大模型 (流式)",
        ProviderKind.DeepL => "DeepL API",
        _ => "免费传统翻译 (零配置)",
    };

    public string BadgeText => Kind switch
    {
        ProviderKind.OpenAiCompatible => "LLM",
        ProviderKind.DeepL => "DeepL",
        _ => "免费",
    };

    public bool IsOpenAi => Kind == ProviderKind.OpenAiCompatible;

    public bool IsDeepL => Kind == ProviderKind.DeepL;

    public bool IsFreeTraditional => Kind == ProviderKind.FreeTraditional;

    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial bool IsCollapsedByDefault { get; set; }

    [ObservableProperty]
    public partial int SortOrder { get; set; }

    [ObservableProperty]
    public partial string BaseUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Model { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ApiKeyPlaintext { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SystemPrompt { get; set; } = ProviderInstanceConfig.DefaultOpenAiSystemPrompt;

    [ObservableProperty]
    public partial double Temperature { get; set; } = 0.2;

    [ObservableProperty]
    public partial double TimeoutSeconds { get; set; } = 30;

    [ObservableProperty]
    public partial int DeepLTierIndex { get; set; }

    [ObservableProperty]
    public partial int FreeEngineIndex { get; set; }

    partial void OnEnabledChanged(bool value)
    {
        if (!_suppressEnabledCallback)
        {
            _enabledChangedCallback(this);
        }
    }

    public ProviderInstanceConfig ToConfig(ISecretProtector secretProtector, int sortOrder) => new()
    {
        Id = Id,
        DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? KindLabel : DisplayName.Trim(),
        Kind = Kind,
        Enabled = Enabled,
        IsCollapsedByDefault = IsCollapsedByDefault,
        SortOrder = sortOrder,
        BaseUrl = BaseUrl?.Trim() ?? string.Empty,
        Model = Model?.Trim() ?? string.Empty,
        EncryptedApiKey = string.IsNullOrWhiteSpace(ApiKeyPlaintext)
            ? string.Empty
            : secretProtector.Protect(ApiKeyPlaintext.Trim()),
        SystemPrompt = string.IsNullOrWhiteSpace(SystemPrompt)
            ? ProviderInstanceConfig.DefaultOpenAiSystemPrompt
            : SystemPrompt,
        Temperature = double.IsNaN(Temperature) ? 0.2 : Math.Clamp(Temperature, 0.0, 2.0),
        TimeoutSeconds = double.IsNaN(TimeoutSeconds) ? 30 : Math.Clamp((int)Math.Round(TimeoutSeconds), 5, 180),
        DeepLTier = DeepLTierIndex switch
        {
            (int)DeepLEndpointTier.Free => DeepLEndpointTier.Free,
            (int)DeepLEndpointTier.Pro => DeepLEndpointTier.Pro,
            _ => DeepLEndpointTier.Auto,
        },
        FreeEngine = FreeEngineIndex switch
        {
            (int)FreeTranslatorEngine.Google => FreeTranslatorEngine.Google,
            (int)FreeTranslatorEngine.Bing => FreeTranslatorEngine.Bing,
            _ => FreeTranslatorEngine.Auto,
        },
    };
}

public sealed partial class ProvidersSettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _settingsStore;
    private readonly ISecretProtector _secretProtector;
    private readonly ITranslationOrchestrator _translationOrchestrator;
    private bool _isLoading;

    public ProvidersSettingsViewModel(
        ISettingsStore settingsStore,
        ISecretProtector secretProtector,
        ITranslationOrchestrator translationOrchestrator)
    {
        _settingsStore = settingsStore;
        _secretProtector = secretProtector;
        _translationOrchestrator = translationOrchestrator;
    }

    public ObservableCollection<ProviderItemViewModel> Providers { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedProvider))]
    [NotifyPropertyChangedFor(nameof(HasNoSelectedProvider))]
    [NotifyPropertyChangedFor(nameof(CanTestConnection))]
    public partial ProviderItemViewModel? SelectedProvider { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTestConnection))]
    public partial bool IsTestingConnection { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFeedback))]
    public partial string? FeedbackMessage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FeedbackSeverity))]
    public partial bool IsFeedbackError { get; set; }

    public bool HasSelectedProvider => SelectedProvider is not null;

    public bool HasNoSelectedProvider => SelectedProvider is null;

    public bool CanTestConnection => HasSelectedProvider && !IsTestingConnection;

    public bool HasFeedback => !string.IsNullOrWhiteSpace(FeedbackMessage);

    public InfoBarSeverity FeedbackSeverity => IsFeedbackError ? InfoBarSeverity.Error : InfoBarSeverity.Success;

    public void LoadFrom(AppSettings settings)
    {
        _isLoading = true;
        try
        {
            var previousId = SelectedProvider?.Id;
            Providers.Clear();

            foreach (var config in settings.Providers.OrderBy(p => p.SortOrder))
            {
                var plaintextKey = string.Empty;
                if (!string.IsNullOrWhiteSpace(config.EncryptedApiKey) &&
                    _secretProtector.TryUnprotect(config.EncryptedApiKey, out var decrypted))
                {
                    plaintextKey = decrypted;
                }

                Providers.Add(new ProviderItemViewModel(config, plaintextKey, OnItemEnabledToggled));
            }

            SelectedProvider = Providers.FirstOrDefault(p => p.Id == previousId)
                ?? Providers.FirstOrDefault();

            FeedbackMessage = null;
            IsFeedbackError = false;
        }
        finally
        {
            _isLoading = false;
        }
    }

    [RelayCommand]
    private async Task AddOpenAiProviderAsync()
    {
        var count = Providers.Count(p => p.Kind == ProviderKind.OpenAiCompatible) + 1;
        var config = new ProviderInstanceConfig
        {
            Id = $"openai-{Guid.NewGuid():N}",
            DisplayName = count == 1 ? "OpenAI 兼容服务" : $"OpenAI 兼容服务 #{count}",
            Kind = ProviderKind.OpenAiCompatible,
            Enabled = true,
            SortOrder = Providers.Count,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o-mini",
            SystemPrompt = ProviderInstanceConfig.DefaultOpenAiSystemPrompt,
            Temperature = 0.2,
            TimeoutSeconds = 30,
        };

        var vm = new ProviderItemViewModel(config, string.Empty, OnItemEnabledToggled);
        Providers.Add(vm);
        SelectedProvider = vm;
        await SaveAllProvidersAsync("已添加新的 OpenAI 兼容实例，请填写 API Key 后保存。");
    }

    [RelayCommand]
    private async Task AddDeepLProviderAsync()
    {
        var count = Providers.Count(p => p.Kind == ProviderKind.DeepL) + 1;
        var config = new ProviderInstanceConfig
        {
            Id = $"deepl-{Guid.NewGuid():N}",
            DisplayName = count == 1 ? "DeepL" : $"DeepL #{count}",
            Kind = ProviderKind.DeepL,
            Enabled = true,
            SortOrder = Providers.Count,
            DeepLTier = DeepLEndpointTier.Auto,
            TimeoutSeconds = 20,
        };

        var vm = new ProviderItemViewModel(config, string.Empty, OnItemEnabledToggled);
        Providers.Add(vm);
        SelectedProvider = vm;
        await SaveAllProvidersAsync("已添加 DeepL 实例，请填写 API Key 后保存。");
    }

    [RelayCommand]
    private async Task AddFreeTraditionalProviderAsync()
    {
        var count = Providers.Count(p => p.Kind == ProviderKind.FreeTraditional) + 1;
        var config = new ProviderInstanceConfig
        {
            Id = $"free-{Guid.NewGuid():N}",
            DisplayName = count == 1 ? "免费翻译 (Google / Bing)" : $"免费翻译 #{count}",
            Kind = ProviderKind.FreeTraditional,
            Enabled = true,
            SortOrder = Providers.Count,
            FreeEngine = FreeTranslatorEngine.Auto,
            TimeoutSeconds = 15,
        };

        var vm = new ProviderItemViewModel(config, string.Empty, OnItemEnabledToggled);
        Providers.Add(vm);
        SelectedProvider = vm;
        await SaveAllProvidersAsync("已添加零配置免费翻译服务。");
    }

    [RelayCommand]
    private async Task DeleteSelectedProviderAsync()
    {
        if (SelectedProvider is null)
        {
            return;
        }

        var index = Providers.IndexOf(SelectedProvider);
        if (index < 0)
        {
            return;
        }

        var removedName = SelectedProvider.DisplayName;
        Providers.RemoveAt(index);
        SelectedProvider = Providers.Count == 0
            ? null
            : Providers[Math.Min(index, Providers.Count - 1)];

        await SaveAllProvidersAsync($"已删除服务「{removedName}」。");
    }

    [RelayCommand]
    private async Task MoveSelectedUpAsync()
    {
        if (SelectedProvider is null)
        {
            return;
        }

        var index = Providers.IndexOf(SelectedProvider);
        if (index <= 0)
        {
            return;
        }

        var item = SelectedProvider;
        Providers.Move(index, index - 1);
        SelectedProvider = item;
        await SaveAllProvidersAsync("已更新服务卡片排序。");
    }

    [RelayCommand]
    private async Task MoveSelectedDownAsync()
    {
        if (SelectedProvider is null)
        {
            return;
        }

        var index = Providers.IndexOf(SelectedProvider);
        if (index < 0 || index >= Providers.Count - 1)
        {
            return;
        }

        var item = SelectedProvider;
        Providers.Move(index, index + 1);
        SelectedProvider = item;
        await SaveAllProvidersAsync("已更新服务卡片排序。");
    }

    [RelayCommand]
    private async Task SaveSelectedProviderAsync()
    {
        await SaveAllProvidersAsync("服务配置已加密保存并实时生效。");
    }

    [RelayCommand]
    private void ResetSystemPrompt()
    {
        if (SelectedProvider is not null)
        {
            SelectedProvider.SystemPrompt = ProviderInstanceConfig.DefaultOpenAiSystemPrompt;
        }
    }

    [RelayCommand]
    private async Task TestSelectedProviderAsync()
    {
        if (SelectedProvider is null || IsTestingConnection)
        {
            return;
        }

        IsTestingConnection = true;
        IsFeedbackError = false;
        FeedbackMessage = $"正在测试「{SelectedProvider.DisplayName}」连接...";

        try
        {
            var tempConfig = SelectedProvider.ToConfig(_secretProtector, SelectedProvider.SortOrder);
            var result = await _translationOrchestrator.TestProviderAsync(tempConfig);
            IsFeedbackError = !result.IsSuccess;
            FeedbackMessage = result.IsSuccess
                ? $"连接成功 ({Math.Round(result.Latency.TotalMilliseconds)} ms)：{result.Message}"
                : $"连接失败：{result.Message}";
        }
        catch (Exception ex)
        {
            IsFeedbackError = true;
            FeedbackMessage = $"测试异常：{ex.Message}";
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    private void OnItemEnabledToggled(ProviderItemViewModel item)
    {
        if (_isLoading)
        {
            return;
        }

        _ = SaveAllProvidersAsync(item.Enabled
            ? $"已启用「{item.DisplayName}」。"
            : $"已停用「{item.DisplayName}」。");
    }

    private async Task SaveAllProvidersAsync(string successMessage)
    {
        try
        {
            var updated = _settingsStore.Current.Clone();
            updated.Providers = Providers
                .Select((item, index) =>
                {
                    item.SortOrder = index;
                    return item.ToConfig(_secretProtector, index);
                })
                .ToList();

            await _settingsStore.SaveAsync(updated);
            IsFeedbackError = false;
            FeedbackMessage = successMessage;
        }
        catch (Exception ex)
        {
            IsFeedbackError = true;
            FeedbackMessage = $"保存翻译服务失败：{ex.Message}";
        }
    }
}
