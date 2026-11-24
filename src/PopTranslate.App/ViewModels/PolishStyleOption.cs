using PopTranslate.Core.Models;

namespace PopTranslate.App.ViewModels;

public sealed record PolishStyleOption(PolishStyle Style, string DisplayLabel)
{
    public override string ToString() => DisplayLabel;

    public static IReadOnlyList<PolishStyleOption> CreatePresets() =>
    [
        new(PolishStyle.General, "通用流畅"),
        new(PolishStyle.Academic, "学术正式"),
        new(PolishStyle.Business, "商务邮件"),
        new(PolishStyle.Casual, "口语自然"),
        new(PolishStyle.Concise, "精简凝练"),
        new(PolishStyle.CodeNaming, "代码命名"),
        new(PolishStyle.Custom, "自定义"),
    ];
}

public sealed record OcrPromptModeOption(OcrPromptMode Mode, string DisplayLabel)
{
    public override string ToString() => DisplayLabel;

    public static IReadOnlyList<OcrPromptModeOption> CreatePresets() =>
    [
        new(OcrPromptMode.PlainText, "纯文本"),
        new(OcrPromptMode.MarkdownTable, "Markdown 表格"),
        new(OcrPromptMode.LatexFormula, "LaTeX 公式"),
    ];
}
