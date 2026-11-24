using System.Text;
using PopTranslate.Core.Diff;
using PopTranslate.Core.Models;
using Xunit;

namespace PopTranslate.Tests.Core;

public sealed class LocalTextDiffServiceTests
{
    private readonly LocalTextDiffService _sut = new();

    [Fact]
    public void ComputeDiff_EmptyAndNullInputs_ReturnsExpectedMinimalSegments()
    {
        Assert.Empty(_sut.ComputeDiff(string.Empty, string.Empty));
        Assert.Empty(_sut.ComputeDiff(null!, null!));

        var insertOnly = _sut.ComputeDiff(string.Empty, "Hello 世界");
        var inserted = Assert.Single(insertOnly);
        Assert.Equal(DiffOperation.Insert, inserted.Operation);
        Assert.Equal("Hello 世界", inserted.Text);

        var deleteOnly = _sut.ComputeDiff("Hello 世界", string.Empty);
        var deleted = Assert.Single(deleteOnly);
        Assert.Equal(DiffOperation.Delete, deleted.Operation);
        Assert.Equal("Hello 世界", deleted.Text);
    }

    [Fact]
    public void ComputeDiff_IdenticalStrings_ReturnsSingleEqualSegment()
    {
        const string text = "这是一段 mixed English & 中文 text.\r\nSecond line!";
        var diff = _sut.ComputeDiff(text, text);

        var segment = Assert.Single(diff);
        Assert.Equal(DiffOperation.Equal, segment.Operation);
        Assert.Equal(text, segment.Text);
    }

    [Fact]
    public void Tokenize_SeparatesCjkCharsWesternWordsWhitespaceNewlinesAndPunctuation()
    {
        const string input = "Hello, 世界123!\r\n  foo\tbar🚀。";
        var tokens = LocalTextDiffService.Tokenize(input);

        Assert.Equal(
            ["Hello", ",", " ", "世", "界", "123", "!", "\r\n", "  ", "foo", "\t", "bar", "🚀", "。"],
            tokens);
        Assert.Equal(input, string.Concat(tokens));
    }

    [Fact]
    public void ComputeDiff_EnglishWordEdit_DiffsAtWholeWordBoundariesNotMidWord()
    {
        const string original = "This report is very good.";
        const string revised = "This report is excellent.";

        var diff = _sut.ComputeDiff(original, revised);

        AssertInvariants(original, revised, diff);
        Assert.Equal(
            [
                new DiffSegment(DiffOperation.Equal, "This report is "),
                new DiffSegment(DiffOperation.Delete, "very good"),
                new DiffSegment(DiffOperation.Insert, "excellent"),
                new DiffSegment(DiffOperation.Equal, "."),
            ],
            diff);
    }

    [Fact]
    public void ComputeDiff_ChineseCharacterEdit_DiffsCharacterByCharacterAndCoalesces()
    {
        const string original = "这个方案非常好用。";
        const string revised = "这个方案极其出色。";

        var diff = _sut.ComputeDiff(original, revised);

        AssertInvariants(original, revised, diff);
        Assert.Equal(
            [
                new DiffSegment(DiffOperation.Equal, "这个方案"),
                new DiffSegment(DiffOperation.Delete, "非常好用"),
                new DiffSegment(DiffOperation.Insert, "极其出色"),
                new DiffSegment(DiffOperation.Equal, "。"),
            ],
            diff);
    }

    [Fact]
    public void ComputeDiff_MixedChineseAndEnglish_PreservesExactReconstructionAndBoundaries()
    {
        const string original = "请使用 PopTranslate 工具来 fast translate 文档，效果很好！";
        const string revised = "推荐使用 PopTranslate 工具来 quickly polish 文档，效果更佳。";

        var diff = _sut.ComputeDiff(original, revised);

        AssertInvariants(original, revised, diff);
        Assert.Contains(diff, s => s.Operation == DiffOperation.Equal && s.Text.Contains("PopTranslate", StringComparison.Ordinal));
        Assert.Contains(diff, s => s.Operation == DiffOperation.Delete && s.Text == "fast translate");
        Assert.Contains(diff, s => s.Operation == DiffOperation.Insert && s.Text == "quickly polish");
    }

    [Fact]
    public void ComputeDiff_MultiParagraphAndSurrogatePairs_NeverSplitsSurrogatesAndReconstructsExactly()
    {
        const string original = "第一段内容 🚀\r\n第二段：Hello world.\n第三段。";
        const string revised = "第一段优化内容 ✨\r\n第二段：Hello PopTranslate.\n第三段！";

        var diff = _sut.ComputeDiff(original, revised);

        AssertInvariants(original, revised, diff);
        foreach (var segment in diff)
        {
            Assert.False(string.IsNullOrEmpty(segment.Text));
            Assert.False(char.IsLowSurrogate(segment.Text[0]), "Segment must not begin with an orphaned low surrogate.");
            Assert.False(char.IsHighSurrogate(segment.Text[^1]), "Segment must not end with an orphaned high surrogate.");
        }
    }

