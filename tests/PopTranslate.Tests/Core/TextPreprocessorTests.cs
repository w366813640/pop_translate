using PopTranslate.Core.Language;
using Xunit;

namespace PopTranslate.Tests.Core;

public sealed class TextPreprocessorTests
{
    private readonly TextPreprocessor _preprocessor = new();

    [Fact]
    public void Preprocess_DeHyphenatesEnglishPdfWrap()
    {
        var output = _preprocessor.Preprocess("This is an auto-\nmatic translation\ntest.", mergeLineBreaks: true);
        Assert.Equal("This is an automatic translation test.", output);
    }

    [Fact]
    public void Preprocess_JoinsCjkLinesWithoutExtraSpace()
    {
        var output = _preprocessor.Preprocess("这是一段从PDF\n复制出来的中文文本", mergeLineBreaks: true);
        Assert.Equal("这是一段从PDF复制出来的中文文本", output);
    }

    [Fact]
    public void Preprocess_PreservesBlankLineParagraphsAndBullets()
    {
        var input = "Paragraph one line 1\nline 2.\n\n- Bullet one\n- Bullet two";
        var output = _preprocessor.Preprocess(input, mergeLineBreaks: true);
        Assert.Equal("Paragraph one line 1 line 2.\n\n- Bullet one\n- Bullet two", output);
    }

    [Fact]
    public void Preprocess_PreservesNumberedListsAndCodeIndentation()
    {
        var input = "1. First item\n2. Second item\n\n    var x = 1;\n    var y = 2;";
        var output = _preprocessor.Preprocess(input, mergeLineBreaks: true);
        Assert.Equal("1. First item\n2. Second item\n\n    var x = 1;\n    var y = 2;", output);
    }

    [Fact]
    public void Preprocess_Disabled_KeepsOriginalLineBreaksWithNormalizedLf()
    {
        var output = _preprocessor.Preprocess("Line 1\r\nLine 2\rLine 3", mergeLineBreaks: false);
        Assert.Equal("Line 1\nLine 2\nLine 3", output);
    }

    [Fact]
    public void Preprocess_OcrMode_RemovesSpuriousInterCjkSpacesAndMergesLines()
    {
        var output = _preprocessor.Preprocess("你 好 世 界\n欢 迎 使 用 PopTranslate", mergeLineBreaks: true, isFromOcr: true);
        Assert.Equal("你好世界欢迎使用 PopTranslate", output);
    }

    [Fact]
    public void Preprocess_EmptyOrWhitespace_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, _preprocessor.Preprocess("   \r\n\t  ", mergeLineBreaks: true));
        Assert.Equal(string.Empty, _preprocessor.Preprocess(string.Empty, mergeLineBreaks: false));
    }
}
