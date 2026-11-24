using PopTranslate.Core.Models;
using PopTranslate.Win32.Hotkeys;
using Xunit;

namespace PopTranslate.Tests.Win32;

public sealed class GlobalHotkeyServiceTests
{
    private const uint VkF9 = 0x78;
    private const uint VkF10 = 0x79;
    private const uint VkF11 = 0x7A;
    private const uint VkF12 = 0x7B;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    [Fact]
    public void ToWin32Modifiers_IncludesNoRepeatFlag()
    {
        var flags = GlobalHotkeyService.ToWin32Modifiers(
            HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift);

        Assert.Equal(ModControl | ModAlt | ModShift | ModNoRepeat, flags);
    }

    [Fact]
    public void ToWin32Modifiers_WinFlagMappedCorrectly()
    {
        var flags = GlobalHotkeyService.ToWin32Modifiers(HotkeyModifiers.Win);
        Assert.Equal(ModWin | ModNoRepeat, flags);
    }

    [Fact]
    public void ApplyHotkeys_ValidNonConflictingGesture_RegistersAndUnregistersCleanly()
    {
        using var service = new GlobalHotkeyService();
        var config = new HotkeyConfig
        {
            SelectionTranslate = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, VkF11),
            InputTranslate = HotkeyGesture.Empty,
            ScreenshotTranslate = HotkeyGesture.Empty,
            SelectionPolish = HotkeyGesture.Empty,
            SilentOcr = HotkeyGesture.Empty,
        };

        var results = service.ApplyHotkeys(config);

        Assert.Equal(5, results.Count);
        Assert.True(results[HotkeyAction.SelectionTranslate].Succeeded, results[HotkeyAction.SelectionTranslate].ErrorMessage);
        Assert.True(results[HotkeyAction.InputTranslate].Succeeded);
        Assert.True(results[HotkeyAction.ScreenshotTranslate].Succeeded);
        Assert.True(results[HotkeyAction.SelectionPolish].Succeeded);
        Assert.True(results[HotkeyAction.SilentOcr].Succeeded);

        service.UnregisterAll();

        // Re-applying after unregister succeeds cleanly.
        var reapplied = service.ApplyHotkeys(config);
        Assert.True(reapplied[HotkeyAction.SelectionTranslate].Succeeded);
    }

    [Fact]
    public void ApplyHotkeys_SelectionPolishGesture_RegistersUnregistersAndDetectsConflicts()
    {
        using var serviceA = new GlobalHotkeyService();
        using var serviceB = new GlobalHotkeyService();
        var polishGesture = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, VkF12);

        var config = new HotkeyConfig
        {
            SelectionTranslate = HotkeyGesture.Empty,
            InputTranslate = HotkeyGesture.Empty,
            ScreenshotTranslate = HotkeyGesture.Empty,
            SelectionPolish = polishGesture,
            SilentOcr = HotkeyGesture.Empty,
        };

        var resA = serviceA.ApplyHotkeys(config);
        Assert.Equal(5, resA.Count);
        Assert.True(resA[HotkeyAction.SelectionPolish].Succeeded, resA[HotkeyAction.SelectionPolish].ErrorMessage);

        // External Win32 conflict on SelectionPolish
        var resB = serviceB.ApplyHotkeys(config);
        Assert.False(resB[HotkeyAction.SelectionPolish].Succeeded);
        Assert.Contains("1409", resB[HotkeyAction.SelectionPolish].ErrorMessage ?? string.Empty);

        // Release and verify internal duplicate conflict with SelectionTranslate
        serviceA.UnregisterAll();
        var duplicateConfig = new HotkeyConfig
        {
            SelectionTranslate = polishGesture,
            InputTranslate = HotkeyGesture.Empty,
            ScreenshotTranslate = HotkeyGesture.Empty,
            SelectionPolish = polishGesture,
            SilentOcr = HotkeyGesture.Empty,
        };

        var dupResults = serviceA.ApplyHotkeys(duplicateConfig);
        Assert.Equal(5, dupResults.Count);
        Assert.True(dupResults[HotkeyAction.SelectionTranslate].Succeeded, dupResults[HotkeyAction.SelectionTranslate].ErrorMessage);
        Assert.False(dupResults[HotkeyAction.SelectionPolish].Succeeded);
        Assert.Contains("SelectionTranslate", dupResults[HotkeyAction.SelectionPolish].ErrorMessage ?? string.Empty);
    }

    [Fact]
    public void ApplyHotkeys_DuplicateGestureInSameConfig_ReportsConflictOnSecondAction()
    {
        using var service = new GlobalHotkeyService();
        var sharedGesture = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, VkF10);
        var config = new HotkeyConfig
        {
            SelectionTranslate = sharedGesture,
            InputTranslate = sharedGesture,
            ScreenshotTranslate = HotkeyGesture.Empty,
            SelectionPolish = HotkeyGesture.Empty,
            SilentOcr = HotkeyGesture.Empty,
        };

        var results = service.ApplyHotkeys(config);

        Assert.Equal(5, results.Count);
        Assert.True(results[HotkeyAction.SelectionTranslate].Succeeded, results[HotkeyAction.SelectionTranslate].ErrorMessage);
        Assert.False(results[HotkeyAction.InputTranslate].Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(results[HotkeyAction.InputTranslate].ErrorMessage));
    }

    [Fact]
    public void ApplyHotkeys_ConflictWithAnotherRegistration_ReportsWin32Conflict()
    {
        using var serviceA = new GlobalHotkeyService();
        using var serviceB = new GlobalHotkeyService();
        var gesture = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, VkF9);
        var config = new HotkeyConfig
        {
            SelectionTranslate = gesture,
            InputTranslate = HotkeyGesture.Empty,
            ScreenshotTranslate = HotkeyGesture.Empty,
            SelectionPolish = HotkeyGesture.Empty,
            SilentOcr = HotkeyGesture.Empty,
        };

        var resA = serviceA.ApplyHotkeys(config);
        Assert.True(resA[HotkeyAction.SelectionTranslate].Succeeded, resA[HotkeyAction.SelectionTranslate].ErrorMessage);

        var resB = serviceB.ApplyHotkeys(config);
        Assert.False(resB[HotkeyAction.SelectionTranslate].Succeeded);
        Assert.Contains("1409", resB[HotkeyAction.SelectionTranslate].ErrorMessage ?? string.Empty);
    }

    [Fact]
    public void Dispose_IsIdempotentAndReleasesHotkeys()
    {
        var gesture = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, VkF11);
        var config = new HotkeyConfig
        {
            SelectionTranslate = gesture,
            InputTranslate = HotkeyGesture.Empty,
            ScreenshotTranslate = HotkeyGesture.Empty,
            SelectionPolish = HotkeyGesture.Empty,
            SilentOcr = HotkeyGesture.Empty,
        };

        var service1 = new GlobalHotkeyService();
        var res1 = service1.ApplyHotkeys(config);
        Assert.True(res1[HotkeyAction.SelectionTranslate].Succeeded, res1[HotkeyAction.SelectionTranslate].ErrorMessage);
        service1.Dispose();
        service1.Dispose();

        // After service1 disposal, another instance can claim the exact same hotkey.
        using var service2 = new GlobalHotkeyService();
        var res2 = service2.ApplyHotkeys(config);
        Assert.True(res2[HotkeyAction.SelectionTranslate].Succeeded, res2[HotkeyAction.SelectionTranslate].ErrorMessage);
    }
}
