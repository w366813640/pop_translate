using System.Text;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;

namespace PopTranslate.Core.Diff;

public sealed class LocalTextDiffService : ITextDiffService
{
    public const int MaxEditDistance = 4096;

    public IReadOnlyList<DiffSegment> ComputeDiff(string originalText, string revisedText)
    {
        var original = originalText ?? string.Empty;
        var revised = revisedText ?? string.Empty;

        if (original.Length == 0 && revised.Length == 0)
        {
            return Array.Empty<DiffSegment>();
        }

        if (original.Length == 0)
        {
            return [new DiffSegment(DiffOperation.Insert, revised)];
        }

        if (revised.Length == 0)
        {
            return [new DiffSegment(DiffOperation.Delete, original)];
        }

        if (string.Equals(original, revised, StringComparison.Ordinal))
        {
            return [new DiffSegment(DiffOperation.Equal, original)];
        }

        var origTokens = Tokenize(original);
        var revTokens = Tokenize(revised);

        var bufferSize = 2 * ((MaxEditDistance + 1) / 2) + 5;
        var vf = new int[bufferSize];
        var vb = new int[bufferSize];
        var rawOps = new List<(DiffOperation Operation, string Text)>(origTokens.Count + revTokens.Count);

        DiffRange(
            origTokens,
            0,
            origTokens.Count,
            revTokens,
            0,
            revTokens.Count,
            MaxEditDistance,
            allowLineFallback: true,
            vf,
            vb,
            rawOps);

        return CoalesceAndNormalize(rawOps);
    }

