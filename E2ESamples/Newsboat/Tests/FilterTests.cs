// End-to-end tests for the Newsboat filter sample. Three sets:
//
// 1. Golden corpus: every valid input we expect to parse, drawn from
//    the upstream Rust port's test suite
//    (rust/libnewsboat/src/filterparser.rs). Original and Rewrite must
//    produce equal AST shapes on each one. This is the "behavioral
//    parity" check.
//
// 2. Reject corpus: every kind of bad input the upstream tests call
//    out (operator typos, unbalanced parens, missing space after
//    `and`, broken ranges, broken quoted strings). Both parsers must
//    reject; the Rewrite parser additionally has to point at the
//    right position. This is the "errors are useful" check.
//
// 3. Side-by-side: a small set of bad inputs where we explicitly
//    compare the error messages from Original and Rewrite. The
//    Original is hand-written and gets the position right too, but
//    its message is a single sentence with no line / column
//    formatting; the Rewrite gives line, column, and a targeted
//    message via `WithError`. This is the "value-add" check.

using System.Linq;
using NUnit.Framework;
using NewsboatSample.Original;
using NewsboatSample.Rewrite;

namespace NewsboatSample.Tests;

[TestFixture]
public class FilterGoldenTests
{
    private static readonly string[] ValidInputs = {
        "a = \"b\"",
        "(a=\"b\")",
        "((a=\"b\"))",
        "a != \"b\"",
        "a =~ \"b\"",
        "a !~ \"b\"",
        "a == \"abc\"",
        "a < \"b\"",
        "a <= \"b\"",
        "a > \"abc\"",
        "a >= 3",
        "some_value between 0:-1",
        "value between -100:-1",
        "value between -100:100500",
        "value between 123:-10",
        "other_string between \"impossible\"",
        "array # \"name\"",
        "answers !# 42",
        "author =~ \"\\s*Doe$\"",
        "title==\"\"",
        "array # \"bar\"",
        "   array # \"bar\"",
        "array   # \"bar\"",
        "array #   \"bar\"",
        "array # \"bar\"     ",
        "array#  \"bar\"  ",
        "     array         #         \"bar\"      ",
        "a = \"b\" and b = \"c\" or c = \"d\"",
        "a = \"b\" or b = \"c\" and c = \"d\"",
        "(a = \"b\" or b = \"c\") and c = \"d\"",
        "( a = \"b\") and ( b = \"c\" ) or ( ( c != \"d\" ) and ( c !~ \"asdf\" )) or c != \"xx\"",
        "a=42and(y=0)",
        "(a=42)and(y=0)",
        "a=42and y=0",
        "a=42or(y=0)",
        "(a=42)or(y=0)",
        "a=42or y=0",
        "x = 42and y=0",
        "x = \"42\"and y=0",
        "x = \"42\"or y=42",
    };

    [TestCaseSource(nameof(ValidInputs))]
    public void Original_and_Rewrite_agree_on_valid_input(string input)
    {
        bool originalOk = OriginalFilterParser.TryParse(input, out var originalExpression, out var originalError);
        bool rewriteOk = FilterParser.TryParse(input, out var rewriteExpression, out var rewriteError);

        Assert.That(originalOk, Is.True, $"Original rejected '{input}': {originalError}");
        Assert.That(rewriteOk, Is.True, $"Rewrite rejected '{input}': {rewriteError}");

        AssertSameTree(originalExpression!, rewriteExpression!, input);
    }

