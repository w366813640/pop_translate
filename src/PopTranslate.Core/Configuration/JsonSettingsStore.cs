using System.Text.Json;
using System.Text.Json.Serialization;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace PopTranslate.Core.Configuration;

public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ISecretProtector _secretProtector;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private AppSettings _current;

    [ActivatorUtilitiesConstructor]
    public JsonSettingsStore(ISecretProtector secretProtector)
        : this(secretProtector, customSettingsFilePath: null)
    {
    }

    public JsonSettingsStore(ISecretProtector secretProtector, string? customSettingsFilePath)
    {
        _secretProtector = secretProtector ?? throw new ArgumentNullException(nameof(secretProtector));
        SettingsFilePath = string.IsNullOrWhiteSpace(customSettingsFilePath)
            ? GetDefaultSettingsPath()
            : Path.GetFullPath(customSettingsFilePath);
        _current = AppSettings.CreateDefault();
    }

    public JsonSettingsStore(string? customSettingsFilePath = null)
        : this(new DpapiSecretProtector(), customSettingsFilePath)
    {
    }

    public string SettingsFilePath { get; }

    public AppSettings Current => Volatile.Read(ref _current);

    public event EventHandler<AppSettings>? SettingsChanged;

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                var defaults = AppSettings.CreateDefault();
                Volatile.Write(ref _current, defaults);
                return defaults.Clone();
            }

            try
            {
                await using var stream = new FileStream(
                    SettingsFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    useAsync: true);

                var loaded = await JsonSerializer.DeserializeAsync<AppSettings>(
                    stream,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);

                if (loaded is null)
                {
                    TryBackupCorruptFile();
                    var defaults = AppSettings.CreateDefault();
                    Volatile.Write(ref _current, defaults);
                    return defaults.Clone();
                }

                SanitizeAndProtectSecrets(loaded);
                Volatile.Write(ref _current, loaded);
                return loaded.Clone();
            }
            catch (JsonException)
            {
                TryBackupCorruptFile();
                var defaults = AppSettings.CreateDefault();
                Volatile.Write(ref _current, defaults);
                return defaults.Clone();
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

        var snapshot = settings.Clone();
        SanitizeAndProtectSecrets(snapshot);

        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

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
                    await JsonSerializer.SerializeAsync(
                        stream,
                        snapshot,
                        SerializerOptions,
                        cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tempPath, SettingsFilePath, overwrite: true);
            }
            catch
            {
                TryDeleteFile(tempPath);
                throw;
            }

            Volatile.Write(ref _current, snapshot);
        }
        finally
        {
            _ioLock.Release();
        }

        SettingsChanged?.Invoke(this, snapshot.Clone());
    }

    private void SanitizeAndProtectSecrets(AppSettings settings)
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

        settings.Hotkeys ??= new HotkeyConfig();
        settings.Hotkeys.SelectionTranslate ??= HotkeyConfig.DefaultSelectionTranslate;
        settings.Hotkeys.InputTranslate ??= HotkeyConfig.DefaultInputTranslate;
        settings.Hotkeys.ScreenshotTranslate ??= HotkeyConfig.DefaultScreenshotTranslate;
        settings.Hotkeys.SelectionPolish ??= HotkeyConfig.DefaultSelectionPolish;
        settings.Selection ??= new SelectionConfig();
        settings.Selection.ProcessBlacklist ??= [.. SelectionConfig.DefaultProcessBlacklist];
        settings.Ocr ??= new OcrConfig();
        settings.Proxy ??= new ProxyConfig();
        settings.Window ??= new WindowBehaviorConfig();
        settings.Polish ??= new PolishConfig();
        settings.Polish.CustomPrompt ??= PolishConfig.DefaultCustomPrompt;
        settings.Providers ??= AppSettings.CreateDefault().Providers;

        foreach (var provider in settings.Providers)
        {
            provider.EncryptedApiKey = EnsureProtected(provider.EncryptedApiKey);
        }

        settings.Ocr.EncryptedVisionApiKey = EnsureProtected(settings.Ocr.EncryptedVisionApiKey);
        settings.Proxy.EncryptedPassword = EnsureProtected(settings.Proxy.EncryptedPassword);
    }

    private string EnsureProtected(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
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

    private static string GetDefaultSettingsPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PopTranslate",
            "settings.json");
}
