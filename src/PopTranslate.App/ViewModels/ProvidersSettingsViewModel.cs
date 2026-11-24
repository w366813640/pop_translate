using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private readonly IModelDiscoveryService _modelDiscoveryService;
    private bool _isLoading;
    private bool _isDirty;
    private bool _suppressDiscoveredModelSync;

    public ProvidersSettingsViewModel(
        ISettingsStore settingsStore,
        ISecretProtector secretProtector,
        ITranslationOrchestrator translationOrchestrator,
        IModelDiscoveryService modelDiscoveryService)
    {
        _settingsStore = settingsStore;
        _secretProtector = secretProtector;
        _translationOrchestrator = translationOrchestrator;
        _modelDiscoveryService = modelDiscoveryService;
    }

    public ObservableCollection<ProviderItemViewModel> Providers { get; } = [];

    public ObservableCollection<string> DiscoveredModels { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedProvider))]
    [NotifyPropertyChangedFor(nameof(HasNoSelectedProvider))]
    [NotifyPropertyChangedFor(nameof(CanTestConnection))]
    [NotifyPropertyChangedFor(nameof(CanFetchModels))]
    public partial ProviderItemViewModel? SelectedProvider { get; set; }

    [ObservableProperty]
    public partial string? SelectedDiscoveredModel { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTestConnection))]
    public partial bool IsTestingConnection { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFetchModels))]
    public partial bool IsFetchingModels { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFeedback))]
    public partial string? FeedbackMessage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FeedbackSeverity))]
    public partial bool IsFeedbackError { get; set; }

    public bool HasSelectedProvider => SelectedProvider is not null;

    public bool HasNoSelectedProvider => SelectedProvider is null;

    public bool CanTestConnection => HasSelectedProvider && !IsTestingConnection;

    public bool CanFetchModels => SelectedProvider is { IsOpenAi: true } && !IsFetchingModels;

    public bool HasDiscoveredModels => DiscoveredModels.Count > 0;

    public bool HasFeedback => !string.IsNullOrWhiteSpace(FeedbackMessage);

    public InfoBarSeverity FeedbackSeverity => IsFeedbackError ? InfoBarSeverity.Error : InfoBarSeverity.Success;

    public void LoadFrom(AppSettings settings)
    {
        _isLoading = true;
        try
        {
            var previousId = SelectedProvider?.Id;
            DetachAllProviderListeners();
            Providers.Clear();
            ClearDiscoveredModels();

            foreach (var config in settings.Providers.OrderBy(p => p.SortOrder))
            {
                var plaintextKey = string.Empty;
                if (!string.IsNullOrWhiteSpace(config.EncryptedApiKey) &&
                    _secretProtector.TryUnprotect(config.EncryptedApiKey, out var decrypted))
                {
                    plaintextKey = decrypted;
                }

                var item = new ProviderItemViewModel(config, plaintextKey, OnItemEnabledToggled);
                item.PropertyChanged += OnProviderItemPropertyChanged;
                Providers.Add(item);
            }

            SelectedProvider = Providers.FirstOrDefault(p => p.Id == previousId)
                ?? Providers.FirstOrDefault();

            _isDirty = false;
            FeedbackMessage = null;
            IsFeedbackError = false;
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

        await SaveAllProvidersCoreAsync(successMessage: null);
    }

    partial void OnSelectedProviderChanged(ProviderItemViewModel? value)
    {
        ClearDiscoveredModels();
    }

    partial void OnSelectedDiscoveredModelChanged(string? value)
    {
        if (_suppressDiscoveredModelSync || _isLoading || string.IsNullOrWhiteSpace(value) || SelectedProvider is null)
        {
            return;
        }

        SelectedProvider.Model = value;
    }

    [RelayCommand]
    private Task AddOpenAiProviderAsync() => AddLlmPresetAsync("custom");

    [RelayCommand]
    private async Task AddLlmPresetAsync(string? presetKey)
    {
        var (baseName, baseUrl, model, keylessHint) = (presetKey?.Trim().ToLowerInvariant()) switch
        {
            "openai" => ("OpenAI", "https://api.openai.com/v1", "gpt-4o-mini", false),
            "deepseek" => ("DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat", false),
            "gemini" => ("Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-2.5-flash", false),
            "siliconflow" => ("SiliconFlow (硅基流动)", "https://api.siliconflow.cn/v1", "Qwen/Qwen2.5-7B-Instruct", false),
            "ollama" => ("Ollama (本地免密钥)", "http://localhost:11434/v1", "qwen2.5:7b", true),
            "openrouter" => ("OpenRouter", "https://openrouter.ai/api/v1", "openai/gpt-4o-mini", false),
            "dashscope" => ("通义千问 (DashScope)", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus", false),
            "zhipu" => ("智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash", false),
            _ => ("OpenAI 兼容服务", "https://api.openai.com/v1", "gpt-4o-mini", false),
        };

        var samePresetCount = Providers.Count(p =>
            p.Kind == ProviderKind.OpenAiCompatible &&
            p.DisplayName.StartsWith(baseName, StringComparison.OrdinalIgnoreCase)) + 1;

        var displayName = samePresetCount == 1 ? baseName : $"{baseName} #{samePresetCount}";
        var config = new ProviderInstanceConfig
        {
            Id = $"openai-{Guid.NewGuid():N}",
            DisplayName = displayName,
            Kind = ProviderKind.OpenAiCompatible,
            Enabled = true,
            SortOrder = Providers.Count,
            BaseUrl = baseUrl,
            Model = model,
            SystemPrompt = ProviderInstanceConfig.DefaultOpenAiSystemPrompt,
            Temperature = 0.2,
            TimeoutSeconds = 30,
        };

        var vm = new ProviderItemViewModel(config, string.Empty, OnItemEnabledToggled);
        vm.PropertyChanged += OnProviderItemPropertyChanged;
        Providers.Add(vm);
        SelectedProvider = vm;

        var message = keylessHint
            ? $"已添加「{displayName}」预设实例（本地免密钥），可直接拉取模型或测试连接。"
            : $"已添加「{displayName}」预设实例，请填写 API Key 后测试或使用。";
        await SaveAllProvidersAsync(message);
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
        vm.PropertyChanged += OnProviderItemPropertyChanged;
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
        vm.PropertyChanged += OnProviderItemPropertyChanged;
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

        var removed = SelectedProvider;
        removed.PropertyChanged -= OnProviderItemPropertyChanged;
        var removedName = removed.DisplayName;
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
    private async Task FetchModelsAsync()
    {
        if (SelectedProvider is not { IsOpenAi: true } provider || IsFetchingModels)
        {
            return;
        }

        var baseUrl = provider.BaseUrl?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            IsFeedbackError = true;
            FeedbackMessage = "请先填写 API Base URL 后再拉取模型列表。";
            return;
        }

        IsFetchingModels = true;
        IsFeedbackError = false;
        FeedbackMessage = $"正在从「{provider.DisplayName}」拉取可用模型...";

        try
        {
            var models = await _modelDiscoveryService.FetchModelsAsync(baseUrl, provider.ApiKeyPlaintext);

            // Guard against user switching provider while request was in flight.
            if (!ReferenceEquals(SelectedProvider, provider))
            {
                return;
            }

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
                    FeedbackMessage = "服务端未返回可用模型列表，请手动填写模型名称。";
                    return;
                }

                if (string.IsNullOrWhiteSpace(provider.Model))
                {
                    provider.Model = DiscoveredModels[0];
                }

                SelectedDiscoveredModel = DiscoveredModels.FirstOrDefault(
                    m => string.Equals(m, provider.Model, StringComparison.Ordinal));
            }
            finally
            {
                _suppressDiscoveredModelSync = false;
            }

            IsFeedbackError = false;
            FeedbackMessage = $"成功拉取 {DiscoveredModels.Count} 个可用模型，可在下拉框中快速选择。";
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
    private async Task TestSelectedProviderAsync()
    {
        if (SelectedProvider is null || IsTestingConnection)
        {
            return;
        }

        var wasDirty = _isDirty;
        if (wasDirty)
        {
            await SaveAllProvidersCoreAsync(successMessage: null);
        }

        IsTestingConnection = true;
        IsFeedbackError = false;
        FeedbackMessage = $"正在测试「{SelectedProvider.DisplayName}」连接...";

        try
        {
            var tempConfig = SelectedProvider.ToConfig(_secretProtector, SelectedProvider.SortOrder);
            var result = await _translationOrchestrator.TestProviderAsync(tempConfig);
            IsFeedbackError = !result.IsSuccess;
            if (result.IsSuccess)
            {
                var savedNote = wasDirty ? "（配置已自动保存）" : string.Empty;
                FeedbackMessage = $"连接成功{savedNote} ({Math.Round(result.Latency.TotalMilliseconds)} ms)：{result.Message}";
            }
            else
            {
                FeedbackMessage = $"连接失败：{result.Message}";
            }
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

    private void OnProviderItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        if (e.PropertyName is nameof(ProviderItemViewModel.SortOrder) or nameof(ProviderItemViewModel.Enabled))
        {
            return;
        }

        _isDirty = true;

        if (e.PropertyName == nameof(ProviderItemViewModel.Model) &&
            !_suppressDiscoveredModelSync &&
            sender is ProviderItemViewModel item &&
            ReferenceEquals(item, SelectedProvider) &&
            DiscoveredModels.Count > 0)
        {
            _suppressDiscoveredModelSync = true;
            try
            {
                SelectedDiscoveredModel = DiscoveredModels.FirstOrDefault(
                    m => string.Equals(m, item.Model, StringComparison.Ordinal));
            }
            finally
            {
                _suppressDiscoveredModelSync = false;
            }
        }
    }

    private void DetachAllProviderListeners()
    {
        foreach (var item in Providers)
        {
            item.PropertyChanged -= OnProviderItemPropertyChanged;
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

    private Task SaveAllProvidersAsync(string successMessage) =>
        SaveAllProvidersCoreAsync(successMessage);

    private async Task SaveAllProvidersCoreAsync(string? successMessage)
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
            FeedbackMessage = $"保存翻译服务失败：{ex.Message}";
        }
    }
}
