using PopTranslate.Core.Models;
using PopTranslate.Win32.Ocr;
using PopTranslate.Win32.Screenshot;
using Xunit;

namespace PopTranslate.Tests.Win32;

public sealed class WindowsOcrProviderTests
{
    private readonly WindowsOcrProvider _provider = new();

    [Fact]
    public void Metadata_MatchesWindowsNativeContract()
    {
        Assert.Equal(OcrProviderKind.WindowsNative, _provider.Kind);
        Assert.Equal("Windows 11 Native OCR", _provider.DisplayName);
    }

    [Fact]
    public async Task GetAvailableLanguagesAsync_ReturnsInstalledWindows11Packs()
    {
        var languages = await _provider.GetAvailableLanguagesAsync();

        Assert.NotEmpty(languages);
        Assert.All(languages, pack =>
        {
            Assert.False(string.IsNullOrWhiteSpace(pack.LanguageTag));
            Assert.False(string.IsNullOrWhiteSpace(pack.DisplayName));
            Assert.False(pack.MappedLanguage.IsAuto);
        });
    }

    [Fact]
    public async Task TestAsync_DefaultConfig_ReportsReady()
    {
        var health = await _provider.TestAsync(new OcrConfig());

        Assert.True(health.IsSuccess, health.Message);
        Assert.False(string.IsNullOrWhiteSpace(health.Message));
    }

    [Theory]
    [InlineData("你 好 ， 世 界 ！", "你好，世界！")]
    [InlineData("Hello World 2026", "Hello World 2026")]
    [InlineData("Windows 11 翻 译 测 试", "Windows 11 翻译测试")]
    [InlineData("こ ん に ち は", "こんにちは")]
    public void CleanOcrLineText_RemovesSpuriousInterCjkSpacesOnly(string raw, string expected)
    {
        Assert.Equal(expected, WindowsOcrProvider.CleanOcrLineText(raw));
    }

    [Fact]
    public async Task RecognizeAsync_EmptyImageBytes_ReturnsCleanFailureWithoutThrowing()
    {
        var result = await _provider.RecognizeAsync(
            new OcrImage([], 0, 0),
            new OcrConfig());

        Assert.False(result.IsSuccess);
        Assert.Equal(OcrProviderKind.WindowsNative, result.ProviderKind);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
    }

    [Fact]
    public async Task RecognizeAsync_PngAndRawBgra32Images_WithUnknownPreferredPack_FallbackCleanly()
    {
        const int width = 120;
        const int height = 40;
        var whiteBgra = new byte[width * height * 4];
        Array.Fill(whiteBgra, (byte)0xFF);

        var configWithMissingPack = new OcrConfig
        {
            PreferredWindowsOcrLanguageTag = "zz-ZZ-nonexistent",
        };

        // 1. Raw BGRA32 path
        var rawResult = await _provider.RecognizeAsync(
            new OcrImage(whiteBgra, width, height, 1.0, MimeType: "raw/bgra32"),
            configWithMissingPack);

        Assert.True(rawResult.IsSuccess, rawResult.ErrorMessage);
        Assert.NotNull(rawResult.DetectedLanguage);

        // 2. Encoded PNG path
        var pngBytes = await ScreenCaptureGdi.EncodeBgra32ToPngAsync(whiteBgra, width, height);
        var pngResult = await _provider.RecognizeAsync(
            new OcrImage(pngBytes, width, height, 1.0, MimeType: "image/png"),
            configWithMissingPack);

        Assert.True(pngResult.IsSuccess, pngResult.ErrorMessage);
        Assert.NotNull(pngResult.DetectedLanguage);
    }
}
