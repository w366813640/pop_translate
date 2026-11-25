using PopTranslate.Core.Models;

namespace PopTranslate.Core.Contracts;

public interface ISmartInputClassifier
{
    SmartInputClassification Classify(string? text);
}
