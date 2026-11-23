namespace PopTranslate.Core.Models;

public sealed class PolishConfig
{
    public const string DefaultCustomPrompt =
        "You are an expert editor. Polish the user's text in its original language to improve clarity, fluency, and grammar " +
        "while preserving the exact meaning and formatting. Output ONLY the polished text without explanations or markdown fences.";

    public PolishStyle DefaultStyle { get; set; } = PolishStyle.General;

    public string CustomPrompt { get; set; } = DefaultCustomPrompt;

    public bool ShowDiffByDefault { get; set; }

    public PolishConfig Clone() => new()
    {
        DefaultStyle = DefaultStyle,
        CustomPrompt = CustomPrompt ?? DefaultCustomPrompt,
        ShowDiffByDefault = ShowDiffByDefault,
    };
}
