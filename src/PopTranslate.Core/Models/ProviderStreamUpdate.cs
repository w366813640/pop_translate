namespace PopTranslate.Core.Models;

public enum ProviderStreamState
{
    Pending = 0,
    Streaming = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4,
}

public sealed record ProviderStreamUpdate(
    string ProviderInstanceId,
    string ProviderDisplayName,
    ProviderKind ProviderKind,
    ProviderStreamState State,
    string DeltaText,
    string AccumulatedText,
    LanguageCode? DetectedSourceLanguage,
    LanguageCode TargetLanguage,
    string? ErrorMessage,
    TimeSpan Elapsed,
    bool IsReasoning = false);
