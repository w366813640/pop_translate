namespace PopTranslate.Core.Models;

public sealed class ProviderInstanceConfig
{
    public const string DefaultOpenAiSystemPrompt =
        "You are a precise, natural translator. Translate the user's text from {source} into {target}. " +
        "Preserve formatting, line breaks, and code snippets. Output ONLY the translation with no explanations or notes.";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string DisplayName { get; set; } = "Translation Service";

    public ProviderKind Kind { get; set; } = ProviderKind.FreeTraditional;

    public bool Enabled { get; set; } = true;

    public bool IsCollapsedByDefault { get; set; }

    public int SortOrder { get; set; }

    public string BaseUrl { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// DPAPI-encrypted API key ciphertext (never plaintext).
    /// </summary>
    public string EncryptedApiKey { get; set; } = string.Empty;

    public string SystemPrompt { get; set; } = DefaultOpenAiSystemPrompt;

    public double Temperature { get; set; } = 0.2;

    public int TimeoutSeconds { get; set; } = 30;

    public DeepLEndpointTier DeepLTier { get; set; } = DeepLEndpointTier.Auto;

    public FreeTranslatorEngine FreeEngine { get; set; } = FreeTranslatorEngine.Auto;

    public ProviderInstanceConfig Clone() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Kind = Kind,
        Enabled = Enabled,
        IsCollapsedByDefault = IsCollapsedByDefault,
        SortOrder = SortOrder,
        BaseUrl = BaseUrl,
        Model = Model,
        EncryptedApiKey = EncryptedApiKey,
        SystemPrompt = SystemPrompt,
        Temperature = Temperature,
        TimeoutSeconds = TimeoutSeconds,
        DeepLTier = DeepLTier,
        FreeEngine = FreeEngine,
    };
}
