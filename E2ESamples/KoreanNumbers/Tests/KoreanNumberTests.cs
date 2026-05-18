// End-to-end tests for the Korean numbers sample. Three sets:
//
// 1. Golden corpus: every (input, expected number) pair from the
//    upstream test suite (tests/test.js in ohgyun/korean-numbers).
//    Original and Rewrite must both produce the same number.
//    This is the "behavioral parity" check.
//
// 2. Reject corpus: inputs the original silently treats as 0 (regex
//    rejection, mid-string non-number chars). Both parsers must agree
//    on the number value the upstream produces (0). The Rewrite
//    additionally has to report a sensible position, which the
//    Original cannot. This is the "errors are useful" check.
//
// 3. AST shape: the Rewrite exposes a walkable tree with SourceRange
//    on every term. A handful of inputs are locked to specific tree
//    shapes so future grammar changes can't silently regress this.

using System.Linq;
using NUnit.Framework;
using KoreanNumbersSample.Original;
using KoreanNumbersSample.Rewrite;

namespace KoreanNumbersSample.Tests;

[TestFixture]
public class KoreanNumberGoldenTests
{
    // (input, expected) drawn verbatim from tests/test.js in
    // https://github.com/ohgyun/korean-numbers, commit master.
    public static readonly object[] UpstreamCases =
    {
        new object[] { "백", 100L },
        new object[] { "100", 100L },
        new object[] { "이백", 200L },
        new object[] { "삼백", 300L },
        new object[] { "사백", 400L },
        new object[] { "오백", 500L },
        new object[] { "육백", 600L },
        new object[] { "칠백", 700L },
        new object[] { "팔백", 800L },
        new object[] { "구백", 900L },
        new object[] { "천", 1000L },
        new object[] { "천삼백이십", 1320L },
        new object[] { "이천육백", 2600L },
        new object[] { "팔천팔백삼", 8803L },
        new object[] { "이천삼백", 2300L },
        new object[] { "오천육백", 5600L },
        new object[] { "칠천이백", 7200L },
        new object[] { "팔만", 80_000L },
        new object[] { "구만", 90_000L },
        new object[] { "십만이천", 102_000L },
        new object[] { "십이만삼천", 123_000L },
        new object[] { "십구만칠천", 197_000L },
        new object[] { "오십이만삼천", 523_000L },
        new object[] { "백삼십이만칠천", 1_327_000L },
        new object[] { "삼백사십만", 3_400_000L },
        new object[] { "일십팔만", 180_000L },
        new object[] { "일십만이백", 100_200L },
        new object[] { "십팔억", 1_800_000_000L },
        new object[] { "팔억구천삼백이", 800_009_302L },
        new object[] { "12만8천", 128_000L },
        new object[] { "10만", 100_000L },
        new object[] { "14", 14L },
        new object[] { "18천2백", 18_200L },
        new object[] { "18천2", 18_002L },
        new object[] { "만", 10_000L },
        new object[] { "만천", 11_000L },
        new object[] { "만백", 10_100L },
        new object[] { "만일", 10_001L },
        new object[] { "십일", 11L },
        new object[] { "천만일", 10_000_001L },
    };

    [TestCaseSource(nameof(UpstreamCases))]
    public void Original_matches_upstream(string input, long expected)
    {
        Assert.That(OriginalKoreanNumberParser.Parse(input), Is.EqualTo(expected),
            $"Original parser disagrees with upstream on '{input}'");
    }

    [TestCaseSource(nameof(UpstreamCases))]
    public void Rewrite_matches_upstream(string input, long expected)
    {
        bool ok = KoreanNumberParser.TryParse(input, out long value, out var error);
        Assert.That(ok, Is.True, $"Rewrite rejected '{input}': {error}");
        Assert.That(value, Is.EqualTo(expected),
            $"Rewrite parser disagrees with upstream on '{input}'");
    }

    [TestCaseSource(nameof(UpstreamCases))]
    public void Original_and_Rewrite_agree(string input, long expected)
    {
        long originalValue = OriginalKoreanNumberParser.Parse(input);
        bool rewriteOk = KoreanNumberParser.TryParse(input, out long rewriteValue, out _);
        Assert.That(rewriteOk, Is.True, $"Rewrite rejected '{input}'");
        Assert.That(rewriteValue, Is.EqualTo(originalValue),
            $"Parity mismatch on '{input}': original={originalValue}, rewrite={rewriteValue}");
    }
}

