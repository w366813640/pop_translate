namespace PopTranslate.Core.Contracts;

public interface ITextWriteBackService
{
    Task<bool> ReplaceTextInWindowAsync(nint targetWindowHandle, string replacementText, CancellationToken cancellationToken = default);
}
