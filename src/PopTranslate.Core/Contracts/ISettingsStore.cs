using PopTranslate.Core.Models;

namespace PopTranslate.Core.Contracts;

public interface ISettingsStore
{
    string SettingsFilePath { get; }

    AppSettings Current { get; }

    event EventHandler<AppSettings>? SettingsChanged;

    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