[TestFixture]
public class KoreanNumberParseCountTests
{
    // ParseCount goes through a flat lookup table identical in both
    // implementations. The grammar isn't involved (count words like
    // 하나, 다섯, 열셋 use a totally different vocabulary than the
    // Sino-Korean digit system the grammar handles). Kept here as a
    // regression test so a future refactor that drops the count
    // map gets caught.
    public static readonly object[] CountCases =
    {
        new object[] { "하나", 1 },
        new object[] { "둘", 2 },
        new object[] { "셋", 3 },
        new object[] { "넷", 4 },
        new object[] { "다섯", 5 },
        new object[] { "여섯", 6 },
        new object[] { "일곱", 7 },
        new object[] { "여덟", 8 },
        new object[] { "아홉", 9 },
        new object[] { "열", 10 },
        new object[] { "열셋", 13 },
        new object[] { "스물", 20 },
        new object[] { "한명", 1 },
        new object[] { "스무명", 20 },
        new object[] { "기타", 0 },
    };

    [TestCaseSource(nameof(CountCases))]
    public void Both_implementations_agree(string input, int expected)
    {
        Assert.That(OriginalKoreanNumberParser.ParseCount(input), Is.EqualTo(expected));
        Assert.That(KoreanNumberParser.ParseCount(input), Is.EqualTo(expected));
    }
}

[TestFixture]
public class KoreanNumberMoneyTests
{
    public static readonly object[] MoneyCases =
    {
        new object[] { "백이십삼만원", 1_230_000L },
        new object[] { "123만원", 1_230_000L },
        new object[] { "만원", 10_000L },
        new object[] { "10만원", 100_000L },
        new object[] { "백원", 100L },
    };

    [TestCaseSource(nameof(MoneyCases))]
    public void Both_implementations_agree(string input, long expected)
    {
        Assert.That(OriginalKoreanNumberParser.ParseMoney(input), Is.EqualTo(expected));
        Assert.That(KoreanNumberParser.ParseMoney(input), Is.EqualTo(expected));
    }
}

[TestFixture]
public class KoreanNumberRejectTests
{
    public record RejectCase(string Input, string DescribesProblem);

    public static readonly RejectCase[] InvalidInputs =
    {
        new("",              "empty input"),
        new("원",            "no number, just suffix"),
        new("백a",           "trailing non-Korean letter"),
        new("a백",           "leading non-Korean letter"),
        new("백 만",         "space inside a number"),
        new("백.만",         "dot inside a number"),
        new("1+2",           "arithmetic expression"),
        new("백천!",         "trailing punctuation"),
        new("12.3",          "decimal point"),
    };

    [TestCaseSource(nameof(InvalidInputs))]
    public void Original_silently_returns_zero(RejectCase reject)
    {
        // The upstream library's "error reporting" is to silently
        // return 0 from the regex-failure branch. This is the friction
        // the rewrite improves on.
        long value = OriginalKoreanNumberParser.Parse(reject.Input);
        Assert.That(value, Is.EqualTo(0L),
            $"Upstream behavior: '{reject.Input}' ({reject.DescribesProblem}) should silently return 0");
    }

    [TestCaseSource(nameof(InvalidInputs))]
    public void Rewrite_rejects_with_positioned_error(RejectCase reject)
    {
        bool ok = KoreanNumberParser.TryParse(reject.Input, out _, out var error);
        Assert.That(ok, Is.False, $"Rewrite should reject '{reject.Input}' ({reject.DescribesProblem})");
        Assert.That(error, Is.Not.Null);
        Assert.That(error!.CharIndex, Is.GreaterThanOrEqualTo(0));
        Assert.That(error.CharIndex, Is.LessThanOrEqualTo(reject.Input.Length));
        Assert.That(error.Message, Is.Not.Empty);
    }
}

[TestFixture]
public class KoreanNumberErrorPositionTests
{
    // Side-by-side: input, expected position the Rewrite should report.
    // The Original returns 0 with no position, so these checks verify the
    // rewrite's behavior only. The "expected position" is the char
    // index at which the parser gave up.

    [Test]
    public void Empty_input_points_at_position_zero()
    {
        bool ok = KoreanNumberParser.TryParse("", out _, out var error);
        Assert.That(ok, Is.False);
        Assert.That(error!.CharIndex, Is.EqualTo(0));
        Assert.That(error.Line, Is.EqualTo(0));
        Assert.That(error.Column, Is.EqualTo(0));
    }

