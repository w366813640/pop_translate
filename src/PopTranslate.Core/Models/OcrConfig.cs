namespace PopTranslate.Core.Models;

public enum OcrPromptMode
{
    PlainText = 0,
    MarkdownTable = 1,
    LatexFormula = 2,
}

public sealed class OcrConfig
{
    public const string DefaultVisionOcrPrompt =
        "Extract all visible text from this screenshot accurately. Preserve reading order and line breaks. " +
        "Output ONLY the extracted text without commentary or markdown fences.";

    public OcrProviderKind ActiveProvider { get; set; } = OcrProviderKind.WindowsNative;

    /// <summary>
    /// Optional BCP-47 tag (e.g., "en-US", "zh-Hans-CN"). Empty means Windows user-profile default.
    /// </summary>
    public string PreferredWindowsOcrLanguageTag { get; set; } = string.Empty;

    public bool AutoMergeOcrLineBreaks { get; set; } = true;

    public string VisionBaseUrl { get; set; } = "https://api.openai.com/v1";

    public string VisionModel { get; set; } = "gpt-4o-mini";

    /// <summary>
    /// DPAPI-encrypted API key ciphertext (never plaintext).
    /// </summary>
    public string EncryptedVisionApiKey { get; set; } = string.Empty;

    public string VisionPrompt { get; set; } = DefaultVisionOcrPrompt;

    public OcrPromptMode PromptMode { get; set; } = OcrPromptMode.PlainText;

    public int TimeoutSeconds { get; set; } = 45;

    public OcrConfig Clone() => new()
    {
        ActiveProvider = ActiveProvider,
        PreferredWindowsOcrLanguageTag = PreferredWindowsOcrLanguageTag ?? string.Empty,
        AutoMergeOcrLineBreaks = AutoMergeOcrLineBreaks,
        VisionBaseUrl = VisionBaseUrl ?? string.Empty,
        VisionModel = VisionModel ?? string.Empty,
        EncryptedVisionApiKey = EncryptedVisionApiKey ?? string.Empty,
        VisionPrompt = VisionPrompt ?? DefaultVisionOcrPrompt,
        PromptMode = PromptMode,
        TimeoutSeconds = TimeoutSeconds,
    };
}
