using PopTranslate.Core.Models;

namespace PopTranslate.Core.Contracts;

public interface ITextDiffService
{
    IReadOnlyList<DiffSegment> ComputeDiff(string originalText, string revisedText);
}
