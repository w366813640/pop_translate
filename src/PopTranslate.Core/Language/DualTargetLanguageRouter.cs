using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;

namespace PopTranslate.Core.Language;

public sealed class DualTargetLanguageRouter : ILanguageRouter
{
    private readonly ILanguageDetector _languageDetector;

    public DualTargetLanguageRouter(ILanguageDetector languageDetector)
    {
        _languageDetector = languageDetector ?? throw new ArgumentNullException(nameof(languageDetector));
    }

    public LanguageRoutingDecision Resolve(
        string text,
        LanguageCode requestedSource,
        LanguageCode requestedTarget,
        LanguageCode primaryTarget,
        LanguageCode secondaryTarget)
    {
        var effectivePrimary = primaryTarget.IsAuto ? LanguageCode.ChineseSimplified : primaryTarget;
        var effectiveSecondary = secondaryTarget.IsAuto ? LanguageCode.English : secondaryTarget;

        var detectedSource = requestedSource.IsAuto
            ? _languageDetector.Detect(text ?? string.Empty).Language
            : requestedSource;

        if (!requestedTarget.IsAuto)
        {
            return new LanguageRoutingDecision(
                DetectedSourceLanguage: detectedSource,
                TargetLanguage: requestedTarget,
                UsedManualOverride: true);
        }

        var sourceMatchesPrimary =
            detectedSource == effectivePrimary ||
            (detectedSource.IsChinese && effectivePrimary.IsChinese && !effectiveSecondary.IsChinese);

        var resolvedTarget = sourceMatchesPrimary ? effectiveSecondary : effectivePrimary;

        return new LanguageRoutingDecision(
            DetectedSourceLanguage: detectedSource,
            TargetLanguage: resolvedTarget,
            UsedManualOverride: false);
    }
}
