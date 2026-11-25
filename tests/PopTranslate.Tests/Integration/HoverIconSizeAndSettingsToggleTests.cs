using Microsoft.Extensions.DependencyInjection;
using PopTranslate.App.Services;
using PopTranslate.App.ViewModels;
using PopTranslate.App.Windows;
using PopTranslate.Core.Configuration;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.DependencyInjection;
using PopTranslate.Core.Models;
using PopTranslate.Win32.DependencyInjection;
using Xunit;

namespace PopTranslate.Tests.Integration;

public sealed class HoverIconSizeAndSettingsToggleTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public HoverIconSizeAndSettingsToggleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"PopTranslate_HoverIcon_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore temp cleanup races.
        }
    }

    [Fact]
    public async Task FloatingHoverIconWindow_UsesPlainWinUIWindowAndCompactNarrowDimensions()
    {
        // Must inherit directly from Microsoft.UI.Xaml.Window, NOT WinUIEx.WindowEx (which clamps MinWidth to 139 DIPs).
        Assert.Equal(typeof(Microsoft.UI.Xaml.Window), typeof(FloatingHoverIconWindow).BaseType);

        Assert.InRange(FloatingHoverIconWindow.IconWidthDips, 24, 32);
        Assert.InRange(FloatingHoverIconWindow.IconHeightDips, 24, 34);
        Assert.True(FloatingHoverIconWindow.IconWidthDips <= FloatingHoverIconWindow.IconHeightDips);

        var (w100, h100) = FloatingHoverIconWindow.CalculatePhysicalSize(1.0);
        Assert.Equal((int)Math.Ceiling(FloatingHoverIconWindow.IconWidthDips), w100);
        Assert.Equal((int)Math.Ceiling(FloatingHoverIconWindow.IconHeightDips), h100);

        var (w150, h150) = FloatingHoverIconWindow.CalculatePhysicalSize(1.5);
        Assert.Equal((int)Math.Ceiling(FloatingHoverIconWindow.IconWidthDips * 1.5), w150);
        Assert.Equal((int)Math.Ceiling(FloatingHoverIconWindow.IconHeightDips * 1.5), h150);

        var repoRoot = FindRepoRoot();
        var xaml = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "src", "PopTranslate.App", "Windows", "FloatingHoverIconWindow.xaml"));
        Assert.DoesNotContain("winex:WindowEx", xaml, StringComparison.Ordinal);
        Assert.Contains("<Window", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HoverIconToggle_AvailableInGeneralAndHotkeysSettings_PersistsAndStaysSynchronized()
    {
        var services = new ServiceCollection();
        services.AddPopTranslateCore();
        services.AddPopTranslateWin32();
        services.AddPopTranslateApp();
        services.AddSingleton<ISettingsStore>(sp =>
            new JsonSettingsStore(sp.GetRequiredService<ISecretProtector>(), _settingsPath));
        services.AddSingleton<IStartupPrivilegeService, NoOpStartupPrivilegeService>();

        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<ISettingsStore>();
        await store.SaveAsync(AppSettings.CreateDefault());

        var settingsVm = provider.GetRequiredService<SettingsViewModel>();
        var generalVm = settingsVm.General;
        var hotkeysVm = settingsVm.Hotkeys;

        generalVm.LoadFrom(store.Current);
        hotkeysVm.LoadFrom(store.Current);
        Assert.True(generalVm.EnableHoverIcon);
        Assert.True(hotkeysVm.EnableHoverIcon);

        // 1. Toggle OFF from General settings -> persists and immediately syncs Hotkeys VM.
        var disabledSaved = WaitForNextSettingsSaveAsync(store);
        generalVm.EnableHoverIcon = false;
        await disabledSaved;

        Assert.False(hotkeysVm.EnableHoverIcon);
        var afterDisable = await store.LoadAsync();
        Assert.False(afterDisable.Selection.EnableHoverIcon);

        // 2. Toggle ON from Hotkeys settings -> persists and immediately syncs General VM.
        var enabledSaved = WaitForNextSettingsSaveAsync(store);
        hotkeysVm.EnableHoverIcon = true;
        await enabledSaved;

        Assert.True(generalVm.EnableHoverIcon);
        var afterEnable = await store.LoadAsync();
        Assert.True(afterEnable.Selection.EnableHoverIcon);

        // 3. External sync (e.g. from system tray or hover icon right-click) updates General VM without saving.
        generalVm.SyncHoverIconFromExternal(false);
        Assert.False(generalVm.EnableHoverIcon);
        var stillEnabledInStore = await store.LoadAsync();
        Assert.True(stillEnabledInStore.Selection.EnableHoverIcon);

        // 4. Both GeneralSettingsPage.xaml and HotkeysSettingsPage.xaml bind EnableHoverIcon.
        var repoRoot = FindRepoRoot();
        var generalXaml = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "src", "PopTranslate.App", "Pages", "GeneralSettingsPage.xaml"));
        var hotkeysXaml = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "src", "PopTranslate.App", "Pages", "HotkeysSettingsPage.xaml"));

        Assert.Contains("ViewModel.EnableHoverIcon", generalXaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.EnableHoverIcon", hotkeysXaml, StringComparison.Ordinal);
    }

    private static Task WaitForNextSettingsSaveAsync(ISettingsStore store)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<AppSettings>? handler = null;
        handler = (_, _) =>
        {
            store.SettingsChanged -= handler;
            tcs.TrySetResult();
        };
        store.SettingsChanged += handler;
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "PopTranslate.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return dir!;
    }

    private sealed class NoOpStartupPrivilegeService : IStartupPrivilegeService
    {
        public bool IsRunningAsAdministrator() => false;
        public bool IsStandardStartupEnabled() => false;
        public void SetStandardStartup(bool enabled, string executablePath) { }
        public bool IsElevatedStartupTaskEnabled() => false;
        public Task<PrivilegeOperationResult> SetElevatedStartupTaskAsync(
            bool enabled,
            string executablePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PrivilegeOperationResult(true, string.Empty));
        public bool RestartAsAdministrator(string executablePath, string? arguments = null) => false;
    }
}
