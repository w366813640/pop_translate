using PopTranslate.Core.Language;
using PopTranslate.Core.Models;
using Xunit;

namespace PopTranslate.Tests.Core;

public sealed class LocalLanguageDetectorTests
{
    private readonly LocalLanguageDetector _detector = new();

    [Theory]
    [InlineData("你好，世界。这是一个翻译测试。", "zh-Hans")]
    [InlineData("這是一個繁體中文的翻譯測試，用來驗證語言偵測功能。", "zh-Hant")]
    [InlineData("Hello world, this is a translation test.", "en")]
    [InlineData("こんにちは、これは翻訳テストです。", "ja")]
    [InlineData("안녕하세요, 번역 테스트입니다.", "ko")]
    [InlineData("Привет мир, это тест перевода.", "ru")]
    [InlineData("مرحبا بالعالم، هذا اختبار للترجمة.", "ar")]
    [InlineData("Bonjour le monde, ceci est un test de traduction.", "fr")]
    [InlineData("Hallo Welt, das ist ein Übersetzungstest für Windows.", "de")]
    [InlineData("Hola mundo, esta es una prueba de traducción para todos.", "es")]
    [InlineData("Ciao mondo, questo è un test di traduzione per tutti.", "it")]
    [InlineData("Olá mundo, este é um teste de tradução para você.", "pt")]
    public void Detect_IdentifiesExpectedLanguage(string text, string expectedCode)
    {
        var result = _detector.Detect(text);
        Assert.Equal(LanguageCode.Parse(expectedCode), result.Language);
        Assert.True(result.IsReliable);
        Assert.InRange(result.Confidence, 0.5, 1.0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("   12345 !@# ")]
    public void Detect_EmptyOrSymbols_ReturnsUnreliableAuto(string text)
    {
        var result = _detector.Detect(text);
        Assert.Equal(LanguageCode.Auto, result.Language);
        Assert.False(result.IsReliable);
        Assert.Equal(0.0, result.Confidence);
    }

    [Fact]
    public void Detect_MixedChineseAndEnglishTechnicalText_DetectsChinese()
    {
        var result = _detector.Detect("在 Windows 11 中使用 WinUI 3 开发桌面翻译工具。");
        Assert.Equal(LanguageCode.ChineseSimplified, result.Language);
        Assert.True(result.IsReliable);
    }

    [Fact]
    public void Detect_JapaneseKanjiWithKana_DetectsJapaneseNotChinese()
    {
        var result = _detector.Detect("日本語の文章を翻訳する機能です。");
        Assert.Equal(LanguageCode.Japanese, result.Language);
        Assert.True(result.IsReliable);
    }
}
