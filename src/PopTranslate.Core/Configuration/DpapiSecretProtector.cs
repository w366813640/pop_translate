using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using PopTranslate.Core.Contracts;

namespace PopTranslate.Core.Configuration;

public sealed class DpapiSecretProtector : ISecretProtector
{
    public const string Prefix = "dpapi:";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PopTranslate.V1.Secrets");

    public static bool IsProtected(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    public string Protect(string plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            return string.Empty;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI secret protection is only supported on Windows.");
        }

        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var protectedBytes = ProtectOnWindows(plainBytes);
        return Prefix + Convert.ToBase64String(protectedBytes);
    }

    public string Unprotect(string protectedText)
    {
        if (string.IsNullOrWhiteSpace(protectedText))
        {
            return string.Empty;
        }

        if (!IsProtected(protectedText))
        {
            return protectedText;
        }

        return TryUnprotect(protectedText, out var plaintext) ? plaintext : string.Empty;
    }

    public bool TryUnprotect(string protectedText, out string plaintext)
    {
        if (string.IsNullOrWhiteSpace(protectedText))
        {
            plaintext = string.Empty;
            return true;
        }

        if (!IsProtected(protectedText) || !OperatingSystem.IsWindows())
        {
            plaintext = string.Empty;
            return false;
        }

        try
        {
            var payload = protectedText[Prefix.Length..];
            var protectedBytes = Convert.FromBase64String(payload);
            var plainBytes = UnprotectOnWindows(protectedBytes);
            plaintext = Encoding.UTF8.GetString(plainBytes);
            return true;
        }
        catch (FormatException)
        {
            plaintext = string.Empty;
            return false;
        }
        catch (CryptographicException)
        {
            plaintext = string.Empty;
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectOnWindows(byte[] plainBytes) =>
        ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectOnWindows(byte[] protectedBytes) =>
        ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
}
