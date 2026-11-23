namespace PopTranslate.Core.Contracts;

public interface ISecretProtector
{
    string Protect(string plaintext);

    string Unprotect(string protectedText);

    bool TryUnprotect(string protectedText, out string plaintext);
}
