namespace PopTranslate.Core.Models;

public enum SmartInputKind
{
    Prose = 0,
    UrlOrEmail = 1,
    FilePath = 2,
    JsonOrMarkup = 3,
    StackTrace = 4,
    CodeSnippet = 5,
}

public sealed record SmartInputClassification(
    SmartInputKind Kind,
    string ReasonLabel);
