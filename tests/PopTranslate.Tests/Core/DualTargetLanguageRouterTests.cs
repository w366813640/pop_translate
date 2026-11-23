using PopTranslate.Core.Language;
using PopTranslate.Core.Models;
using Xunit;

namespace PopTranslate.Tests.Core;

public sealed class DualTargetLanguageRouterTests
{
    private readonly DualTargetLanguageRouter _router = new(new LocalLanguageDetector());

    [Fact]
    public void Resolve_SimplifiedChineseText_RoutesToSecondaryEnglish()
    {
        var decision = _router.Resolve(
            "今天天气不错",
            LanguageCode.Auto,
            LanguageCode.Auto,
            LanguageCode.ChineseSimplified,
            LanguageCode.English);

        Assert.Equal(LanguageCode.ChineseSimplified, decision.DetectedSourceLanguage);
        Assert.Equal(LanguageCode.English, decision.TargetLanguage);
        Assert.False(decision.UsedManualOverride);
    }

    [Fact]
    public void Resolve_TraditionalChineseText_WhenPrimaryChineseAndSecondaryEnglish_RoutesToEnglish()
    {
        var decision = _router.Resolve(
            "這是一個繁體中文測試",
            LanguageCode.Auto,
            LanguageCode.Auto,
            LanguageCode.ChineseSimplified,
            LanguageCode.English);

        Assert.Equal(LanguageCode.ChineseTraditional, decision.DetectedSourceLanguage);
        Assert.Equal(LanguageCode.English, decision.TargetLanguage);
        Assert.False(decision.UsedManualOverride);
    }

    [Fact]
    public void Resolve_EnglishText_RoutesToPrimaryChinese()
    {
        var decision = _router.Resolve(
            "Windows 11 desktop translation",
            LanguageCode.Auto,
            LanguageCode.Auto,
            LanguageCode.ChineseSimplified,
            LanguageCode.English);

        Assert.Equal(LanguageCode.English, decision.DetectedSourceLanguage);
        Assert.Equal(LanguageCode.ChineseSimplified, decision.TargetLanguage);
        Assert.False(decision.UsedManualOverride);
    }

    [Fact]
    public void Resolve_JapaneseText_RoutesToPrimaryChinese()
    {
        var decision = _router.Resolve(
            "ありがとうございます",
            LanguageCode.Auto,
            LanguageCode.Auto,
            LanguageCode.ChineseSimplified,
            LanguageCode.English);

        Assert.Equal(LanguageCode.Japanese, decision.DetectedSourceLanguage);
        Assert.Equal(LanguageCode.ChineseSimplified, decision.TargetLanguage);
        Assert.False(decision.UsedManualOverride);
    }

    [Fact]
    public void Resolve_ManualTargetOverride_IsRespectedAndStillDetectsSource()
    {
        var decision = _router.Resolve(
            "Hello world",
            LanguageCode.Auto,
            LanguageCode.Japanese,
            LanguageCode.ChineseSimplified,
            LanguageCode.English);

        Assert.Equal(LanguageCode.English, decision.DetectedSourceLanguage);
        Assert.Equal(LanguageCode.Japanese, decision.TargetLanguage);
        Assert.True(decision.UsedManualOverride);
    }

    [Fact]
    public void Resolve_ExplicitSourceMatchingPrimary_RoutesToSecondary()
    {
        var decision = _router.Resolve(
            "12345",
            LanguageCode.ChineseSimplified,
            LanguageCode.Auto,
            LanguageCode.ChineseSimplified,
            LanguageCode.English);

        Assert.Equal(LanguageCode.ChineseSimplified, decision.DetectedSourceLanguage);
        Assert.Equal(LanguageCode.English, decision.TargetLanguage);
        Assert.False(decision.UsedManualOverride);
    }

    [Fact]
    public void Resolve_PrimarySimplifiedSecondaryTraditional_RoutesTraditionalToSimplified()
    {
        var decision = _router.Resolve(
            "這是一個繁體中文測試",
            LanguageCode.Auto,
            LanguageCode.Auto,
            LanguageCode.ChineseSimplified,
            LanguageCode.ChineseTraditional);

        Assert.Equal(LanguageCode.ChineseTraditional, decision.DetectedSourceLanguage);
        Assert.Equal(LanguageCode.ChineseSimplified, decision.TargetLanguage);
        Assert.False(decision.UsedManualOverride);
    }
}