    public static IReadOnlyList<string> Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<string>();
        }

        var tokens = new List<string>(Math.Min(text.Length, 256));
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            // Discrete newline tokens (\r\n, \n, \r)
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    tokens.Add("\r\n");
                    i += 2;
                }
                else
                {
                    tokens.Add("\r");
                    i++;
                }

                continue;
            }

            if (c == '\n')
            {
                tokens.Add("\n");
                i++;
                continue;
            }

            // Contiguous horizontal whitespace run
            if (char.IsWhiteSpace(c))
            {
                var start = i;
                i++;
                while (i < text.Length && char.IsWhiteSpace(text[i]) && text[i] != '\r' && text[i] != '\n')
                {
                    i++;
                }

                tokens.Add(text[start..i]);
                continue;
            }

            // Preserve UTF-16 surrogate pairs (emojis, CJK Extension B-G) as atomic tokens
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                tokens.Add(text.Substring(i, 2));
                i += 2;
                continue;
            }

            // Individual CJK ideographs / Kana / Hangul
            if (IsCjk(c))
            {
                tokens.Add(text.Substring(i, 1));
                i++;
                continue;
            }

            // Contiguous Western words / digits
            if (IsWesternWordChar(c))
            {
                var start = i;
                i++;
                while (i < text.Length && IsWesternWordChar(text[i]))
                {
                    i++;
                }

                tokens.Add(text[start..i]);
                continue;
            }

            // Punctuation and symbols (individual character tokens)
            tokens.Add(text.Substring(i, 1));
            i++;
        }

        return tokens;
    }

    private static bool IsWesternWordChar(char c) =>
        char.IsLetterOrDigit(c) && !IsCjk(c) && !char.IsSurrogate(c);

    private static bool IsCjk(char c) =>
        (c >= '\u4E00' && c <= '\u9FFF') || // CJK Unified Ideographs
        (c >= '\u3400' && c <= '\u4DBF') || // CJK Extension A
        (c >= '\uF900' && c <= '\uFAFF') || // CJK Compatibility Ideographs
        (c >= '\u3040' && c <= '\u309F') || // Hiragana
        (c >= '\u30A0' && c <= '\u30FF') || // Katakana
        (c >= '\u31F0' && c <= '\u31FF') || // Katakana Phonetic Extensions
        (c >= '\uAC00' && c <= '\uD7AF') || // Hangul Syllables
        (c >= '\u1100' && c <= '\u11FF') || // Hangul Jamo
        (c >= '\u3130' && c <= '\u318F');   // Hangul Compatibility Jamo

    private static void DiffRange(
        IReadOnlyList<string> orig,
        int a0,
        int n,
        IReadOnlyList<string> rev,
        int b0,
        int m,
        int maxEditDistance,
        bool allowLineFallback,
        int[] vf,
        int[] vb,
        List<(DiffOperation Operation, string Text)> output)
    {
        // Trim common prefix in O(N)
        var prefixLen = 0;
        var minLen = Math.Min(n, m);
        while (prefixLen < minLen && string.Equals(orig[a0 + prefixLen], rev[b0 + prefixLen], StringComparison.Ordinal))
        {
            prefixLen++;
        }

        for (var i = 0; i < prefixLen; i++)
        {
            output.Add((DiffOperation.Equal, orig[a0 + i]));
        }

        var midA = a0 + prefixLen;
        var midB = b0 + prefixLen;
        var midN = n - prefixLen;
        var midM = m - prefixLen;

        // Trim common suffix in O(N)
        var suffixLen = 0;
        var remainingMin = Math.Min(midN, midM);
        while (suffixLen < remainingMin &&
               string.Equals(orig[midA + midN - 1 - suffixLen], rev[midB + midM - 1 - suffixLen], StringComparison.Ordinal))
        {
            suffixLen++;
        }

        var coreN = midN - suffixLen;
        var coreM = midM - suffixLen;

        if (coreN == 0 && coreM > 0)
        {
            for (var j = 0; j < coreM; j++)
            {
                output.Add((DiffOperation.Insert, rev[midB + j]));
            }
        }
        else if (coreM == 0 && coreN > 0)
        {
            for (var i = 0; i < coreN; i++)
            {
                output.Add((DiffOperation.Delete, orig[midA + i]));
            }
        }
        else if (coreN > 0 && coreM > 0)
        {
            if (TryFindMiddleSnake(orig, midA, coreN, rev, midB, coreM, maxEditDistance, vf, vb, out var snake))
            {
                if (snake.LeftN > 0 || snake.LeftM > 0)
                {
                    DiffRange(orig, midA, snake.LeftN, rev, midB, snake.LeftM, maxEditDistance, allowLineFallback, vf, vb, output);
                }

                if (snake.IsForward)
                {
                    EmitSingleEdit(orig, rev, snake, output);
                    EmitSnake(orig, snake, output);
                }
                else
                {
                    EmitSnake(orig, snake, output);
                    EmitSingleEdit(orig, rev, snake, output);
                }

                if (snake.RightN > 0 || snake.RightM > 0)
                {
                    DiffRange(orig, snake.RightA, snake.RightN, rev, snake.RightB, snake.RightM, maxEditDistance, allowLineFallback, vf, vb, output);
                }
            }
            else if (allowLineFallback)
            {
                var origMidText = ConcatSlice(orig, midA, coreN);
                var revMidText = ConcatSlice(rev, midB, coreM);
                var origLines = SplitKeepingNewlines(origMidText);
                var revLines = SplitKeepingNewlines(revMidText);

                if (origLines.Count > 1 || revLines.Count > 1)
                {
                    DiffRange(origLines, 0, origLines.Count, revLines, 0, revLines.Count, maxEditDistance, allowLineFallback: false, vf, vb, output);
                }
                else
                {
                    output.Add((DiffOperation.Delete, origMidText));
                    output.Add((DiffOperation.Insert, revMidText));
                }
            }
            else
            {
                output.Add((DiffOperation.Delete, ConcatSlice(orig, midA, coreN)));
                output.Add((DiffOperation.Insert, ConcatSlice(rev, midB, coreM)));
            }
        }

        // Emit common suffix
        for (var i = 0; i < suffixLen; i++)
        {
            output.Add((DiffOperation.Equal, orig[midA + coreN + i]));
        }
    }

    private static void EmitSingleEdit(
        IReadOnlyList<string> orig,
        IReadOnlyList<string> rev,
        in MiddleSnake snake,
        List<(DiffOperation Operation, string Text)> output)
    {
        if (snake.EditOp == DiffOperation.Delete)
        {
            output.Add((DiffOperation.Delete, orig[snake.EditOrigIndex]));
        }
        else
        {
            output.Add((DiffOperation.Insert, rev[snake.EditRevIndex]));
        }
    }

    private static void EmitSnake(
        IReadOnlyList<string> orig,
        in MiddleSnake snake,
        List<(DiffOperation Operation, string Text)> output)
    {
        for (var i = 0; i < snake.SnakeLen; i++)
        {
            output.Add((DiffOperation.Equal, orig[snake.SnakeOrigStart + i]));
        }
    }

    private static bool TryFindMiddleSnake(
        IReadOnlyList<string> orig,
        int a0,
        int n,
        IReadOnlyList<string> rev,
        int b0,
        int m,
        int maxEditDistance,
        int[] vf,
        int[] vb,
        out MiddleSnake snake)
    {
        var delta = n - m;
        var odd = (delta & 1) != 0;
        var maxHalfD = Math.Min((n + m + 1) / 2, (maxEditDistance + 1) / 2);
        var offset = maxHalfD + 2;

        // d = 0 initialization (common prefix/suffix already trimmed, so no diagonal progress at d = 0).
        vf[offset] = 0;
        vb[offset] = 0;

        for (var d = 1; d <= maxHalfD; d++)
        {
            // Forward search step d
            for (var k = -d; k <= d; k += 2)
            {
                var down = k == -d || (k != d && vf[offset + k - 1] < vf[offset + k + 1]);
                var xPrev = down ? vf[offset + k + 1] : vf[offset + k - 1];
                var xStart = down ? xPrev : xPrev + 1;
                var yStart = xStart - k;
                var yPrev = down ? yStart - 1 : yStart;

                var x = xStart;
                var y = yStart;
                while (x < n && y < m && string.Equals(orig[a0 + x], rev[b0 + y], StringComparison.Ordinal))
                {
                    x++;
                    y++;
                }

                vf[offset + k] = x;

                if (odd && xStart <= n && yStart <= m && xPrev >= 0 && yPrev >= 0)
                {
                    var c = delta - k;
                    if (c >= -(d - 1) && c <= d - 1)
                    {
                        var u = vb[offset + c];
                        if (x + u >= n)
                        {
                            snake = new MiddleSnake(
                                IsForward: true,
                                LeftN: xPrev,
                                LeftM: yPrev,
                                EditOp: down ? DiffOperation.Insert : DiffOperation.Delete,
                                EditOrigIndex: a0 + xPrev,
                                EditRevIndex: b0 + yPrev,
                                SnakeOrigStart: a0 + xStart,
                                SnakeLen: x - xStart,
                                RightA: a0 + x,
                                RightB: b0 + y,
                                RightN: n - x,
                                RightM: m - y);
                            return true;
                        }
                    }
                }
            }

            // Backward search step d (in mirrored coordinates u = n - x, v = m - y)
            for (var c = -d; c <= d; c += 2)
            {
                var down = c == -d || (c != d && vb[offset + c - 1] < vb[offset + c + 1]);
                var uPrev = down ? vb[offset + c + 1] : vb[offset + c - 1];
                var uStart = down ? uPrev : uPrev + 1;
                var vStart = uStart - c;
                var vPrev = down ? vStart - 1 : vStart;

                var u = uStart;
                var v = vStart;
                while (u < n && v < m && string.Equals(orig[a0 + n - 1 - u], rev[b0 + m - 1 - v], StringComparison.Ordinal))
                {
                    u++;
                    v++;
                }

                vb[offset + c] = u;

                if (!odd && uStart <= n && vStart <= m && uPrev >= 0 && vPrev >= 0)
                {
                    var k = delta - c;
                    if (k >= -d && k <= d)
                    {
                        var xFwd = vf[offset + k];
                        if (xFwd + u >= n)
                        {
                            var xSnakeStart = n - u;
                            var ySnakeStart = m - v;
                            var xSnakeEnd = n - uStart;
                            var ySnakeEnd = m - vStart;
                            var xAfterEdit = n - uPrev;
                            var yAfterEdit = m - vPrev;

                            snake = new MiddleSnake(
                                IsForward: false,
                                LeftN: xSnakeStart,
                                LeftM: ySnakeStart,
                                EditOp: down ? DiffOperation.Insert : DiffOperation.Delete,
                                EditOrigIndex: a0 + xSnakeEnd,
                                EditRevIndex: b0 + ySnakeEnd,
                                SnakeOrigStart: a0 + xSnakeStart,
                                SnakeLen: u - uStart,
                                RightA: a0 + xAfterEdit,
                                RightB: b0 + yAfterEdit,
                                RightN: n - xAfterEdit,
                                RightM: m - yAfterEdit);
                            return true;
                        }
                    }
                }
            }
        }

        snake = default;
        return false;
    }

    private static IReadOnlyList<DiffSegment> CoalesceAndNormalize(List<(DiffOperation Operation, string Text)> ops)
    {
        if (ops.Count == 0)
        {
            return Array.Empty<DiffSegment>();
        }

        var result = new List<DiffSegment>();
        var pendingEqual = new StringBuilder();
        var pendingDelete = new StringBuilder();
        var pendingInsert = new StringBuilder();

        void FlushEdits()
        {
            if (pendingDelete.Length > 0)
            {
                result.Add(new DiffSegment(DiffOperation.Delete, pendingDelete.ToString()));
                pendingDelete.Clear();
            }

            if (pendingInsert.Length > 0)
            {
                result.Add(new DiffSegment(DiffOperation.Insert, pendingInsert.ToString()));
                pendingInsert.Clear();
            }
        }

        void FlushEqual()
        {
            if (pendingEqual.Length > 0)
            {
                result.Add(new DiffSegment(DiffOperation.Equal, pendingEqual.ToString()));
                pendingEqual.Clear();
            }
        }

        foreach (var (op, text) in ops)
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            switch (op)
            {
                case DiffOperation.Equal:
                    FlushEdits();
                    pendingEqual.Append(text);
                    break;
                case DiffOperation.Delete:
                    FlushEqual();
                    pendingDelete.Append(text);
                    break;
                case DiffOperation.Insert:
                    FlushEqual();
                    pendingInsert.Append(text);
                    break;
            }
        }

        FlushEqual();
        FlushEdits();

        return result;
    }

    private static string ConcatSlice(IReadOnlyList<string> items, int start, int count)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            sb.Append(items[start + i]);
        }

        return sb.ToString();
    }

    private static List<string> SplitKeepingNewlines(string text)
    {
        var lines = new List<string>();
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                lines.Add(text[start..(i + 1)]);
                start = i + 1;
            }
            else if (text[i] == '\n')
            {
                lines.Add(text[start..(i + 1)]);
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }

    private readonly record struct MiddleSnake(
        bool IsForward,
        int LeftN,
        int LeftM,
        DiffOperation EditOp,
        int EditOrigIndex,
        int EditRevIndex,
        int SnakeOrigStart,
        int SnakeLen,
        int RightA,
        int RightB,
        int RightN,
        int RightM);
}
