namespace PopTranslate.Core.Contracts;

public interface ITextPreprocessor
{
    string Preprocess(string rawText, bool mergeLineBreaks, bool isFromOcr = false);
}
