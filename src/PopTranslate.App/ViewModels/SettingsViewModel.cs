using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PopTranslate.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _settingsStore;

    public SettingsViewModel(
        ISettingsStore settingsStore,
        GeneralSettingsViewModel general,
        ProvidersSettingsViewModel providers,
        OcrSettingsViewModel ocr,
        HotkeysSettingsViewModel hotkeys,
        NetworkSettingsViewModel network)
    {
        _settingsStore = settingsStore;
        General = general;
        Providers = providers;
        Ocr = ocr;
        Hotkeys = hotkeys;
        Network = network;

        Network.RestartAsAdministratorRequested += (_, _) =>
            RestartAsAdministratorRequested?.Invoke(this, EventArgs.Empty);

        General.StandardStartupToggled += (_, enabled) =>
        {
            if (enabled)
            {
                Network.SyncElevatedStartupFromExternal(false);
            }
        };

        Network.ElevatedStartupToggled += (_, enabled) =>
        {
            if (enabled)
            {
                General.SyncStandardStartupFromExternal(false);
            }
        };
    }

    public event EventHandler? RestartAsAdministratorRequested;

    public GeneralSettingsViewModel General { get; }

    public ProvidersSettingsViewModel Providers { get; }

    public OcrSettingsViewModel Ocr { get; }

    public HotkeysSettingsViewModel Hotkeys { get; }

    public NetworkSettingsViewModel Network { get; }

    [ObservableProperty]
    public partial string SelectedPageTag { get; set; } = "General";

    public async Task RefreshAllAsync()
    {
        var current = _settingsStore.Current;
        General.LoadFrom(current);
        Providers.LoadFrom(current);
        Hotkeys.LoadFrom(current);
        await Task.WhenAll(
            Network.LoadFromAsync(current),
            Ocr.LoadFromAsync(current));
    }

    public async Task FlushPendingChangesAsync()
    {
        await General.FlushIfDirtyAsync();
        await Providers.FlushIfDirtyAsync();
        await Ocr.FlushIfDirtyAsync();
        await Network.FlushIfDirtyAsync();
    }

    public void UpdateHotkeyRegistrationResults(IReadOnlyDictionary<HotkeyAction, HotkeyRegistrationResult> results)
    {
        Hotkeys.UpdateRegistrationResults(results);
    }
}
