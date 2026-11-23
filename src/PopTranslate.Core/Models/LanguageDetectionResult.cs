namespace PopTranslate.Core.Models;

public sealed record LanguageDetectionResult(
    LanguageCode Language,
    double Confidence,
    bool IsReliable);

public sealed record LanguageRoutingDecision(
    LanguageCode DetectedSourceLanguage,
    LanguageCode TargetLanguage,
    bool UsedManualOverride);
