using PopTranslate.Core.Models;

namespace PopTranslate.Core.Translation;

/// <summary>
/// Classifies whether an input string is a single word, short 2–3 word English idiom,
/// or 1–4 character CJK/Japanese/Korean term suitable for structured dictionary lookup.
/// </summary>
public static class WordLookupClassifier
{
    private static readonly HashSet<string> ShortIdiomConnectors = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "of", "in", "on", "at", "to", "up", "by", "for", "out", "off", "as", "or", "and",
    };

    public static bool IsDictionaryLookupCandidate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.Length is < 1 or > 40)
        {
            return false;
        }

        if (trimmed.Contains('\n', StringComparison.Ordinal) ||
            trimmed.Contains('\r', StringComparison.Ordinal) ||
            trimmed.Contains('\t', StringComparison.Ordinal) ||
            trimmed.Contains("://", StringComparison.Ordinal) ||
            trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var ch in trimmed)
        {
            if (char.IsAsciiDigit(ch) || IsDisallowedPunctuationOrSymbol(ch))
            {
                return false;
            }
        }

        // Case 1: 1–4 character CJK / Japanese Kana / Korean Hangul word without spaces.
        if (!trimmed.Contains(' ', StringComparison.Ordinal) && trimmed.Length <= 4)
        {
            var allEastAsianWordChars = true;
            foreach (var ch in trimmed)
            {
                if (!IsEastAsianWordChar(ch))
                {
                    allEastAsianWordChars = false;
                    break;
                }
            }

            if (allEastAsianWordChars)
            {
                return true;
            }
        }

        // Case 2: 1–2 Latin words, or a 3-word short idiom joined by a preposition/article.
        var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 1 or > 3)
        {
            return false;
        }

        if (words.Length == 3 && !ShortIdiomConnectors.Contains(words[1]))
        {
            return false;
        }

        foreach (var word in words)
        {
            if (!IsValidLatinLookupWord(word))
            {
                return false;
            }
        }

        return true;
    }

    public static string BuildDictionarySystemPrompt(LanguageCode sourceLanguage, LanguageCode targetLanguage)
    {
        var sourceName = sourceLanguage.IsAuto
            ? "the detected source language"
            : sourceLanguage.DisplayName;
        var targetName = targetLanguage.IsAuto
            ? LanguageCode.ChineseSimplified.DisplayName
            : targetLanguage.DisplayName;

        return
            $"You are a concise, authoritative bilingual dictionary ({sourceName} -> {targetName}). " +
            $"Provide a crisp structured dictionary entry in {targetName} for the headword or phrase using this exact layout:\n" +
            "1. **Phonetics**: UK /.../ | US /.../ (or reading/pinyin for CJK/Japanese/Korean terms)\n" +
            $"2. **POS & Meanings**: Part of speech (e.g., n., v., adj., idiom) + concise, natural definitions in {targetName}\n" +
            "3. **Inflections**: Key word forms (plural, past, past participle, present participle, comparative; omit line if invariant)\n" +
            $"4. **Examples**: 2 short, idiomatic bilingual example sentences ({sourceName} with {targetName} translation)\n" +
            "Output ONLY the dictionary entry without extra commentary or markdown code fences.";
    }

    private static bool IsValidLatinLookupWord(string word)
    {
        if (word.Length is < 1 or > 28)
        {
            return false;
        }

        if (!IsLatinLetter(word[0]) || !IsLatinLetter(word[^1]))
        {
            return false;
        }

        for (var i = 0; i < word.Length; i++)
        {
            var ch = word[i];
            if (IsLatinLetter(ch))
            {
                // Reject unsplit camelCase code identifiers (e.g. "getWindow") from dictionary lookup.
                if (i > 0 && char.IsLower(word[i - 1]) && char.IsUpper(ch))
                {
                    return false;
                }

                continue;
            }

            if (ch is '-' or '\'' or '’')
            {
                if (i == 0 || i == word.Length - 1 || !IsLatinLetter(word[i - 1]) || !IsLatinLetter(word[i + 1]))
                {
                    return false;
                }

                continue;
            }

            return false;
        }

        return true;
    }

    private static bool IsLatinLetter(char ch) =>
        ch is (>= 'A' and <= 'Z')
            or (>= 'a' and <= 'z')
            or (>= '\u00C0' and <= '\u00D6')
            or (>= '\u00D8' and <= '\u00F6')
            or (>= '\u00F8' and <= '\u024F');

    private static bool IsEastAsianWordChar(char ch) =>
        ch is (>= '\u4E00' and <= '\u9FFF')
            or (>= '\u3400' and <= '\u4DBF')
            or (>= '\uF900' and <= '\uFAFF')
            or (>= '\u3040' and <= '\u309F')
            or (>= '\u30A0' and <= '\u30FF')
            or '\u3005'
            or (>= '\uAC00' and <= '\uD7AF');

    private static bool IsDisallowedPunctuationOrSymbol(char ch) =>
        ch is '.' or ',' or '!' or '?' or ';' or ':' or '"' or '(' or ')' or '[' or ']' or '{' or '}'
            or '<' or '>' or '/' or '\\' or '|' or '@' or '#' or '$' or '%' or '^' or '&' or '*'
            or '=' or '+' or '~' or '`' or '_'
            or '。' or '，' or '！' or '？' or '；' or '：' or '、' or '“' or '”' or '‘' or '’'
            or '（' or '）' or '【' or '】' or '《' or '》' or '—' or '…' or '·';
}
