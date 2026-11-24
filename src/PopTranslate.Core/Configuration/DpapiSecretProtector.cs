using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using PopTranslate.Core.Contracts;

namespace PopTranslate.Core.Configuration;

public sealed class DpapiSecretProtector : ISecretProtector
{
    public const string Prefix = "dpapi:";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PopTranslate.V1.Secrets");

    private int _decryptionFailureCount;

    public bool HadDecryptionFailure => Volatile.Read(ref _decryptionFailureCount) > 0;

    public int DecryptionFailureCount => Volatile.Read(ref _decryptionFailureCount);

    public static bool IsProtected(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    public bool CanDecryptProtectedValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !IsProtected(value))
        {
            return true;
        }

        return TryUnprotect(value, out _);
    }

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
        try
        {
            var protectedBytes = ProtectOnWindows(plainBytes);
            return Prefix + Convert.ToBase64String(protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }
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

        byte[]? plainBytes = null;
        try
        {
            var payload = protectedText.AsSpan(Prefix.Length).Trim();
            var protectedBytes = Convert.FromBase64String(payload.ToString());
            plainBytes = UnprotectOnWindows(protectedBytes);
            plaintext = Encoding.UTF8.GetString(plainBytes);
            return true;
        }
        catch (FormatException)
        {
            Interlocked.Increment(ref _decryptionFailureCount);
            plaintext = string.Empty;
            return false;
        }
        catch (CryptographicException)
        {
            Interlocked.Increment(ref _decryptionFailureCount);
            plaintext = string.Empty;
            return false;
        }
        catch (ArgumentException)
        {
            Interlocked.Increment(ref _decryptionFailureCount);
            plaintext = string.Empty;
            return false;
        }
        finally
        {
            if (plainBytes is not null)
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectOnWindows(byte[] plainBytes) =>
        ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectOnWindows(byte[] protectedBytes) =>
        ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
}
