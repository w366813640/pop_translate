using System.Collections.Frozen;
using System.Text;
using System.Text.RegularExpressions;
using PopTranslate.Core.Contracts;

namespace PopTranslate.Core.Language;

public sealed partial class TextPreprocessor : ITextPreprocessor
{
    private static readonly FrozenSet<string> CompoundHyphenPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "self", "real", "open", "non", "multi", "cross", "well", "built",
        "high", "low", "zero", "one", "two", "read", "thread", "type",
        "user", "full", "auto", "co", "pre", "post", "sub", "inter",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] KebabExcludedPrefixes =
    [
        "self-", "real-", "state-of-the-", "open-", "non-", "multi-", "cross-", "well-",
        "built-", "end-to-", "up-to-", "high-", "low-", "zero-", "one-", "two-",
        "read-", "thread-", "type-", "user-", "full-", "auto-", "co-", "pre-",
        "post-", "sub-", "inter-", "out-of-", "step-by-", "day-to-", "side-by-", "peer-to-",
    ];

    private static readonly FrozenSet<string> KnownBrandTokens = new HashSet<string>(StringComparer.Ordinal)
    {
        "iPhone", "iPad", "iPod", "iCloud", "iOS", "macOS", "tvOS", "watchOS", "visionOS",
        "eBay", "OpenAI", "ChatGPT", "DeepL", "DeepSeek", "GitHub", "GitLab", "YouTube",
        "OneDrive", "SharePoint", "PowerPoint", "PowerToys", "PowerShell", "JavaScript",
        "TypeScript", "WebAssembly", "WinUI", "PopTranslate", "PostgreSQL", "MySQL",
        "SQLite", "MongoDB", "GraphQL", "FastAPI", "PyTorch", "TensorFlow",
    }.ToFrozenSet(StringComparer.Ordinal);

    public string Preprocess(string rawText, bool mergeLineBreaks, bool isFromOcr = false)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return string.Empty;
        }

        var normalized = rawText.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        var trimmed = normalized.Trim();
        if (!trimmed.Contains('\n') && TrySplitCodeIdentifier(trimmed, out var splitIdentifier))
        {
            return splitIdentifier;
        }

        if (!mergeLineBreaks)
        {
            return isFromOcr
                ? CleanOcrMultilinePreservingLineBreaks(normalized)
                : trimmed;
        }

        var lines = normalized.Split('\n');
        var result = new StringBuilder(normalized.Length);
        var pendingParagraphBreak = false;
        var previousWasCode = false;
        var previousWasHeading = false;
        var previousWasStructuredLine = false;
        var inFence = false;
        var inDisplayMath = false;
        var activeFenceChar = '\0';
        var activeFenceLength = 0;

        foreach (var rawLine in lines)
        {
            if (inFence)
            {
                result.Append('\n');
                result.Append(rawLine);

                if (IsClosingFence(rawLine, activeFenceChar, activeFenceLength))
                {
                    inFence = false;
                    activeFenceChar = '\0';
                    activeFenceLength = 0;
                    pendingParagraphBreak = false;
                    previousWasCode = true;
                    previousWasHeading = false;
                    previousWasStructuredLine = false;
                }

                continue;
            }

            if (inDisplayMath)
            {
                result.Append('\n');
                result.Append(rawLine.TrimEnd());
                if (rawLine.AsSpan().Trim().EndsWith("$$", StringComparison.Ordinal))
                {
                    inDisplayMath = false;
                    pendingParagraphBreak = false;
                    previousWasCode = true;
                    previousWasHeading = false;
                    previousWasStructuredLine = false;
                }

                continue;
            }

            if (TryParseOpeningFence(rawLine, out var fenceChar, out var fenceLength))
            {
                if (result.Length > 0)
                {
                    result.Append(pendingParagraphBreak ? "\n\n" : "\n");
                }

                result.Append(rawLine);
                inFence = true;
                activeFenceChar = fenceChar;
                activeFenceLength = fenceLength;
                pendingParagraphBreak = false;
                previousWasCode = true;
                previousWasHeading = false;
                previousWasStructuredLine = false;
                continue;
            }

            var trimmedMathCandidate = rawLine.AsSpan().Trim();
            if (trimmedMathCandidate.StartsWith("$$", StringComparison.Ordinal))
            {
                if (result.Length > 0)
                {
                    result.Append(pendingParagraphBreak ? "\n\n" : "\n");
                }

                result.Append(rawLine.TrimEnd());
                var isSingleLineDisplayMath = trimmedMathCandidate.Length >= 4 &&
                                              trimmedMathCandidate.EndsWith("$$", StringComparison.Ordinal);
                inDisplayMath = !isSingleLineDisplayMath;
                pendingParagraphBreak = false;
                previousWasCode = true;
                previousWasHeading = false;
                previousWasStructuredLine = false;
                continue;
            }

            if (string.IsNullOrWhiteSpace(rawLine))
            {
                if (result.Length > 0)
                {
                    pendingParagraphBreak = true;
                }

                previousWasCode = false;
                previousWasHeading = false;
                previousWasStructuredLine = false;
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
                previousWasStructuredLine = false;
                continue;
            }

            var cleanedLine = CollapseInlineSpaces(rawLine.Trim());
            if (isFromOcr)
            {
                cleanedLine = RemoveSpuriousInterCjkSpaces(cleanedLine);
            }

            var isListOrBullet = IsListOrBulletLine(cleanedLine);
            var isHeading = IsMarkdownHeadingLine(cleanedLine);
            var isTableRow = IsMarkdownTableRow(cleanedLine);

            if (result.Length == 0)
            {
                result.Append(cleanedLine);
            }
            else if (pendingParagraphBreak)
            {
                result.Append("\n\n");
                result.Append(cleanedLine);
            }
            else if (isListOrBullet || isHeading || isTableRow || previousWasCode || previousWasHeading || previousWasStructuredLine)
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
            previousWasStructuredLine = isTableRow;
        }

        return result.ToString().Trim();
    }

    public static bool TrySplitCodeIdentifier(string token, out string splitText)
    {
        splitText = string.Empty;
        if (string.IsNullOrEmpty(token) || token.Length is < 4 or > 120)
        {
            return false;
        }

        if (KnownBrandTokens.Contains(token))
        {
            return false;
        }

        if (token[0] is '-' or >= '0' and <= '9' || token[^1] == '-')
        {
            return false;
        }

        var hasUnderscore = false;
        var hasHyphen = false;
        var hasDigit = false;
        var hasUpper = false;
        var hasLower = false;

        foreach (var ch in token)
        {
            if (ch == '_')
            {
                hasUnderscore = true;
            }
            else if (ch == '-')
            {
                hasHyphen = true;
            }
            else if (ch is >= '0' and <= '9')
            {
                hasDigit = true;
            }
            else if (ch is >= 'A' and <= 'Z')
            {
                hasUpper = true;
            }
            else if (ch is >= 'a' and <= 'z')
            {
                hasLower = true;
            }
            else
            {
                // Contains whitespace, punctuation, slashes, colons, dots, @, or non-ASCII letters.
                return false;
            }
        }

        if (!hasUpper && !hasLower)
        {
            return false;
        }

        if (IsHexOrUuidLike(token, hasHyphen))
        {
            return false;
        }

        if (hasHyphen && !hasUnderscore)
        {
            // Do not split model names with digits (e.g., gpt-4o-mini, utf-8, sha-256),
            // BCP-47 tags (zh-Hans, en-US), or normal English compound words (state-of-the-art).
            if (hasDigit || hasUpper)
            {
                return false;
            }

            foreach (var compoundPrefix in KebabExcludedPrefixes)
            {
                if (token.StartsWith(compoundPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        var parts = new List<string>(8);
        if (hasUnderscore || hasHyphen)
        {
            var rawSegments = token.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries);
            if (rawSegments.Length == 0)
            {
                return false;
            }

            foreach (var segment in rawSegments)
            {
                AppendCamelSplitSegments(segment, parts);
            }
        }
        else
        {
            AppendCamelSplitSegments(token, parts);
        }

        if (parts.Count < 2)
        {
            return false;
        }

        // Guard iPhone / eBay style words (single 'i'/'e' prefix + 1 capitalized word).
        if (!hasUnderscore &&
            !hasHyphen &&
            parts.Count == 2 &&
            parts[0].Length == 1 &&
            parts[0][0] is 'i' or 'e' &&
            parts[1].Length >= 2 &&
            parts[1][0] is >= 'A' and <= 'Z')
        {
            return false;
        }

        splitText = string.Join(' ', parts);
        return true;
    }

    private static void AppendCamelSplitSegments(string segment, List<string> output)
    {
        if (string.IsNullOrEmpty(segment))
        {
            return;
        }

        var start = 0;
        for (var i = 1; i < segment.Length; i++)
        {
            var prev = segment[i - 1];
            var curr = segment[i];

            var lowerToUpper = prev is >= 'a' and <= 'z' && curr is >= 'A' and <= 'Z';
            var acronymToWord = prev is >= 'A' and <= 'Z' &&
                                curr is >= 'A' and <= 'Z' &&
                                i + 1 < segment.Length &&
                                segment[i + 1] is >= 'a' and <= 'z';
            var digitToUpper = prev is >= '0' and <= '9' && curr is >= 'A' and <= 'Z';

            if (lowerToUpper || acronymToWord || digitToUpper)
            {
                output.Add(segment[start..i]);
                start = i;
            }
        }

        output.Add(segment[start..]);
    }

    private static bool IsHexOrUuidLike(string token, bool hasHyphen)
    {
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            for (var i = 2; i < token.Length; i++)
            {
                if (!Uri.IsHexDigit(token[i]))
                {
                    return false;
                }
            }

            return token.Length > 2;
        }

        if (!hasHyphen)
        {
            return false;
        }

        var hexCount = 0;
        foreach (var ch in token)
        {
            if (ch == '-')
            {
                continue;
            }

            if (!Uri.IsHexDigit(ch))
            {
                return false;
            }

            hexCount++;
        }

        return hexCount >= 8;
    }

    private static string CleanOcrMultilinePreservingLineBreaks(string normalized)
    {
        var lines = normalized.Split('\n');
        var sb = new StringBuilder(normalized.Length);
        var inFence = false;
        var activeFenceChar = '\0';
        var activeFenceLength = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                sb.Append('\n');
            }

            var line = lines[i];
            if (inFence)
            {
                sb.Append(line);
                if (IsClosingFence(line, activeFenceChar, activeFenceLength))
                {
                    inFence = false;
                    activeFenceChar = '\0';
                    activeFenceLength = 0;
                }

                continue;
            }

            if (TryParseOpeningFence(line, out var fenceChar, out var fenceLen))
            {
                inFence = true;
                activeFenceChar = fenceChar;
                activeFenceLength = fenceLen;
                sb.Append(line);
                continue;
            }

            sb.Append(RemoveSpuriousInterCjkSpaces(line));
        }

        return sb.ToString().Trim();
    }

    private static bool TryParseOpeningFence(string rawLine, out char fenceChar, out int fenceLength)
    {
        fenceChar = '\0';
        fenceLength = 0;

        var trimmed = rawLine.AsSpan().TrimStart(" \t");
        if (trimmed.Length < 3)
        {
            return false;
        }

        var ch = trimmed[0];
        if (ch is not ('`' or '~'))
        {
            return false;
        }

        var run = 0;
        while (run < trimmed.Length && trimmed[run] == ch)
        {
            run++;
        }

        if (run < 3)
        {
            return false;
        }

        // Backtick fenced code blocks cannot contain backticks in their info string (avoids inline ```code```).
        if (ch == '`' && trimmed[run..].Contains('`'))
        {
            return false;
        }

        fenceChar = ch;
        fenceLength = run;
        return true;
    }

    private static bool IsClosingFence(string rawLine, char expectedFenceChar, int minFenceLength)
    {
        var trimmed = rawLine.AsSpan().TrimStart(" \t");
        if (trimmed.Length < minFenceLength || trimmed[0] != expectedFenceChar)
        {
            return false;
        }

        var run = 0;
        while (run < trimmed.Length && trimmed[run] == expectedFenceChar)
        {
            run++;
        }

        return run >= minFenceLength && trimmed[run..].IsWhiteSpace();
    }

    private static void AppendMergedLine(StringBuilder sb, string nextLine)
    {
        if (sb.Length == 0 || nextLine.Length == 0)
        {
            sb.Append(nextLine);
            return;
        }

        if (EndsWithWrappedUrl(sb, nextLine))
        {
            sb.Append(nextLine);
            return;
        }

        var lastChar = sb[^1];
        var firstNextChar = nextLine[0];

        // De-hyphenate broken Western words across line wraps ("auto-\nmatic" -> "automatic"),
        // while preserving true compound prefixes ("state-of-the-\nart" -> "state-of-the-art").
        if (lastChar == '-' &&
            sb.Length >= 2 &&
            IsAsciiOrLatinLetter(sb[^2]) &&
            IsLowercaseLatinLetter(firstNextChar))
        {
            if (!ShouldPreserveCompoundHyphen(sb, nextLine))
            {
                sb.Length -= 1;
            }

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

    private static bool EndsWithWrappedUrl(StringBuilder sb, string nextLine)
    {
        if (sb[^1] is '.' or ',' or ';' or ')' or ']' &&
            nextLine.Length > 1 &&
            char.IsUpper(nextLine[0]) &&
            nextLine.Contains(' '))
        {
            return false;
        }

        var tokenStart = sb.Length;
        while (tokenStart > 0 && !char.IsWhiteSpace(sb[tokenStart - 1]))
        {
            tokenStart--;
        }

        var tokenLength = sb.Length - tokenStart;
        if (tokenLength < 7)
        {
            return false;
        }

        Span<char> prefixBuffer = stackalloc char[8];
        var copyLen = Math.Min(8, tokenLength);
        sb.CopyTo(tokenStart, prefixBuffer, copyLen);
        var prefix = prefixBuffer[..copyLen];

        return prefix.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               prefix.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldPreserveCompoundHyphen(StringBuilder sb, string nextLine)
    {
        // sb[^1] is '-'. Extract the preceding word/compound token before '-'.
        var end = sb.Length - 1;
        var start = end;
        var hasEarlierHyphen = false;

        while (start > 0)
        {
            var ch = sb[start - 1];
            if (ch == '-')
            {
                hasEarlierHyphen = true;
                start--;
            }
            else if (IsAsciiOrLatinLetter(ch))
            {
                start--;
            }
            else
            {
                break;
            }
        }

        if (hasEarlierHyphen)
        {
            return true;
        }

        var len = end - start;
        if (len is < 2 or > 16)
        {
            return false;
        }

        Span<char> buf = stackalloc char[16];
        sb.CopyTo(start, buf, len);
        var prefix = new string(buf[..len]);

        if (!CompoundHyphenPrefixes.Contains(prefix))
        {
            return false;
        }

        if (prefix.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
            IsSingleWordAutoContinuation(nextLine))
        {
            return false;
        }

        return true;
    }

    private static bool IsSingleWordAutoContinuation(string nextLine) =>
        nextLine.StartsWith("mat", StringComparison.OrdinalIgnoreCase) ||
        nextLine.StartsWith("mob", StringComparison.OrdinalIgnoreCase) ||
        nextLine.StartsWith("nom", StringComparison.OrdinalIgnoreCase) ||
        nextLine.StartsWith("graph", StringComparison.OrdinalIgnoreCase) ||
        nextLine.StartsWith("biograph", StringComparison.OrdinalIgnoreCase);

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

    private static bool IsMarkdownTableRow(string trimmedLine) =>
        trimmedLine.Length >= 3 &&
        trimmedLine[0] == '|' &&
        trimmedLine[^1] == '|';

    private static string CollapseInlineSpaces(string line) =>
        InlineSpacesRegex().Replace(line, " ");

    public static string RemoveSpuriousInterCjkSpaces(string line)
    {
        if (line.Length < 3 || (!line.Contains(' ') && !line.Contains('\t')))
        {
            return line;
        }

        var sb = new StringBuilder(line.Length);
        for (var i = 0; i < line.Length; i++)
        {
            var current = line[i];
            if (current is ' ' or '\t' && sb.Length > 0 && IsCjkOrCjkPunctuation(sb[^1]))
            {
                var nextNonSpace = i + 1;
                while (nextNonSpace < line.Length && line[nextNonSpace] is ' ' or '\t')
                {
                    nextNonSpace++;
                }

                if (nextNonSpace < line.Length && IsCjkOrCjkPunctuation(line[nextNonSpace]))
                {
                    i = nextNonSpace - 1;
                    continue;
                }
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
        !char.IsWhiteSpace(ch) &&
        (ch is (>= '\u4E00' and <= '\u9FFF')
            or (>= '\u3400' and <= '\u4DBF')
            or (>= '\uF900' and <= '\uFAFF')
            or (>= '\u3000' and <= '\u303F')
            or (>= '\u3040' and <= '\u30FF')
            or (>= '\u31F0' and <= '\u31FF')
            or (>= '\uFF00' and <= '\uFFEF')
            or (>= '\uAC00' and <= '\uD7AF')
            or (>= '\u2000' and <= '\u206F')
            or '\u00B7');

    [GeneratedRegex(@"^(?:\d{1,3}[\.\)]|\(\d{1,3}\)|[a-zA-Z]\))\s+", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedListRegex();

    [GeneratedRegex(@"^#{1,6}\s+", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownHeadingRegex();

    [GeneratedRegex(@"[ \t]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex InlineSpacesRegex();
}
