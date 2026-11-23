using PopTranslate.Core.Models;

namespace PopTranslate.Core.Polish;

public static class PolishPromptTemplates
{
    private const string SharedStrictRules =
        "Keep the text in its original language (do NOT translate it). " +
        "Preserve the core meaning, facts, formatting, line breaks, and code snippets. " +
        "Output ONLY the polished text with no preface, explanations, surrounding quotes, or markdown code fences.";

    private const string GeneralPrompt =
        "You are an expert editor. Polish the user's text to make it natural, fluent, clear, and grammatically accurate. " +
        SharedStrictRules;

    private const string AcademicPrompt =
        "You are a scholarly academic editor. Polish the user's text into formal, rigorous, objective academic prose " +
        "with precise terminology and clear logical flow. " +
        SharedStrictRules;

    private const string BusinessPrompt =
        "You are a senior business communications editor. Polish the user's text into a polite, clear, and professional " +
        "business and email communication tone. " +
        SharedStrictRules;

    private const string CasualPrompt =
        "You are a skilled conversational writer. Polish the user's text into a natural, relaxed, warm, and friendly " +
        "casual tone that sounds effortless and authentic. " +
        SharedStrictRules;

    private const string ConcisePrompt =
        "You are a crisp technical editor. Polish the user's text to be concise, direct, and tightly worded, " +
        "eliminating redundancy and filler while keeping every key fact intact. " +
        SharedStrictRules;

    public static string ResolveSystemPrompt(
        PolishStyle style,
        string? customPrompt,
        LanguageCode? preferredLanguage = null)
    {
        if (style == PolishStyle.Custom)
        {
            return string.IsNullOrWhiteSpace(customPrompt)
                ? PolishConfig.DefaultCustomPrompt
                : customPrompt.Trim();
        }

        var basePrompt = style switch
        {
            PolishStyle.General => GeneralPrompt,
            PolishStyle.Academic => AcademicPrompt,
            PolishStyle.Business => BusinessPrompt,
            PolishStyle.Casual => CasualPrompt,
            PolishStyle.Concise => ConcisePrompt,
            _ => GeneralPrompt,
        };

        if (preferredLanguage is { IsAuto: false } lang)
        {
            return $"{basePrompt} Keep the output strictly in {lang.DisplayName}.";
        }

        return basePrompt;
    }
}
