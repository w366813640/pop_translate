namespace PopTranslate.Core.Models;

public sealed record TranslationChunk(
    string DeltaText,
    bool IsComplete = false,
    LanguageCode? DetectedSourceLanguage = null,
    string? ErrorMessage = null);
