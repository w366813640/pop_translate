using PopTranslate.Core.Models;
using PopTranslate.Win32.Guards;
using Xunit;

namespace PopTranslate.Tests.Win32;

public sealed class ForegroundAppGuardTests
{
    private readonly ForegroundAppGuard _guard = new();

    [Theory]
    [InlineData("WindowsTerminal.exe", true)]
    [InlineData("windowsterminal.exe", true)]
    [InlineData("WindowsTerminal", true)]
    [InlineData("CMD", true)]
    [InlineData("powershell.exe", true)]
    [InlineData("pwsh", true)]
    [InlineData("conhost.exe", true)]
    [InlineData("mstsc.exe", true)]
    [InlineData(@"C:\Windows\System32\cmd.exe", true)]
    [InlineData("notepad.exe", false)]
    [InlineData("chrome", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsProcessBlacklisted_MatchesNormalizedProcessNamesCaseInsensitively(string? processName, bool expected)
    {
        var result = _guard.IsProcessBlacklisted(processName, SelectionConfig.DefaultProcessBlacklist);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsProcessBlacklisted_HandlesBlacklistEntriesWithoutExeSuffix()
    {
        string[] customBlacklist = ["WindowsTerminal", "  Code.exe  ", @"C:\Apps\MyGame.exe", ""];

        Assert.True(_guard.IsProcessBlacklisted("windowsterminal.exe", customBlacklist));
        Assert.True(_guard.IsProcessBlacklisted("code", customBlacklist));
        Assert.True(_guard.IsProcessBlacklisted("mygame", customBlacklist));
        Assert.False(_guard.IsProcessBlacklisted("edge.exe", customBlacklist));
    }

    [Fact]
    public void IsFullscreenAppActive_ZeroHandle_ReturnsFalse()
    {
        Assert.False(_guard.IsFullscreenAppActive(nint.Zero));
    }

    [Fact]
    public void IsFullscreenAppActive_DesktopOrInvalidHandle_ReturnsFalse()
    {
        Assert.False(_guard.IsFullscreenAppActive((nint)0x1234567));
    }

    [Fact]
    public void GetProcessNameForWindow_ZeroHandle_ReturnsNull()
    {
        Assert.Null(_guard.GetProcessNameForWindow(nint.Zero));
    }

    [Fact]
    public void IsForegroundAppSuppressed_BlacklistedActiveProcess_ReturnsTrueWithReason()
    {
        var config = new SelectionConfig
        {
            QuietModeInFullscreen = false,
            ProcessBlacklist = ["example-app.exe"],
        };

        var suppressed = _guard.EvaluateSuppression(
            foregroundWindow: nint.Zero,
            processName: "EXAMPLE-APP",
            config: config,
            out var reason);

        Assert.True(suppressed);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void IsForegroundAppSuppressed_NonBlacklistedAndQuietModeDisabled_ReturnsFalse()
    {
        var config = new SelectionConfig
        {
            QuietModeInFullscreen = false,
            ProcessBlacklist = ["cmd.exe"],
        };

        var suppressed = _guard.EvaluateSuppression(
            foregroundWindow: nint.Zero,
            processName: "notepad.exe",
            config: config,
            out var reason);

        Assert.False(suppressed);
        Assert.Null(reason);
    }
}
