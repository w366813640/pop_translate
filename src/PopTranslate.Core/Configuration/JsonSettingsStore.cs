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

    public static bool IsPortableMode() =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "portable"));

    public static string GetDefaultSettingsPath() =>
        IsPortableMode()
            ? Path.Combine(AppContext.BaseDirectory, "data", "settings.json")
            : Path.Combine(
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
                    FileShare.ReadWrite,
                    bufferSize: 4096,
                    useAsync: true);

                var loaded = await JsonSerializer.DeserializeAsync<AppSettings>(
                    stream,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);

                if (loaded is null)
                {
                    return BackupCorruptAndRestoreDefaults();
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
                return BackupCorruptAndRestoreDefaults();
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

                await MoveWithRetryAsync(tempPath, SettingsFilePath, cancellationToken).ConfigureAwait(false);
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

    private AppSettings BackupCorruptAndRestoreDefaults()
    {
        TryBackupCorruptFile();
        var defaults = AppSettings.CreateDefault();
        Volatile.Write(ref _current, defaults);
        return defaults.Clone();
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
        settings.Hotkeys.InputTranslate ??= HotkeyConfig.DefaultInputTranslate;
        settings.Hotkeys.ScreenshotTranslate ??= HotkeyConfig.DefaultScreenshotTranslate;
        settings.Hotkeys.SelectionPolish ??= HotkeyConfig.DefaultSelectionPolish;
        settings.Hotkeys.SilentOcr ??= HotkeyConfig.DefaultSilentOcr;

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
}
