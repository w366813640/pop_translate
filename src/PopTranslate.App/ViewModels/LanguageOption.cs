using PopTranslate.Core.Models;

namespace PopTranslate.App.ViewModels;

public sealed record LanguageOption(LanguageCode Code, string DisplayLabel)
{
    public override string ToString() => DisplayLabel;
}