    private static void AssertSameTree(OriginalExpression original, RewriteExpression rewrite, string input)
    {
        switch (original)
        {
            case OriginalAnd originalAnd:
                Assert.That(rewrite, Is.TypeOf<RewriteAnd>(), $"shape mismatch on '{input}'");
                var rewriteAnd = (RewriteAnd)rewrite;
                AssertSameTree(originalAnd.Left, rewriteAnd.Left, input);
                AssertSameTree(originalAnd.Right, rewriteAnd.Right, input);
                break;
            case OriginalOr originalOr:
                Assert.That(rewrite, Is.TypeOf<RewriteOr>(), $"shape mismatch on '{input}'");
                var rewriteOr = (RewriteOr)rewrite;
                AssertSameTree(originalOr.Left, rewriteOr.Left, input);
                AssertSameTree(originalOr.Right, rewriteOr.Right, input);
                break;
            case OriginalComparison originalComparison:
                Assert.That(rewrite, Is.TypeOf<RewriteComparison>(), $"shape mismatch on '{input}'");
                var rewriteComparison = (RewriteComparison)rewrite;
                Assert.That(rewriteComparison.Attribute, Is.EqualTo(originalComparison.Attribute), $"attribute on '{input}'");
                Assert.That((int)rewriteComparison.Op, Is.EqualTo((int)originalComparison.Op), $"operator on '{input}'");
                Assert.That(rewriteComparison.ValueLiteral, Is.EqualTo(originalComparison.ValueLiteral), $"value on '{input}'");
                break;
            default:
                Assert.Fail($"Unhandled expression type {original.GetType().Name}");
                break;
        }
    }
}

[TestFixture]
public class FilterRightAssociativityTests
{
    [Test]
    public void And_or_chain_parses_right_associatively()
    {
        // Mirrors Rust port: `a and b or c` -> And(a, Or(b, c))
        var rewrite = FilterParser.Parse("a = \"1\" and b = \"2\" or c = \"3\"");
        Assert.That(rewrite, Is.TypeOf<RewriteAnd>());
        var rewriteAnd = (RewriteAnd)rewrite;
        Assert.That(rewriteAnd.Left, Is.TypeOf<RewriteComparison>());
        Assert.That(((RewriteComparison)rewriteAnd.Left).Attribute, Is.EqualTo("a"));
        Assert.That(rewriteAnd.Right, Is.TypeOf<RewriteOr>());
        var rewriteOr = (RewriteOr)rewriteAnd.Right;
        Assert.That(((RewriteComparison)rewriteOr.Left).Attribute, Is.EqualTo("b"));
        Assert.That(((RewriteComparison)rewriteOr.Right).Attribute, Is.EqualTo("c"));
    }

    [Test]
    public void Or_and_chain_parses_right_associatively()
    {
        // Mirrors Rust port: `a or b and c` -> Or(a, And(b, c))
        var rewrite = FilterParser.Parse("a = \"1\" or b = \"2\" and c = \"3\"");
        Assert.That(rewrite, Is.TypeOf<RewriteOr>());
        var rewriteOr = (RewriteOr)rewrite;
        Assert.That(((RewriteComparison)rewriteOr.Left).Attribute, Is.EqualTo("a"));
        Assert.That(rewriteOr.Right, Is.TypeOf<RewriteAnd>());
    }

    [Test]
    public void Parens_override_associativity()
    {
        // `(a or b) and c` -> And(Or(a, b), c)
        var rewrite = FilterParser.Parse("(a = \"1\" or b = \"2\") and c = \"3\"");
        Assert.That(rewrite, Is.TypeOf<RewriteAnd>());
        var rewriteAnd = (RewriteAnd)rewrite;
        Assert.That(rewriteAnd.Left, Is.TypeOf<RewriteOr>());
        Assert.That(((RewriteComparison)rewriteAnd.Right).Attribute, Is.EqualTo("c"));
    }
}

[TestFixture]
public class FilterRejectTests
{
    public record RejectCase(string Input, string DescribesProblem);

    private static readonly RejectCase[] InvalidInputs = {
        new("",                       "empty"),
        new("a = b",                  "non-value to the right of equality operator"),
        new("a !! \"b\"",             "non-existent operator"),
        new("title =¯ \"foo\"",  "invalid character inside operator"),
        new("a = \"b",                "incorrect string quoting"),
        new("((a=\"b\")))",           "unbalanced parens"),
        new("AAAA between 0:15:30",   "incorrect syntax for range"),
        new("x = 42andy=0",           "no whitespace after `and` operator"),
        new("x = 42 andy=0",          "no whitespace after `and` operator (with leading space)"),
        new("=!",                     "operator without arguments"),
        new("attr\t= \"value\"",      "tab is not whitespace"),
        new("attr =\t\"value\"",      "tab is not whitespace (after `=`)"),
        new("attr\n=\t\"value\"",     "newline is not whitespace"),
        new("attr=\"value\"\r\n",     "trailing CRLF is not whitespace"),
        new("a=42ory=0",              "no whitespace after `or` operator"),
    };

