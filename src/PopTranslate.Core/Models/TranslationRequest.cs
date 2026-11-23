namespace PopTranslate.Core.Models;

public sealed record TranslationRequest(
    string Text,
    LanguageCode SourceLanguage,
    LanguageCode TargetLanguage,
    bool ApplyTextPreprocessing = true,
    bool IsFromOcr = false,
    IReadOnlyList<string>? ProviderInstanceIds = null);
