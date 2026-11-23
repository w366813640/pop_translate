using System.Text.Json;
using PopTranslate.Core.DependencyInjection;
using PopTranslate.Core.Models;
using PopTranslate.Win32.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace PopTranslate.Tests.Contracts;

public sealed class ContractsSmokeTests
{
    [Fact]
    public void AppSettings_CreateDefault_ProvidesZeroConfigWorkingDefaults()
    {
        var settings = AppSettings.CreateDefault();

        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal(LanguageCode.ChineseSimplified, settings.PrimaryTargetLanguage);
        Assert.Equal(LanguageCode.English, settings.SecondaryTargetLanguage);
        Assert.True(settings.MergePdfLineBreaks);
        Assert.Equal(OcrProviderKind.WindowsNative, settings.Ocr.ActiveProvider);
        Assert.Equal(ProxyMode.System, settings.Proxy.Mode);
        Assert.Equal(WindowPositionMode.FollowCursor, settings.Window.PositionMode);
        Assert.True(settings.Selection.EnableHoverIcon);
        Assert.True(settings.Selection.QuietModeInFullscreen);
        Assert.Contains("WindowsTerminal.exe", settings.Selection.ProcessBlacklist);

        var enabledProviders = settings.Providers.Where(p => p.Enabled).ToList();
        Assert.Single(enabledProviders);
        Assert.Equal(ProviderKind.FreeTraditional, enabledProviders[0].Kind);
        Assert.Contains(settings.Providers, p => p.Kind == ProviderKind.OpenAiCompatible);
        Assert.Contains(settings.Providers, p => p.Kind == ProviderKind.DeepL);
    }

    [Fact]
    public void AppSettings_JsonRoundTrip_PreservesLanguageCodesAndConfigs()
    {
        var original = AppSettings.CreateDefault();
        original.Window.PositionMode = WindowPositionMode.RememberFixedPosition;
        original.Window.FixedX = 320;
        original.Window.FixedY = 180;

        var json = JsonSerializer.Serialize(original);
        Assert.Contains("\"zh-Hans\"", json);
        Assert.Contains("\"en\"", json);

        var restored = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(restored);
        Assert.Equal(original.PrimaryTargetLanguage, restored.PrimaryTargetLanguage);
        Assert.Equal(original.SecondaryTargetLanguage, restored.SecondaryTargetLanguage);
        Assert.Equal(original.Providers.Count, restored.Providers.Count);
        Assert.Equal(320, restored.Window.FixedX);
        Assert.Equal(180, restored.Window.FixedY);
    }

    [Theory]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh_hans_cn", "zh-Hans")]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("en-US", "en")]
    [InlineData("ja-JP", "ja")]
    [InlineData("", "auto")]
    [InlineData(null, "auto")]
    public void LanguageCode_Parse_NormalizesBcp47Variants(string? raw, string expected)
    {
        var code = LanguageCode.Parse(raw);
        Assert.Equal(expected, code.Value);
    }

    [Fact]
    public void HotkeyGesture_DisplayText_FormatsModifiersAndKey()
    {
        var gesture = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x44);
        Assert.Equal("Ctrl + Alt + D", gesture.DisplayText);
        Assert.False(gesture.IsEmpty);
        Assert.True(HotkeyGesture.Empty.IsEmpty);
    }

    [Fact]
    public void ScreenGeometry_AndCapturedScreenshot_ConvertCleanlyToOcrImage()
    {
        var rect = new ScreenRect(100, 200, 300, 150);
        Assert.Equal(400, rect.Right);
        Assert.Equal(350, rect.Bottom);
        Assert.True(rect.Contains(new ScreenPoint(150, 250)));
        Assert.False(rect.Contains(new ScreenPoint(400, 350)));

        var monitor = new MonitorInfo(1, @"\\.\DISPLAY1", rect, rect, 144, 144, true);
        Assert.Equal(1.5, monitor.DpiScale);

        var shot = new CapturedScreenshot([0x89, 0x50, 0x4E, 0x47], rect, 1.5, 300, 150);
        var ocrImage = shot.ToOcrImage(LanguageCode.English);
        Assert.Equal("image/png", ocrImage.MimeType);
        Assert.Equal(300, ocrImage.PixelWidth);
        Assert.Equal(150, ocrImage.PixelHeight);
        Assert.Equal(LanguageCode.English, ocrImage.PreferredLanguage);
    }

    [Fact]
    public void DependencyInjection_StubExtensions_ExecuteCleanly()
    {
        var services = new ServiceCollection();
        services.AddPopTranslateCore().AddPopTranslateWin32();
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider);
    }
}
