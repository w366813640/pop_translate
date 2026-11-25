using PopTranslate.App.Services;
using PopTranslate.App.ViewModels;
using PopTranslate.App.Windows;
using PopTranslate.Core.Configuration;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using PopTranslate.Win32.Windowing;
using Xunit;

namespace PopTranslate.Tests.Integration;

public sealed class DefaultHotkeysAndTaskbarCenteringTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public DefaultHotkeysAndTaskbarCenteringTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"PopTranslate_DefaultsCenter_{Guid.NewGuid():N}");
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
    public void HotkeyConfig_DefaultGestures_MatchRequestedDefaults()
    {
        var config = new HotkeyConfig();

        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Alt, 0x45), HotkeyConfig.DefaultSelectionTranslate);
        Assert.Equal("Alt + E", config.SelectionTranslate.DisplayText);

        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Alt, 0x52), HotkeyConfig.DefaultReplaceTranslate);
        Assert.Equal("Alt + R", config.ReplaceTranslate.DisplayText);

        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Alt, 0x57), HotkeyConfig.DefaultScreenshotTranslate);
        Assert.Equal("Alt + W", config.ScreenshotTranslate.DisplayText);

        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x57), HotkeyConfig.DefaultSilentOcr);
        Assert.Equal("Alt + Shift + W", config.SilentOcr.DisplayText);

        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Alt, 0x51), HotkeyConfig.DefaultSelectionPolish);
        Assert.Equal("Alt + Q", config.SelectionPolish.DisplayText);
    }

    [Fact]
    public async Task HotkeysSettingsViewModel_ResetDefaultHotkeys_RestoresAllFiveNewDefaultsAndStatusMessage()
    {
        var store = new JsonSettingsStore(new DpapiSecretProtector(), _settingsPath);
        var custom = AppSettings.CreateDefault();
        custom.Hotkeys.SelectionTranslate = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x41);
        custom.Hotkeys.ReplaceTranslate = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x42);
        custom.Hotkeys.ScreenshotTranslate = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x43);
        custom.Hotkeys.SilentOcr = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x44);
        custom.Hotkeys.SelectionPolish = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x46);
        await store.SaveAsync(custom);

        var vm = new HotkeysSettingsViewModel(store);
        vm.LoadFrom(store.Current);

        vm.ResetDefaultHotkeysCommand.Execute(null);
        await Task.Delay(150);

        Assert.Equal("Alt + E", vm.SelectionTranslateGesture.DisplayText);
        Assert.Equal("Alt + R", vm.ReplaceTranslateGesture.DisplayText);
        Assert.Equal("Alt + W", vm.ScreenshotTranslateGesture.DisplayText);
        Assert.Equal("Alt + Shift + W", vm.SilentOcrGesture.DisplayText);
        Assert.Equal("Alt + Q", vm.SelectionPolishGesture.DisplayText);
        Assert.Contains("Alt+E / Alt+R / Alt+W / Alt+Shift+W / Alt+Q", vm.StatusMessage ?? string.Empty, StringComparison.Ordinal);

        var reloaded = await store.LoadAsync();
        Assert.Equal("Alt + E", reloaded.Hotkeys.SelectionTranslate.DisplayText);
        Assert.Equal("Alt + R", reloaded.Hotkeys.ReplaceTranslate.DisplayText);
        Assert.Equal("Alt + W", reloaded.Hotkeys.ScreenshotTranslate.DisplayText);
        Assert.Equal("Alt + Shift + W", reloaded.Hotkeys.SilentOcr.DisplayText);
        Assert.Equal("Alt + Q", reloaded.Hotkeys.SelectionPolish.DisplayText);
    }

    [Fact]
    public async Task TrayAndSettingsXaml_DisplayUpdatedDefaultHotkeys()
    {
        var defaults = AppSettings.CreateDefault();
        using var tray = new TrayIconHost();
        tray.SyncMenuState(defaults, isRunningAsAdministrator: false);

        Assert.Contains("Alt + E", tray.InputTranslateLabel, StringComparison.Ordinal);
        Assert.Contains("Alt + R", tray.ReplaceTranslateLabel, StringComparison.Ordinal);
        Assert.Contains("Alt + Q", tray.PolishInputLabel, StringComparison.Ordinal);
        Assert.Contains("Alt + W", tray.ScreenshotTranslateLabel, StringComparison.Ordinal);
        Assert.Contains("Alt + Shift + W", tray.SilentOcrLabel, StringComparison.Ordinal);

        var repoRoot = FindRepoRoot();
        var hotkeysXaml = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "src", "PopTranslate.App", "Pages", "HotkeysSettingsPage.xaml"));
        Assert.Contains("默认 Alt + R", hotkeysXaml, StringComparison.Ordinal);
        Assert.Contains("默认 Alt + Shift + W", hotkeysXaml, StringComparison.Ordinal);
        Assert.Contains("默认 Alt + Q", hotkeysXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("默认 Alt + Shift + S", hotkeysXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("默认 Alt + P", hotkeysXaml, StringComparison.Ordinal);

        var providersXaml = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "src", "PopTranslate.App", "Pages", "ProvidersSettingsPage.xaml"));
        Assert.Contains("Alt+Q", providersXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Alt+P", providersXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void CenterPositionInWorkArea_CentersWindowAcrossPrimarySecondaryAndOversizedCases()
    {
        // Primary monitor 1920x1040 work area (bottom taskbar 40px)
        var primary = WindowHelperService.CenterPositionInWorkArea(
            width: 460,
            height: 260,
            workArea: new ScreenRect(0, 0, 1920, 1040));
        Assert.Equal(new ScreenPoint(730, 390), primary);

        // Left secondary monitor with negative X and top taskbar (Y = 48)
        var secondary = WindowHelperService.CenterPositionInWorkArea(
            width: 460,
            height: 260,
            workArea: new ScreenRect(-1920, 48, 1920, 1032));
        Assert.Equal(new ScreenPoint(-1190, 434), secondary);

        // Oversized window clamps to work area origin
        var oversized = WindowHelperService.CenterPositionInWorkArea(
            width: 1200,
            height: 900,
            workArea: new ScreenRect(100, 50, 800, 600));
        Assert.Equal(new ScreenPoint(100, 50), oversized);
    }

    [Fact]
    public void ResolvePopupPosition_CenterOnScreen_CentersInFollowCursorMode_AndRespectsFixedPositionMode()
    {
        var workArea = new ScreenRect(0, 0, 1920, 1040);
        var fakeHelper = new StubWindowHelperService(workArea);
        var taskbarCursor = new ScreenPoint(1820, 1055);

        // 1. FollowCursor + centerOnScreen=true -> desktop center, NOT bottom-right taskbar corner
        var followConfig = new WindowBehaviorConfig { PositionMode = WindowPositionMode.FollowCursor };
        var centered = TranslationPopupWindow.ResolvePopupPosition(
            fakeHelper,
            followConfig,
            anchor: taskbarCursor,
            widthPx: 460,
            heightPx: 260,
            cursorOffsetPx: 14,
            centerOnScreen: true);
        Assert.Equal(new ScreenPoint(730, 390), centered);

        // 2. FollowCursor + centerOnScreen=false -> follows cursor with clamping
        var followed = TranslationPopupWindow.ResolvePopupPosition(
            fakeHelper,
            followConfig,
            anchor: new ScreenPoint(300, 200),
            widthPx: 460,
            heightPx: 260,
            cursorOffsetPx: 14,
            centerOnScreen: false);
        Assert.Equal(new ScreenPoint(314, 214), followed);

        // 3. RememberFixedPosition + centerOnScreen=true -> respects user's explicit fixed position
        var fixedConfig = new WindowBehaviorConfig
        {
            PositionMode = WindowPositionMode.RememberFixedPosition,
            FixedX = 120,
            FixedY = 180,
        };
        var fixedPos = TranslationPopupWindow.ResolvePopupPosition(
            fakeHelper,
            fixedConfig,
            anchor: taskbarCursor,
            widthPx: 460,
            heightPx: 260,
            cursorOffsetPx: 14,
            centerOnScreen: true);
        Assert.Equal(new ScreenPoint(120, 180), fixedPos);
    }

    [Fact]
    public async Task AppCoordinator_TrayAndTaskbarActivationPaths_RequestCenteredPopup()
    {
        var repoRoot = FindRepoRoot();
        var coordinatorCode = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "src", "PopTranslate.App", "Services", "AppCoordinator.cs"));

        Assert.Contains(
            "_trayIconHost.InputTranslateRequested += (_, _) => PostUi(() => OpenInputTranslation(centerOnScreen: true));",
            coordinatorCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "_trayIconHost.PolishInputRequested += (_, _) => PostUi(() => OpenInputPolish(centerOnScreen: true));",
            coordinatorCode,
            StringComparison.Ordinal);
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

    private sealed class StubWindowHelperService : IWindowHelperService
    {
        private readonly MonitorInfo _monitor;

        public StubWindowHelperService(ScreenRect workArea)
        {
            _monitor = new MonitorInfo(
                Handle: 1,
                DeviceName: @"\\.\DISPLAY1",
                Bounds: workArea,
                WorkArea: workArea,
                DpiX: 96,
                DpiY: 96,
                IsPrimary: true);
        }

        public IReadOnlyList<MonitorInfo> GetMonitors() => [_monitor];
        public MonitorInfo GetMonitorAtPoint(ScreenPoint point) => _monitor;
        public ScreenPoint GetCursorPosition() => new(1820, 1055);
        public nint GetForegroundWindowHandle() => nint.Zero;

        public ScreenPoint CalculateClampedWindowPosition(
            ScreenPoint anchorPoint,
            int windowWidthPx,
            int windowHeightPx,
            int cursorOffsetPx = 12) =>
            WindowHelperService.ClampPositionToWorkArea(anchorPoint, windowWidthPx, windowHeightPx, _monitor.WorkArea, cursorOffsetPx);

        public ScreenPoint CalculateCenteredWindowPosition(
            ScreenPoint referencePoint,
            int windowWidthPx,
            int windowHeightPx) =>
            WindowHelperService.CenterPositionInWorkArea(windowWidthPx, windowHeightPx, _monitor.WorkArea);

        public double GetDpiScaleForWindow(nint windowHandle) => 1.0;
        public void ConfigureNoActivateOverlayWindow(nint windowHandle) { }
        public void ConfigurePopupToolWindow(nint windowHandle, bool hideFromTaskbar = true) { }
        public void SetWindowTopMost(nint windowHandle, bool topMost) { }
        public void MoveAndResizeWindow(nint windowHandle, int x, int y, int width, int height, bool activate = false) { }
        public void ShowWindowNoActivate(nint windowHandle) { }
        public void ForceForegroundWindow(nint windowHandle) { }
    }
}
