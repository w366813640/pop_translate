using PopTranslate.Core.Models;
using PopTranslate.Win32.Guards;
using PopTranslate.Win32.Selection;
using PopTranslate.Win32.Windowing;
using Xunit;

namespace PopTranslate.Tests.Win32;

public sealed class ClipboardAntiPollutionTests
{
    [Fact]
    public void AntiPollutionFormatNames_ContainsAllRequiredWindowsClipboardFormats()
    {
        Assert.Contains("ExcludeClipboardContentFromMonitorProcessing", ClipboardBackupService.AntiPollutionFormatNames);
        Assert.Contains("CanIncludeInClipboardHistory", ClipboardBackupService.AntiPollutionFormatNames);
        Assert.Contains("CanUploadToCloudClipboard", ClipboardBackupService.AntiPollutionFormatNames);
        Assert.Equal(3, ClipboardBackupService.AntiPollutionFormatNames.Count);
    }

    [Fact]
    public void GetAntiPollutionFormatIds_RegistersNonZeroWin32FormatIds()
    {
        var service = new ClipboardBackupService();
        var ids = service.GetAntiPollutionFormatIds();

        Assert.NotEqual(0u, ids.ExcludeFromMonitorProcessing);
        Assert.NotEqual(0u, ids.CanIncludeInClipboardHistory);
        Assert.NotEqual(0u, ids.CanUploadToCloudClipboard);
        Assert.Equal(3, new HashSet<uint>
        {
            ids.ExcludeFromMonitorProcessing,
            ids.CanIncludeInClipboardHistory,
            ids.CanUploadToCloudClipboard,
        }.Count);
    }

    [Theory]
    [InlineData(1u, true)]   // CF_TEXT
    [InlineData(7u, true)]   // CF_OEMTEXT
    [InlineData(8u, true)]   // CF_DIB
    [InlineData(13u, true)]  // CF_UNICODETEXT
    [InlineData(15u, true)]  // CF_HDROP
    [InlineData(16u, true)]  // CF_LOCALE
    [InlineData(17u, true)]  // CF_DIBV5
    [InlineData(0xC123u, true)] // Registered format (e.g. HTML/RTF)
    [InlineData(2u, false)]  // CF_BITMAP (GDI handle)
    [InlineData(3u, false)]  // CF_METAFILEPICT (GDI handle)
    [InlineData(9u, false)]  // CF_PALETTE (GDI handle)
    [InlineData(14u, false)] // CF_ENHMETAFILE (GDI handle)
    [InlineData(0x0080u, false)] // CF_OWNERDISPLAY
    public void IsSafeHGlobalFormat_ClassifiesClipboardFormatsSafely(uint format, bool expected)
    {
        var service = new ClipboardBackupService();
        Assert.Equal(expected, service.IsSafeHGlobalFormat(format));
    }

    [Fact]
    public void CaptureSnapshot_DoesNotThrowAndExcludesAntiPollutionIds()
    {
        var service = new ClipboardBackupService();
        var ids = service.GetAntiPollutionFormatIds();

        Assert.False(service.IsSafeHGlobalFormat(ids.ExcludeFromMonitorProcessing));
        Assert.False(service.IsSafeHGlobalFormat(ids.CanIncludeInClipboardHistory));
        Assert.False(service.IsSafeHGlobalFormat(ids.CanUploadToCloudClipboard));

        var snapshot = service.CaptureSnapshot();
        Assert.NotNull(snapshot);
    }

    [Fact]
    public async Task TextSelectionService_ClipboardFallbackDisabled_NeverCallsClipboardFallback()
    {
        var fakeUia = new FakeUiaReader(result: null);
        var fakeClipboard = new FakeClipboardFallbackReader(result: "should not be used");
        var service = new TextSelectionService(
            fakeUia,
            fakeClipboard,
            new WindowHelperService(),
            new ForegroundAppGuard());

        var config = new SelectionConfig { EnableClipboardFallback = false };
        var result = await service.CaptureSelectionAsync(SelectionTriggerSource.Hotkey, config);

        Assert.False(result.IsSuccess);
        Assert.Equal(SelectionCaptureMethod.None, result.CaptureMethod);
        Assert.Equal(string.Empty, result.Text);
        Assert.Equal(1, fakeUia.CallCount);
        Assert.Equal(0, fakeClipboard.CallCount);
    }

    [Fact]
    public async Task TextSelectionService_UiaSucceeds_SkipsClipboardFallback()
    {
        var fakeUia = new FakeUiaReader(result: "Selected via UIA");
        var fakeClipboard = new FakeClipboardFallbackReader(result: "fallback text");
        var service = new TextSelectionService(
            fakeUia,
            fakeClipboard,
            new WindowHelperService(),
            new ForegroundAppGuard());

        var config = new SelectionConfig { EnableClipboardFallback = true };
        var result = await service.CaptureSelectionAsync(SelectionTriggerSource.Hotkey, config);

        Assert.True(result.IsSuccess);
        Assert.Equal(SelectionCaptureMethod.UiAutomation, result.CaptureMethod);
        Assert.Equal("Selected via UIA", result.Text);
        Assert.Equal(1, fakeUia.CallCount);
        Assert.Equal(0, fakeClipboard.CallCount);
    }