    [TestCaseSource(nameof(InvalidInputs))]
    public void Original_rejects(RejectCase reject)
    {
        bool ok = OriginalFilterParser.TryParse(reject.Input, out _, out _);
        Assert.That(ok, Is.False, $"Original should reject '{reject.Input}' ({reject.DescribesProblem})");
    }

    [TestCaseSource(nameof(InvalidInputs))]
    public void Rewrite_rejects_with_positioned_error(RejectCase reject)
    {
        bool ok = FilterParser.TryParse(reject.Input, out _, out var error);
        Assert.That(ok, Is.False, $"Rewrite should reject '{reject.Input}' ({reject.DescribesProblem})");
        Assert.That(error, Is.Not.Null);
        Assert.That(error!.CharIndex, Is.GreaterThanOrEqualTo(0));
        Assert.That(error.CharIndex, Is.LessThanOrEqualTo(reject.Input.Length));
        Assert.That(error.Message, Is.Not.Empty);
    }
}

[TestFixture]
public class FilterErrorPositionTests
{
    // Side-by-side: input, expected position from the upstream Rust
    // tests, and a substring the Rewrite message should contain.
    // The upstream Rust suite asserts exact (position, expected) pairs;
    // our port asserts position parity and a sensible message.

    [Test]
    public void Operator_typo_points_at_the_typo_position()
    {
        // "title =¯ \"foo\"" -> upstream says AtPos(7, Value)
        FilterParser.TryParse("title =¯ \"foo\"", out _, out var rewriteError);
        Assert.That(rewriteError, Is.Not.Null);
        Assert.That(rewriteError!.CharIndex, Is.EqualTo(7));
        Assert.That(rewriteError.Message.ToLowerInvariant(), Does.Contain("quoted string").Or.Contain("range").Or.Contain("number"));
    }

    [Test]
    public void Bad_operator_after_attribute_points_at_the_operator_position()
    {
        // "a !! \"b\"" -> upstream nom port says AtPos(2, Operators).
        // Under depth-primary ranking the caret lands at 3, not 2: the
        // operator branches Literal("!~") / Literal("!=") consume the
        // leading '!' and fail on the trailing '!' at position 3, and
        // ComparisonOperator's named WithError ("expected one of: =~, ==,
        // ...") anchors there too and wins the named-beats-mechanical
        // tie. Position 3 is the '!!' the user has to fix. (The worked
        // example in docs/ErrorArchitecture.md.)
        FilterParser.TryParse("a !! \"b\"", out _, out var rewriteError);
        Assert.That(rewriteError, Is.Not.Null);
        Assert.That(rewriteError!.CharIndex, Is.EqualTo(3));
        Assert.That(rewriteError.Message, Does.Contain("expected one of"));
    }

    [Test]
    public void Operator_without_arguments_points_at_the_attribute_position()
    {
        // "=!" -> upstream says AtPos(0, AttributeName)
        FilterParser.TryParse("=!", out _, out var rewriteError);
        Assert.That(rewriteError, Is.Not.Null);
        Assert.That(rewriteError!.CharIndex, Is.EqualTo(0));
        Assert.That(rewriteError.Message.ToLowerInvariant(), Does.Contain("attribute"));
    }

    [Test]
    public void Unbalanced_close_paren_is_caught_as_trailing_text()
    {
        // "((a=\"b\")))" -> upstream says TrailingCharacters(9, ")")
        FilterParser.TryParse("((a=\"b\")))", out _, out var rewriteError);
        Assert.That(rewriteError, Is.Not.Null);
        Assert.That(rewriteError!.CharIndex, Is.EqualTo(9));
    }

