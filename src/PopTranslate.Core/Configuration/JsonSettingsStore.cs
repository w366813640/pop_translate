using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;

namespace PopTranslate.Core.Configuration;

public sealed class JsonSettingsStore : ISettingsStore
{
    private const int MoveMaxAttempts = 3;
    private static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(25);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ISecretProtector _secretProtector;
    private readonly string? _legacyFallbackFilePath;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private AppSettings _current;

    [ActivatorUtilitiesConstructor]
    public JsonSettingsStore(ISecretProtector secretProtector)
        : this(secretProtector, customSettingsFilePath: null, legacyFallbackFilePath: null)
    {
    }

    public JsonSettingsStore(
        ISecretProtector secretProtector,
        string? customSettingsFilePath,
        string? legacyFallbackFilePath = null)
    {
        _secretProtector = secretProtector ?? throw new ArgumentNullException(nameof(secretProtector));
        if (string.IsNullOrWhiteSpace(customSettingsFilePath))
        {
            SettingsFilePath = GetDefaultSettingsPath();
            _legacyFallbackFilePath = IsPortableMode()
                ? null
                : (string.IsNullOrWhiteSpace(legacyFallbackFilePath)
                    ? GetLegacyLocalSettingsPath()
                    : Path.GetFullPath(legacyFallbackFilePath));
        }
        else
        {
            SettingsFilePath = Path.GetFullPath(customSettingsFilePath);
            _legacyFallbackFilePath = string.IsNullOrWhiteSpace(legacyFallbackFilePath)
                ? null
                : Path.GetFullPath(legacyFallbackFilePath);
        }

        _current = AppSettings.CreateDefault();
    }

    public JsonSettingsStore(string? customSettingsFilePath = null)
        : this(new DpapiSecretProtector(), customSettingsFilePath, legacyFallbackFilePath: null)
    {
    }

    public string SettingsFilePath { get; }

    public AppSettings Current => Volatile.Read(ref _current);

    public event EventHandler<AppSettings>? SettingsChanged;

