namespace PopTranslate.Core.Models;

public sealed class ProxyConfig
{
    public ProxyMode Mode { get; set; } = ProxyMode.System;

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 7890;

    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// DPAPI-encrypted proxy password ciphertext (never plaintext).
    /// </summary>
    public string EncryptedPassword { get; set; } = string.Empty;

    public bool BypassLocalAddresses { get; set; } = true;

    public ProxyConfig Clone() => new()
    {
        Mode = Mode,
        Host = Host,
        Port = Port,
        Username = Username,
        EncryptedPassword = EncryptedPassword,
        BypassLocalAddresses = BypassLocalAddresses,
    };
}
