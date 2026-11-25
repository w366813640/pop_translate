namespace PopTranslate.Core.Models;

public sealed class GlossaryEntry
{
    public string SourceTerm { get; set; } = string.Empty;

    public string TargetTerm { get; set; } = string.Empty;

    public GlossaryEntry Clone() => new()
    {
        SourceTerm = SourceTerm ?? string.Empty,
        TargetTerm = TargetTerm ?? string.Empty,
    };
}

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public LanguageCode PrimaryTargetLanguage { get; set; } = LanguageCode.ChineseSimplified;

    public LanguageCode SecondaryTargetLanguage { get; set; } = LanguageCode.English;

    public bool MergePdfLineBreaks { get; set; } = true;

    public bool EnableSingleWordDictionary { get; set; } = true;

    public bool EnableCodeIdentifierSplit { get; set; } = true;

    public bool EnableLongTextGuard { get; set; } = true;

    public int LongTextGuardThreshold { get; set; } = 1500;

    public bool EnableSmartInputGuard { get; set; } = true;

    public bool AutoBackTranslate { get; set; }

    public bool LaunchAtStartup { get; set; }

    public bool UseElevatedStartupTask { get; set; }

    public HotkeyConfig Hotkeys { get; set; } = new();

    public SelectionConfig Selection { get; set; } = new();

    public OcrConfig Ocr { get; set; } = new();

    public ProxyConfig Proxy { get; set; } = new();

    public WindowBehaviorConfig Window { get; set; } = new();

    public PolishConfig Polish { get; set; } = new();

    public List<ProviderInstanceConfig> Providers { get; set; } = [];

    public List<GlossaryEntry> Glossary { get; set; } = [];

    public static AppSettings CreateDefault() => new()
    {
        SchemaVersion = CurrentSchemaVersion,
        PrimaryTargetLanguage = LanguageCode.ChineseSimplified,
        SecondaryTargetLanguage = LanguageCode.English,
        MergePdfLineBreaks = true,
        EnableSingleWordDictionary = true,
        EnableCodeIdentifierSplit = true,
        EnableLongTextGuard = true,
        LongTextGuardThreshold = 1500,
        EnableSmartInputGuard = true,
        AutoBackTranslate = false,
        LaunchAtStartup = false,
        UseElevatedStartupTask = false,
        Hotkeys = new HotkeyConfig(),
        Selection = new SelectionConfig(),
        Ocr = new OcrConfig(),
        Proxy = new ProxyConfig(),
        Window = new WindowBehaviorConfig(),
        Polish = new PolishConfig(),
        Glossary = [],
        Providers =
        [
            new ProviderInstanceConfig
            {
                Id = "free-traditional-default",
                DisplayName = "Free Translate (Google / Bing)",
                Kind = ProviderKind.FreeTraditional,
                Enabled = true,
                SortOrder = 0,
                FreeEngine = FreeTranslatorEngine.Auto,
            },
            new ProviderInstanceConfig
            {
                Id = "openai-compatible-default",
                DisplayName = "OpenAI Compatible LLM",
                Kind = ProviderKind.OpenAiCompatible,
                Enabled = false,
                SortOrder = 1,
                BaseUrl = "https://api.openai.com/v1",
                Model = "gpt-4o-mini",
                SystemPrompt = ProviderInstanceConfig.DefaultOpenAiSystemPrompt,
                Temperature = 0.2,
                TimeoutSeconds = 30,
            },
            new ProviderInstanceConfig
            {
                Id = "deepl-default",
                DisplayName = "DeepL",
                Kind = ProviderKind.DeepL,
                Enabled = false,
                SortOrder = 2,
                DeepLTier = DeepLEndpointTier.Auto,
                TimeoutSeconds = 20,
            },
        ],
    };

    public AppSettings Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        PrimaryTargetLanguage = PrimaryTargetLanguage,
        SecondaryTargetLanguage = SecondaryTargetLanguage,
        MergePdfLineBreaks = MergePdfLineBreaks,
        EnableSingleWordDictionary = EnableSingleWordDictionary,
        EnableCodeIdentifierSplit = EnableCodeIdentifierSplit,
        EnableLongTextGuard = EnableLongTextGuard,
        LongTextGuardThreshold = LongTextGuardThreshold,
        EnableSmartInputGuard = EnableSmartInputGuard,
        AutoBackTranslate = AutoBackTranslate,
        LaunchAtStartup = LaunchAtStartup,
        UseElevatedStartupTask = UseElevatedStartupTask,
        Hotkeys = (Hotkeys ?? new HotkeyConfig()).Clone(),
        Selection = (Selection ?? new SelectionConfig()).Clone(),
        Ocr = (Ocr ?? new OcrConfig()).Clone(),
        Proxy = (Proxy ?? new ProxyConfig()).Clone(),
        Window = (Window ?? new WindowBehaviorConfig()).Clone(),
        Polish = (Polish ?? new PolishConfig()).Clone(),
        Providers = (Providers ?? []).Where(p => p is not null).Select(p => p.Clone()).ToList(),
        Glossary = (Glossary ?? []).Where(g => g is not null).Select(g => g.Clone()).ToList(),
    };
}
