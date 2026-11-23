using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using PopTranslate.Win32.DependencyInjection;
using PopTranslate.Win32.Ocr;
using PopTranslate.Win32.Startup;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace PopTranslate.Tests.Win32;

public sealed class StartupPrivilegeServiceTests
{
    [Fact]
    public void FormatStandardStartupValue_QuotesPathAndAddsMinimizedArg()
    {
        var value = StartupPrivilegeService.FormatStandardStartupValue(@"C:\Program Files\PopTranslate\PopTranslate.App.exe");
        Assert.Equal("\"C:\\Program Files\\PopTranslate\\PopTranslate.App.exe\" --minimized", value);

        var trimmed = StartupPrivilegeService.FormatStandardStartupValue("  \"D:\\Tools\\PopTranslate.App.exe\"  ");
        Assert.Equal("\"D:\\Tools\\PopTranslate.App.exe\" --minimized", trimmed);
    }

    [Fact]
    public void BuildSchtasksArguments_ProducesValidOnLogonHighestTaskCommands()
    {
        var query = StartupPrivilegeService.BuildSchtasksQueryArguments("PopTranslateElevatedAutoStart");
        Assert.Equal("/Query /TN \"PopTranslateElevatedAutoStart\"", query);

        var create = StartupPrivilegeService.BuildSchtasksCreateArguments(
            "PopTranslateElevatedAutoStart",
            @"C:\Program Files\PopTranslate\PopTranslate.App.exe");
        Assert.Contains("/Create", create);
        Assert.Contains("/TN \"PopTranslateElevatedAutoStart\"", create);
        Assert.Contains("/SC ONLOGON", create);
        Assert.Contains("/RL HIGHEST", create);
        Assert.Contains("/F", create);
        Assert.Contains("--minimized", create);

        var delete = StartupPrivilegeService.BuildSchtasksDeleteArguments("PopTranslateElevatedAutoStart");
        Assert.Equal("/Delete /TN \"PopTranslateElevatedAutoStart\" /F", delete);
    }

    [Fact]
    public void IsRunningAsAdministrator_ExecutesWithoutThrowing()
    {
        var service = new StartupPrivilegeService();
        _ = service.IsRunningAsAdministrator();
    }

    [Fact]
    public void StandardStartupRegistry_IsolatedTestValue_RoundTripsCleanly()
    {
        var testValueName = $"PopTranslate_Test_{Guid.NewGuid():N}";
        var service = new StartupPrivilegeService(registryValueName: testValueName);

        try
        {
            Assert.False(service.IsStandardStartupEnabled());

            service.SetStandardStartup(true, @"C:\Test Path\PopTranslate.App.exe");
            Assert.True(service.IsStandardStartupEnabled());

            service.SetStandardStartup(false, string.Empty);
            Assert.False(service.IsStandardStartupEnabled());
        }
        finally
        {
            service.SetStandardStartup(false, string.Empty);
        }
    }

    [Fact]
    public async Task SetElevatedStartupTaskAsync_WhenNotElevated_ReturnsRequiresElevation()
    {
        var service = new StartupPrivilegeService(elevationChecker: () => false);

        var result = await service.SetElevatedStartupTaskAsync(true, @"C:\PopTranslate\PopTranslate.App.exe");

        Assert.False(result.Succeeded);
        Assert.True(result.RequiresElevation);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task SetElevatedStartupTaskAsync_EmptyExecutablePath_ReturnsValidationError()
    {
        var service = new StartupPrivilegeService(elevationChecker: () => true);

        var result = await service.SetElevatedStartupTaskAsync(true, "   ");

        Assert.False(result.Succeeded);
        Assert.False(result.RequiresElevation);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public void RestartAsAdministrator_EmptyPath_ReturnsFalse()
    {
        var service = new StartupPrivilegeService();
        Assert.False(service.RestartAsAdministrator("   "));
    }

    [Fact]
    public void AddPopTranslateWin32_ResolvesAllWin32ContractsAndWindowsOcrProvider()
    {
        var services = new ServiceCollection();
        services.AddPopTranslateWin32();

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IForegroundAppGuard>());
        Assert.NotNull(provider.GetRequiredService<IWindowHelperService>());
        Assert.NotNull(provider.GetRequiredService<IGlobalHotkeyService>());
        Assert.NotNull(provider.GetRequiredService<ITextSelectionService>());
        Assert.NotNull(provider.GetRequiredService<ISelectionHoverMonitor>());
        Assert.NotNull(provider.GetRequiredService<IScreenshotCaptureService>());
        Assert.NotNull(provider.GetRequiredService<IStartupPrivilegeService>());

        var ocrProviders = provider.GetServices<IOcrProvider>().ToList();
        var windowsOcr = Assert.Single(ocrProviders, p => p.Kind == OcrProviderKind.WindowsNative);
        Assert.IsType<WindowsOcrProvider>(windowsOcr);
    }
}
