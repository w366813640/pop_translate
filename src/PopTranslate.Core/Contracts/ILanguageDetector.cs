using PopTranslate.Core.Models;

namespace PopTranslate.Core.Contracts;

public interface ILanguageDetector
{
    LanguageDetectionResult Detect(string text);
}
