namespace PopTranslate.Core.Models;

public sealed record BackTranslationRequest(
    string ProviderInstanceId,
    string TranslatedText,
    LanguageCode TranslatedLanguage,
    LanguageCode BackTargetLanguage);
