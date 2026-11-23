using System.Text;
using System.Text.RegularExpressions;
using PopTranslate.Core.Contracts;

namespace PopTranslate.Core.Language;

public sealed partial class TextPreprocessor : ITextPreprocessor
{
    public string Preprocess(string rawText, bool mergeLineBreaks, bool isFromOcr = false)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return string.Empty;
        }

        var normalized = rawText.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        if (!mergeLineBreaks)
        {
            return normalized.Trim();
        }

        var lines = normalized.Split('\n');
        var result = new StringBuilder(normalized.Length);
        var pendingParagraphBreak = false;
        var previousWasCode = false;
        var previousWasHeading = false;

        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                if (result.Length > 0)
                {
                    pendingParagraphBreak = true;
                }

                previousWasCode = false;
                previousWasHeading = false;
                continue;
            }

            var isCodeLine = IsCodeIndentedLine(rawLine);
            if (isCodeLine)
            {
                var preservedCodeLine = rawLine.TrimEnd();
                if (result.Length > 0)
                {
                    result.Append(pendingParagraphBreak ? "\n\n" : "\n");
                }

                result.Append(preservedCodeLine);
                pendingParagraphBreak = false;
                previousWasCode = true;
                previousWasHeading = false;
                continue;
            }

            var cleanedLine = CollapseInlineSpaces(rawLine.Trim());
            if (isFromOcr)
            {
                cleanedLine = RemoveSpuriousInterCjkSpaces(cleanedLine);
            }

            var isListOrBullet = IsListOrBulletLine(cleanedLine);
            var isHeading = IsMarkdownHeadingLine(cleanedLine);

            if (result.Length == 0)
            {
                result.Append(cleanedLine);
            }
            else if (pendingParagraphBreak)
            {
                result.Append("\n\n");
                result.Append(cleanedLine);
            }
            else if (isListOrBullet || isHeading || previousWasCode || previousWasHeading)
            {
                result.Append('\n');
                result.Append(cleanedLine);
            }
            else
            {
                AppendMergedLine(result, cleanedLine);
            }

            pendingParagraphBreak = false;
            previousWasCode = false;
            previousWasHeading = isHeading;
        }

        return result.ToString().Trim();
    }

    private static void AppendMergedLine(StringBuilder sb, string nextLine)
    {
        if (sb.Length == 0 || nextLine.Length == 0)
        {
            sb.Append(nextLine);
            return;
        }

        var lastChar = sb[^1];
        var firstNextChar = nextLine[0];

        // De-hyphenate broken Western words across line wraps: "auto-\nmatic" -> "automatic"
        if (lastChar == '-' &&
            sb.Length >= 2 &&
            IsAsciiOrLatinLetter(sb[^2]) &&
            IsLowercaseLatinLetter(firstNextChar))
        {
            sb.Length -= 1;
            sb.Append(nextLine);
            return;
        }

        // CJK line wraps join without spurious spaces.
        if (IsCjkOrCjkPunctuation(lastChar) || IsCjkOrCjkPunctuation(firstNextChar))
        {
            sb.Append(nextLine);
            return;
        }

        sb.Append(' ');
        sb.Append(nextLine);
    }

    private static bool IsCodeIndentedLine(string line) =>
        line.StartsWith('\t') || line.StartsWith("    ", StringComparison.Ordinal);

    private static bool IsListOrBulletLine(string trimmedLine) =>
        trimmedLine.StartsWith("- ", StringComparison.Ordinal) ||
        trimmedLine.StartsWith("* ", StringComparison.Ordinal) ||
        trimmedLine.StartsWith("+ ", StringComparison.Ordinal) ||
        trimmedLine.StartsWith("• ", StringComparison.Ordinal) ||
        trimmedLine.StartsWith("· ", StringComparison.Ordinal) ||
        trimmedLine.StartsWith("▪ ", StringComparison.Ordinal) ||
        NumberedListRegex().IsMatch(trimmedLine);

    private static bool IsMarkdownHeadingLine(string trimmedLine) =>
        MarkdownHeadingRegex().IsMatch(trimmedLine);

    private static string CollapseInlineSpaces(string line) =>
        InlineSpacesRegex().Replace(line, " ");

    private static string RemoveSpuriousInterCjkSpaces(string line)
    {
        if (line.Length < 3 || !line.Contains(' '))
        {
            return line;
        }

        var sb = new StringBuilder(line.Length);
        for (var i = 0; i < line.Length; i++)
        {
            var current = line[i];
            if (current == ' ' &&
                sb.Length > 0 &&
                i + 1 < line.Length &&
                IsCjkOrCjkPunctuation(sb[^1]) &&
                IsCjkOrCjkPunctuation(line[i + 1]))
            {
                continue;
            }

            sb.Append(current);
        }

        return sb.ToString();
    }

    private static bool IsAsciiOrLatinLetter(char ch) =>
        ch is (>= 'A' and <= 'Z')
            or (>= 'a' and <= 'z')
            or (>= '\u00C0' and <= '\u00D6')
            or (>= '\u00D8' and <= '\u00F6')
            or (>= '\u00F8' and <= '\u024F');

    private static bool IsLowercaseLatinLetter(char ch) =>
        ch is (>= 'a' and <= 'z')
            or (>= '\u00DF' and <= '\u00F6')
            or (>= '\u00F8' and <= '\u00FF');

    private static bool IsCjkOrCjkPunctuation(char ch) =>
        ch is (>= '\u4E00' and <= '\u9FFF')
            or (>= '\u3400' and <= '\u4DBF')
            or (>= '\u3000' and <= '\u303F')
            or (>= '\u3040' and <= '\u30FF')
            or (>= '\uFF00' and <= '\uFFEF')
            or (>= '\uAC00' and <= '\uD7AF');

    [GeneratedRegex(@"^(?:\d{1,3}[\.\)]|\(\d{1,3}\)|[a-zA-Z]\))\s+", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedListRegex();

    [GeneratedRegex(@"^#{1,6}\s+", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownHeadingRegex();

    [GeneratedRegex(@"[ \t]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex InlineSpacesRegex();
}