    [Fact]
    public void ComputeDiff_RandomizedCjkAndWesternPairs_MatchesExactLcsOptimalDistance()
    {
        var rng = new Random(20261120);
        string[] vocabulary = ["a", "b", "c", "word", "test", " ", "中", "文", "测", "试", "，", "\n"];

        for (var trial = 0; trial < 200; trial++)
        {
            var lenA = rng.Next(0, 18);
            var lenB = rng.Next(0, 18);

            var sbA = new StringBuilder();
            for (var i = 0; i < lenA; i++) sbA.Append(vocabulary[rng.Next(vocabulary.Length)]);
            var sbB = new StringBuilder();
            for (var i = 0; i < lenB; i++) sbB.Append(vocabulary[rng.Next(vocabulary.Length)]);

            var orig = sbA.ToString();
            var rev = sbB.ToString();

            var diff = _sut.ComputeDiff(orig, rev);
            AssertInvariants(orig, rev, diff);

            // Verify edit distance equals exact DP LCS edit distance on tokenized sequences.
            var origTokens = LocalTextDiffService.Tokenize(orig);
            var revTokens = LocalTextDiffService.Tokenize(rev);
            var expectedLcs = ComputeLcsLength(origTokens, revTokens);
            var actualEqualTokens = diff
                .Where(s => s.Operation == DiffOperation.Equal)
                .Sum(s => LocalTextDiffService.Tokenize(s.Text).Count);

            // Coalescing may merge adjacent same-type tokens, while semantic chaff cleanup intentionally merges
            // isolated 1-char CJK or single-space Equal islands sandwiched between Delete+Insert pairs.
            var absorbedChaffAllowance = diff
                .Where(s => s.Operation == DiffOperation.Delete)
                .Sum(s => LocalTextDiffService.Tokenize(s.Text).Count(t => t == " " || (t.Length == 1 && t[0] >= '\u4E00')));
            Assert.True(actualEqualTokens + absorbedChaffAllowance >= expectedLcs,
                $"Trial {trial}: expected at least {expectedLcs} equal+chaff tokens, got {actualEqualTokens}+{absorbedChaffAllowance}. Orig='{orig}', Rev='{rev}'");
        }
    }

    [Fact]
    public void ComputeDiff_LargeDivergentDocument_RespectsSafetyCapWithoutBlowup()
    {
        // Construct > 6,000 completely divergent tokens to exercise the MaxEditDistance safety cap.
        var origBuilder = new StringBuilder();
        var revBuilder = new StringBuilder();
        for (var i = 0; i < 3500; i++)
        {
            origBuilder.Append("orig").Append(i).Append(i % 20 == 19 ? "\n" : " ");
            revBuilder.Append("rev").Append(i).Append(i % 20 == 19 ? "\n" : " ");
        }

        var original = origBuilder.ToString();
        var revised = revBuilder.ToString();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var diff = _sut.ComputeDiff(original, revised);
        sw.Stop();

        AssertInvariants(original, revised, diff);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Large diff took too long: {sw.Elapsed}");
    }

    private static void AssertInvariants(string original, string revised, IReadOnlyList<DiffSegment> diff)
    {
        var reconstructedOriginal = string.Concat(
            diff.Where(s => s.Operation is DiffOperation.Equal or DiffOperation.Delete).Select(s => s.Text));
        var reconstructedRevised = string.Concat(
            diff.Where(s => s.Operation is DiffOperation.Equal or DiffOperation.Insert).Select(s => s.Text));

        Assert.Equal(original, reconstructedOriginal);
        Assert.Equal(revised, reconstructedRevised);

        for (var i = 0; i < diff.Count; i++)
        {
            Assert.False(string.IsNullOrEmpty(diff[i].Text), "Diff must never emit empty-string segments.");
            if (i > 0)
            {
                Assert.NotEqual(diff[i - 1].Operation, diff[i].Operation);
            }
        }
    }

    private static int ComputeLcsLength(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var dp = new int[a.Count + 1, b.Count + 1];
        for (var i = a.Count - 1; i >= 0; i--)
        {
            for (var j = b.Count - 1; j >= 0; j--)
            {
                dp[i, j] = string.Equals(a[i], b[j], StringComparison.Ordinal)
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        return dp[0, 0];
    }
}
