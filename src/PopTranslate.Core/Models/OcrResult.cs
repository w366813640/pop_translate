namespace PopTranslate.Core.Models;

public sealed record OcrResult(
    bool IsSuccess,
    string Text,
    LanguageCode? DetectedLanguage,
    OcrProviderKind ProviderKind,
    string? ErrorMessage,
    TimeSpan Elapsed);

public sealed record OcrLanguagePackInfo(
    string LanguageTag,
    string DisplayName,
    LanguageCode MappedLanguage);
