using PopTranslate.Core.Models;

namespace PopTranslate.Core.Contracts;

public interface ILanguageRouter
{
    LanguageRoutingDecision Resolve(
        string text,
        LanguageCode requestedSource,
        LanguageCode requestedTarget,
        LanguageCode primaryTarget,
        LanguageCode secondaryTarget);
}