    public static string? ResolvePortableRootDirectory(string? baseDirectory = null)
    {
        var rawBase = string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;
        var trimmed = Path.GetFullPath(rawBase).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (File.Exists(Path.Combine(trimmed, "portable")))
        {
            return trimmed;
        }

        var folderName = Path.GetFileName(trimmed);
        if (string.Equals(folderName, "runtime", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(folderName, "bin", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(trimmed);
            if (parent is not null && File.Exists(Path.Combine(parent.FullName, "portable")))
            {
                return parent.FullName;
            }
        }

        return null;
    }

    public static bool IsPortableMode(string? baseDirectory = null) =>
        ResolvePortableRootDirectory(baseDirectory) is not null;

    public static string GetDefaultSettingsPath(string? baseDirectory = null) =>
        ResolvePortableRootDirectory(baseDirectory) is { } portableRoot
            ? Path.Combine(portableRoot, "data", "settings.json")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PopTranslate",
                "settings.json");

    public static string GetLegacyLocalSettingsPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PopTranslate",
            "settings.json");

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                TryMigrateLegacySettingsFile();
            }

            if (!File.Exists(SettingsFilePath))
            {
                var defaults = AppSettings.CreateDefault();
                Volatile.Write(ref _current, defaults);
                return defaults.Clone();
            }

            try
            {
                var loaded = await ReadAndDeserializeAsync(SettingsFilePath, cancellationToken).ConfigureAwait(false);
                if (loaded is null)
                {
                    return await RecoverFromBackupOrRestoreDefaultsAsync(cancellationToken).ConfigureAwait(false);
                }

                SanitizeStructure(loaded);
                SanitizeAndProtectSecrets(loaded);
                Volatile.Write(ref _current, loaded);
                return loaded.Clone();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (
                ex is JsonException
                    or NotSupportedException
                    or InvalidOperationException
                    or NullReferenceException
                    or ArgumentException
                    or FormatException)
            {
                return await RecoverFromBackupOrRestoreDefaultsAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        SanitizeStructure(settings);
        var snapshot = settings.Clone();
        SanitizeStructure(snapshot);

        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SanitizeAndProtectSecrets(snapshot, Volatile.Read(ref _current));

            var directory = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var serializedBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, SerializerOptions);

            byte[]? existingBytes = null;
            if (File.Exists(SettingsFilePath))
            {
                try
                {
                    existingBytes = await File.ReadAllBytesAsync(SettingsFilePath, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    existingBytes = null;
                }
            }

            // Skip redundant disk rewrite & avoid clobbering a distinct .bak snapshot when content is identical.
            if (existingBytes is not null && existingBytes.AsSpan().SequenceEqual(serializedBytes))
            {
                Volatile.Write(ref _current, snapshot);
            }
            else
            {
                var tempPath = $"{SettingsFilePath}.{Guid.NewGuid():N}.tmp";
                try
                {
                    await using (var stream = new FileStream(
                        tempPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 4096,
                        useAsync: true))
                    {
                        await stream.WriteAsync(serializedBytes, cancellationToken).ConfigureAwait(false);
                        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        stream.Flush(flushToDisk: true);
                    }

                    TryWriteBackupSnapshot(existingBytes);
                    await MoveWithRetryAsync(tempPath, SettingsFilePath, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    TryDeleteFile(tempPath);
                    throw;
                }

                Volatile.Write(ref _current, snapshot);
            }
        }
        finally
        {
            _ioLock.Release();
        }

        SettingsChanged?.Invoke(this, snapshot.Clone());
    }

    private static async Task<AppSettings?> ReadAndDeserializeAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4096,
            useAsync: true);

        return await JsonSerializer.DeserializeAsync<AppSettings>(
            stream,
            SerializerOptions,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AppSettings> RecoverFromBackupOrRestoreDefaultsAsync(CancellationToken cancellationToken)
    {
        TryBackupCorruptFile();

        var backupPath = SettingsFilePath + ".bak";
        if (File.Exists(backupPath))
        {
            try
            {
                var recovered = await ReadAndDeserializeAsync(backupPath, cancellationToken).ConfigureAwait(false);
                if (recovered is not null)
                {
                    SanitizeStructure(recovered);
                    SanitizeAndProtectSecrets(recovered);
                    try
                    {
                        File.Copy(backupPath, SettingsFilePath, overwrite: true);
                    }
                    catch
                    {
                        // Best-effort primary file restoration.
                    }

                    Volatile.Write(ref _current, recovered);
                    return recovered.Clone();
                }
            }
            catch
            {
                // Backup also unreadable; fall through to clean defaults.
            }
        }

        var defaults = AppSettings.CreateDefault();
        Volatile.Write(ref _current, defaults);
        return defaults.Clone();
    }

    private void TryMigrateLegacySettingsFile()
    {
        if (string.IsNullOrWhiteSpace(_legacyFallbackFilePath) ||
            string.Equals(_legacyFallbackFilePath, SettingsFilePath, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(_legacyFallbackFilePath))
        {
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.Copy(_legacyFallbackFilePath, SettingsFilePath, overwrite: false);
        }
        catch
        {
            // Best-effort one-time migration from legacy LocalAppData path.
        }
    }

    private void TryWriteBackupSnapshot(byte[]? existingBytes)
    {
        try
        {
            if (existingBytes is { Length: > 16 })
            {
                var backupPath = SettingsFilePath + ".bak";
                if (File.Exists(backupPath))
                {
                    try
                    {
                        var existingBackupBytes = File.ReadAllBytes(backupPath);
                        if (existingBackupBytes.AsSpan().SequenceEqual(existingBytes))
                        {
                            return;
                        }
                    }
                    catch
                    {
                        // Ignore unreadable prior backup and overwrite with known-good bytes.
                    }
                }

                File.WriteAllBytes(backupPath, existingBytes);
            }
            else if (File.Exists(SettingsFilePath) && new FileInfo(SettingsFilePath).Length > 16)
            {
                File.Copy(SettingsFilePath, SettingsFilePath + ".bak", overwrite: true);
            }
        }
        catch
        {
            // Best-effort rolling backup only.
        }
    }

    private static async Task MoveWithRetryAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(sourcePath, destinationPath, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < MoveMaxAttempts)
            {
                await Task.Delay(MoveRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void SanitizeStructure(AppSettings settings)
    {
        settings.Hotkeys ??= new HotkeyConfig();
        settings.Hotkeys.SelectionTranslate ??= HotkeyConfig.DefaultSelectionTranslate;
        settings.Hotkeys.ScreenshotTranslate ??= HotkeyConfig.DefaultScreenshotTranslate;
        settings.Hotkeys.SelectionPolish ??= HotkeyConfig.DefaultSelectionPolish;
        settings.Hotkeys.SilentOcr ??= HotkeyConfig.DefaultSilentOcr;
        settings.Hotkeys.ReplaceTranslate ??= HotkeyConfig.DefaultReplaceTranslate;

        settings.Selection ??= new SelectionConfig();
        settings.Selection.ProcessBlacklist ??= [.. SelectionConfig.DefaultProcessBlacklist];
        settings.Selection.ProcessBlacklist.RemoveAll(static item => string.IsNullOrWhiteSpace(item));

        settings.Ocr ??= new OcrConfig();
        settings.Ocr.PreferredWindowsOcrLanguageTag ??= string.Empty;
        settings.Ocr.VisionBaseUrl ??= "https://api.openai.com/v1";
        settings.Ocr.VisionModel ??= "gpt-4o-mini";
        settings.Ocr.VisionPrompt ??= OcrConfig.DefaultVisionOcrPrompt;

        settings.Proxy ??= new ProxyConfig();
        settings.Proxy.Host ??= "127.0.0.1";
        settings.Proxy.Username ??= string.Empty;

        settings.Window ??= new WindowBehaviorConfig();
        settings.Polish ??= new PolishConfig();
        settings.Polish.CustomPrompt ??= PolishConfig.DefaultCustomPrompt;

        if (settings.Providers is null)
        {
            settings.Providers = AppSettings.CreateDefault().Providers;
        }
        else
        {
            settings.Providers.RemoveAll(static provider => provider is null);
            foreach (var provider in settings.Providers)
            {
                provider.Id = string.IsNullOrWhiteSpace(provider.Id) ? Guid.NewGuid().ToString("N") : provider.Id;
                provider.DisplayName ??= "Translation Service";
                provider.BaseUrl ??= string.Empty;
                provider.Model ??= string.Empty;
                provider.SystemPrompt ??= ProviderInstanceConfig.DefaultOpenAiSystemPrompt;
            }
        }

        if (settings.Glossary is null)
        {
            settings.Glossary = [];
        }
        else
        {
            settings.Glossary.RemoveAll(static entry => entry is null || string.IsNullOrWhiteSpace(entry.SourceTerm));
            foreach (var entry in settings.Glossary)
            {
                entry.SourceTerm = entry.SourceTerm.Trim();
                entry.TargetTerm = (entry.TargetTerm ?? string.Empty).Trim();
            }
        }
    }

    private void SanitizeAndProtectSecrets(AppSettings settings, AppSettings? previous = null)
    {
        if (settings.SchemaVersion <= 0)
        {
            settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        }

        if (settings.PrimaryTargetLanguage.IsAuto)
        {
            settings.PrimaryTargetLanguage = LanguageCode.ChineseSimplified;
        }

        if (settings.SecondaryTargetLanguage.IsAuto)
        {
            settings.SecondaryTargetLanguage = LanguageCode.English;
        }

        Dictionary<string, string>? previousProviderKeys = null;
        if (previous?.Providers is { Count: > 0 })
        {
            previousProviderKeys = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prev in previous.Providers)
            {
                if (!string.IsNullOrWhiteSpace(prev.Id) && !string.IsNullOrWhiteSpace(prev.EncryptedApiKey))
                {
                    previousProviderKeys[prev.Id] = prev.EncryptedApiKey;
                }
            }
        }

        foreach (var provider in settings.Providers)
        {
            string? existingHint = null;
            previousProviderKeys?.TryGetValue(provider.Id, out existingHint);
            provider.EncryptedApiKey = EnsureProtected(provider.EncryptedApiKey, existingHint);
        }

        settings.Ocr.EncryptedVisionApiKey = EnsureProtected(
            settings.Ocr.EncryptedVisionApiKey,
            previous?.Ocr?.EncryptedVisionApiKey);
        settings.Proxy.EncryptedPassword = EnsureProtected(
            settings.Proxy.EncryptedPassword,
            previous?.Proxy?.EncryptedPassword);
    }

    private string EnsureProtected(string? value, string? existingProtectedHint = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(existingProtectedHint) &&
            DpapiSecretProtector.IsProtected(existingProtectedHint) &&
            _secretProtector.TryUnprotect(existingProtectedHint, out var existingPlain))
        {
            if (DpapiSecretProtector.IsProtected(value))
            {
                if (string.Equals(value, existingProtectedHint, StringComparison.Ordinal) ||
                    (_secretProtector.TryUnprotect(value, out var incomingPlain) &&
                     string.Equals(incomingPlain, existingPlain, StringComparison.Ordinal)))
                {
                    return existingProtectedHint;
                }
            }
            else if (string.Equals(value, existingPlain, StringComparison.Ordinal))
            {
                return existingProtectedHint;
            }
        }

        if (DpapiSecretProtector.IsProtected(value))
        {
            return value;
        }

        return _secretProtector.Protect(value);
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                File.Copy(SettingsFilePath, SettingsFilePath + ".corrupt", overwrite: true);
            }
        }
        catch
        {
            // Best-effort backup only.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore cleanup errors.
        }
    }
}