    [Test]
    public void Trailing_garbage_points_at_first_bad_char()
    {
        bool ok = KoreanNumberParser.TryParse("백a", out _, out var error);
        Assert.That(ok, Is.False);
        Assert.That(error!.CharIndex, Is.EqualTo(1));
    }

    [Test]
    public void Leading_garbage_points_at_position_zero()
    {
        bool ok = KoreanNumberParser.TryParse("a백", out _, out var error);
        Assert.That(ok, Is.False);
        Assert.That(error!.CharIndex, Is.EqualTo(0));
    }

    [Test]
    public void Mid_string_space_points_at_the_space()
    {
        bool ok = KoreanNumberParser.TryParse("백 만", out _, out var error);
        Assert.That(ok, Is.False);
        Assert.That(error!.CharIndex, Is.EqualTo(1));
    }

    [Test]
    public void Error_format_uses_one_based_line_and_column()
    {
        bool ok = KoreanNumberParser.TryParse("백a", out _, out var error);
        Assert.That(ok, Is.False);
        Assert.That(error!.ToString(), Does.Contain("line 1"));
        Assert.That(error.ToString(), Does.Contain("column 2"));
    }
}

[TestFixture]
public class KoreanNumberParseTreeTests
{
    // The rewrite doesn't materialize a typed AST: the InductorParser
    // Symbol tree returned by KoreanNumberGrammar.KoreanNumber.Parse is
    // already an AST with named nodes (scaledTerm, digits, scale) and
    // SourceText / SourceRange on every Symbol. These tests verify that
    // shape so a future consumer (a number-input UI, a breakdown view)
    // can rely on it without re-parsing.

    [Test]
    public void Tree_for_single_scale_has_one_scaledTerm_child()
    {
        var result = KoreanNumberGrammar.KoreanNumber.Parse("백");
        Assert.That(result.Success, Is.True);
        Assert.That(result.Tree!.Children.Count, Is.EqualTo(1));

        var scaledTerm = result.Tree.Children[0];
        Assert.That(scaledTerm.Is(KoreanNumberGrammar.ScaledTerm), Is.True);
        Assert.That(scaledTerm.SourceText, Is.EqualTo("백"));
        Assert.That(scaledTerm.Find(KoreanNumberGrammar.Scale)!.SourceText, Is.EqualTo("백"));
        Assert.That(scaledTerm.Find(KoreanNumberGrammar.Digits), Is.Null);
    }

    [Test]
    public void Tree_for_trailing_ones_has_a_bare_digits_child_at_the_end()
    {
        // 만일 = 10000 + 1 = 10001. The 일 doesn't pair with any scale,
        // so the tree ends with a bare digits child.
        var result = KoreanNumberGrammar.KoreanNumber.Parse("만일");
        Assert.That(result.Success, Is.True);
        Assert.That(result.Tree!.Children.Count, Is.EqualTo(2));

        Assert.That(result.Tree.Children[0].Is(KoreanNumberGrammar.ScaledTerm), Is.True);
        Assert.That(result.Tree.Children[1].Is(KoreanNumberGrammar.Digits), Is.True);
        Assert.That(result.Tree.Children[1].SourceText, Is.EqualTo("일"));
    }

    [Test]
    public void Every_term_in_the_tree_carries_a_source_range()
    {
        // 백이십삼만 splits into three scaledTerm children: 백, 이십, 삼만.
        var result = KoreanNumberGrammar.KoreanNumber.Parse("백이십삼만");
        Assert.That(result.Success, Is.True);
        Assert.That(result.Tree!.Children.Count, Is.EqualTo(3));

        foreach (var child in result.Tree.Children)
        {
            Assert.That(child.SourceRange, Is.Not.Null);
            Assert.That(child.SourceText, Is.Not.Empty);
        }

        Assert.That(result.Tree.Children[0].Find(KoreanNumberGrammar.Scale)!.SourceText, Is.EqualTo("백"));
        Assert.That(result.Tree.Children[1].Find(KoreanNumberGrammar.Scale)!.SourceText, Is.EqualTo("십"));
        Assert.That(result.Tree.Children[2].Find(KoreanNumberGrammar.Scale)!.SourceText, Is.EqualTo("만"));
    }
}