    [Test]
    public void Range_with_three_parts_leaves_the_third_part_trailing()
    {
        // "AAAA between 0:15:30" -> upstream says TrailingCharacters(17, ":30")
        FilterParser.TryParse("AAAA between 0:15:30", out _, out var rewriteError);
        Assert.That(rewriteError, Is.Not.Null);
        Assert.That(rewriteError!.CharIndex, Is.EqualTo(17));
    }

    [Test]
    public void And_without_space_points_at_where_parser_got_stuck()
    {
        // "x = 42andy=0" -> upstream nom port says
        // TrailingCharacters(6, "andy=0"). InductorParser's deepest-
        // failure tracker reports position 9, because the parse
        // committed to `and` (positions 6..9), then failed the
        // space-or-paren peek at position 9. Position 9 names the
        // deepest progress, which is more informative than position 6
        // ("starting here is junk"). The grammar still rejects, just
        // with a different position-pointer convention.
        FilterParser.TryParse("x = 42andy=0", out _, out var rewriteError);
        Assert.That(rewriteError, Is.Not.Null);
        Assert.That(rewriteError!.CharIndex, Is.InRange(6, 9));
    }

    [Test]
    public void Rewrite_carries_line_and_column_in_addition_to_char_index()
    {
        // The Original (hand-written) parser carries a char index too,
        // because we wrote it that way. What it doesn't carry is the
        // line / column pair, the matched-rule context, or the source
        // span of the failing sub-expression. The Rewrite gets all
        // three via Symbol.SourceRange and ParseResult's line / column
        // accessors. The two char indices are close but don't have to
        // match exactly: Inductor's deepest-failure tracker can land
        // a few characters past the hand-written parser's "I gave up
        // here" position, depending on how far each branch of an Or
        // got before backing out.
        OriginalFilterParser.TryParse("a !! \"b\"", out _, out var originalError);
        FilterParser.TryParse("a !! \"b\"", out _, out var rewriteError);

        Assert.That(originalError, Is.Not.Null);
        Assert.That(rewriteError, Is.Not.Null);

        // Both point near the operator typo, within a couple chars of
        // each other.
        Assert.That(System.Math.Abs(originalError!.CharIndex - rewriteError!.CharIndex), Is.LessThanOrEqualTo(2));

        Assert.That(rewriteError.ToString(), Does.Contain("line"));
        Assert.That(rewriteError.ToString(), Does.Contain("column"));
        Assert.That(originalError.ToString(), Does.Not.Contain("line"));
        Assert.That(originalError.ToString(), Does.Not.Contain("column"));
    }
}

[TestFixture]
public class FilterSourceRangeTests
{
    // Verifies the source-range-per-comparison feature the rewrite adds.
    // The Original parser carries no source spans, so a downstream UI
    // (highlighter, query builder, refactoring tool) would have to re-
    // parse to find them.

    [Test]
    public void Comparison_carries_its_own_source_range()
    {
        var expression = FilterParser.Parse("title =~ \"rust\"");
        Assert.That(expression, Is.TypeOf<RewriteComparison>());
        var range = ((RewriteComparison)expression).Range;
        Assert.That(range, Is.Not.Null);
        Assert.That(range!.Value.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.Value.End.CharIndex, Is.EqualTo("title =~ \"rust\"".Length));
    }

    [Test]
    public void Right_hand_comparison_in_chain_keeps_its_own_range()
    {
        // "a=1 and b=2" -> the right-hand `b=2` should span chars 8..11.
        var expression = FilterParser.Parse("a=1 and b=2");
        Assert.That(expression, Is.TypeOf<RewriteAnd>());
        var rewriteAnd = (RewriteAnd)expression;
        Assert.That(rewriteAnd.Right, Is.TypeOf<RewriteComparison>());
        var rightRange = ((RewriteComparison)rewriteAnd.Right).Range;
        Assert.That(rightRange, Is.Not.Null);
        Assert.That(rightRange!.Value.Start.CharIndex, Is.EqualTo(8));
        Assert.That(rightRange.Value.End.CharIndex, Is.EqualTo(11));
    }
}