    [Fact]
    public async Task TextSelectionService_UiaEmptyAndFallbackEnabled_UsesClipboardFallbackForExplicitTriggers()
    {
        var fakeUia = new FakeUiaReader(result: null);
        var fakeClipboard = new FakeClipboardFallbackReader(result: "Captured via Ctrl+C");
        var service = new TextSelectionService(
            fakeUia,
            fakeClipboard,
            new WindowHelperService(),
            new ForegroundAppGuard());

        var config = new SelectionConfig { EnableClipboardFallback = true, ClipboardWaitTimeoutMs = 200 };
        var result = await service.CaptureSelectionAsync(SelectionTriggerSource.HoverIcon, config);

        Assert.True(result.IsSuccess);
        Assert.Equal(SelectionCaptureMethod.ClipboardFallback, result.CaptureMethod);
        Assert.Equal("Captured via Ctrl+C", result.Text);
        Assert.Equal(1, fakeUia.CallCount);
        Assert.Equal(1, fakeClipboard.CallCount);
    }

    [Fact]
    public async Task TextSelectionService_BlacklistedProcessAndUiaEmpty_SkipsClipboardFallback()
    {
        var fakeUia = new FakeUiaReader(result: null);
        var fakeClipboard = new FakeClipboardFallbackReader(result: "must never send Ctrl+C");
        var fakeGuard = new FakeForegroundAppGuard("WindowsTerminal.exe");
        var service = new TextSelectionService(
            fakeUia,
            fakeClipboard,
            new WindowHelperService(),
            fakeGuard);

        var config = new SelectionConfig { EnableClipboardFallback = true, ClipboardWaitTimeoutMs = 200 };
        var result = await service.CaptureSelectionAsync(SelectionTriggerSource.Hotkey, config);

        Assert.False(result.IsSuccess);
        Assert.Equal(SelectionCaptureMethod.None, result.CaptureMethod);
        Assert.Equal(string.Empty, result.Text);
        Assert.Equal("WindowsTerminal.exe", result.SourceProcessName);
        Assert.Contains("blacklist", result.FailureReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, fakeUia.CallCount);
        Assert.Equal(0, fakeClipboard.CallCount);
    }

    [Fact]
    public async Task TextSelectionService_BlacklistedProcessWithUiaText_StillReturnsUiaSelection()
    {
        var fakeUia = new FakeUiaReader(result: "Safe UIA terminal text");
        var fakeClipboard = new FakeClipboardFallbackReader(result: "must never send Ctrl+C");
        var fakeGuard = new FakeForegroundAppGuard("WindowsTerminal.exe");
        var service = new TextSelectionService(
            fakeUia,
            fakeClipboard,
            new WindowHelperService(),
            fakeGuard);

        var config = new SelectionConfig { EnableClipboardFallback = true };
        var result = await service.CaptureSelectionAsync(SelectionTriggerSource.Hotkey, config);

        Assert.True(result.IsSuccess);
        Assert.Equal(SelectionCaptureMethod.UiAutomation, result.CaptureMethod);
        Assert.Equal("Safe UIA terminal text", result.Text);
        Assert.Equal(1, fakeUia.CallCount);
        Assert.Equal(0, fakeClipboard.CallCount);
    }

    private sealed class FakeForegroundAppGuard(string processName) : PopTranslate.Core.Contracts.IForegroundAppGuard
    {
        private readonly ForegroundAppGuard _inner = new();

        public bool IsForegroundAppSuppressed(SelectionConfig config, out string? reason, out string? activeProcess)
        {
            activeProcess = processName;
            return _inner.EvaluateSuppression(nint.Zero, processName, config, out reason);
        }

        public bool IsProcessBlacklisted(string? name, IEnumerable<string> blacklist) =>
            _inner.IsProcessBlacklisted(name, blacklist);

        public bool IsFullscreenAppActive(nint foregroundWindow) => false;

        public string? GetProcessNameForWindow(nint windowHandle) => processName;
    }

    private sealed class FakeUiaReader(string? result) : UiAutomationSelectionReader
    {
        public int CallCount { get; private set; }

        public override Task<string?> TryReadSelectionTextAsync(
            nint? targetWindow = null,
            ScreenPoint? probePoint = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeClipboardFallbackReader(string? result) : ClipboardFallbackSelectionReader(new ClipboardBackupService())
    {
        public int CallCount { get; private set; }

        public override Task<string?> TryCaptureViaCtrlCAsync(
            int waitTimeoutMs,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }
}
