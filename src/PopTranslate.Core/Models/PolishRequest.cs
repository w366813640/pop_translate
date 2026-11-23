namespace PopTranslate.Core.Models;

public sealed record PolishRequest(
    string Text,
    PolishStyle Style = PolishStyle.General,
    string? CustomPrompt = null,
    LanguageCode? PreferredLanguage = null,
    bool ApplyTextPreprocessing = true,
    IReadOnlyList<string>? ProviderInstanceIds = null);
