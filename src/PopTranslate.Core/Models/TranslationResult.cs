namespace PopTranslate.Core.Models;

public sealed record TranslationResult(
    string ProviderInstanceId,
    string ProviderDisplayName,
    ProviderKind ProviderKind,
    string TranslatedText,
    LanguageCode? DetectedSourceLanguage,
    LanguageCode TargetLanguage,
    bool IsSuccess,
    string? ErrorMessage,
    TimeSpan Elapsed);

public sealed record TranslationBatchResult(
    string PreprocessedText,
    LanguageCode EffectiveSourceLanguage,
    LanguageCode EffectiveTargetLanguage,
    IReadOnlyList<TranslationResult> Results);

public sealed record ProviderHealthCheckResult(
    bool IsSuccess,
    string Message,
    TimeSpan Latency);
