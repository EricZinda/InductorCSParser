using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Tests for Symbol.SourceRange. The conversion math (char to rune /
// grapheme / line / column) is shared with ParseResult and covered by
// ErrorPositionTests; this fixture verifies that a Symbol's start and
// end char indices are correctly recovered from its leaves' captured
// memory and stitched into a SourceRange.
[TestFixture]
public class SymbolPositionTests
{
    [Test]
    public void Leaf_token_has_range_pointing_at_its_char_in_input()
    {
        var rule = Token('a').Preserve();
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.True);
        var range = result.Tree!.SourceRange;
        Assert.That(range, Is.Not.Null);
        Assert.That(range!.Value.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.Value.End.CharIndex, Is.EqualTo(1));
    }

    [Test]
    public void Leaf_literal_range_spans_the_matched_text()
    {
        var rule = Literal("hello").Preserve();
        var result = rule.Parse("hello");

        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(5));
    }

    [Test]
    public void Composite_range_spans_leftmost_leaf_to_rightmost_leaf()
    {
        // AllOf(Token, Token, Token) wrapped as a named composite. Its
        // range should run from the first 'a' to one past the last 'c'.
        // Preserve each Token explicitly: Token defaults to Delete, and
        // a composite whose leaves are all Delete-flattened has no
        // surviving text to report a range over.
        var rule = AllOf(Token('a').Preserve(), Token('b').Preserve(), Token('c').Preserve())
            .As("triple").Preserve();
        var result = rule.Parse("abc");

        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(3));
    }

    [Test]
    public void Composite_with_leading_input_starts_at_first_consumed_char()
    {
        // The named subrule should report a range starting at offset 2,
        // even though the outer parse started at 0.
        var inner = Literal("XYZ").As("inner").Preserve();
        var rule = AllOf(Literal("ab"), inner);
        var result = rule.Parse("abXYZ");

        var innerSymbol = result.Tree!.Find(inner);
        Assert.That(innerSymbol, Is.Not.Null);
        var range = innerSymbol!.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(2));
        Assert.That(range.End.CharIndex, Is.EqualTo(5));
    }

    [Test]
    public void Range_end_minus_start_equals_matched_char_length()
    {
        var rule = Literal("hello").Preserve();
        var result = rule.Parse("hello");

        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.End.CharIndex - range.Start.CharIndex, Is.EqualTo(5));
    }

    [Test]
    public void Range_reports_zero_based_line_and_column_on_multi_line_input()
    {
        // Input has the matched literal on line 2 (zero-based).
        var literal = Literal("target").As("target").Preserve();
        var rule = AllOf(
            Literal("first\n"),
            Literal("second\n"),
            literal,
            Literal(" trailing"));
        var result = rule.Parse("first\nsecond\ntarget trailing");

        var symbol = result.Tree!.Find(literal);
        Assert.That(symbol, Is.Not.Null);
        var range = symbol!.SourceRange!.Value;

        // "first\n" = 6 chars, "second\n" = 7 chars. So target starts
        // at char 13, line 2, column 0.
        Assert.That(range.Start.CharIndex, Is.EqualTo(13));
        Assert.That(range.Start.Line, Is.EqualTo(2));
        Assert.That(range.Start.Column, Is.EqualTo(0));

        // End is at char 19 (13 + 6 for "target"), still on line 2.
        Assert.That(range.End.CharIndex, Is.EqualTo(19));
        Assert.That(range.End.Line, Is.EqualTo(2));
        Assert.That(range.End.Column, Is.EqualTo(6));
    }

    [Test]
    public void Range_indices_diverge_for_multi_rune_grapheme_input()
    {
        // Input: family emoji (8 chars / 5 runes / 1 grapheme) then "ab".
        // The "ab" literal should report indices that account for the
        // family emoji preceding it.
        // Man + ZWJ + Woman + ZWJ + Girl. 8 chars, 5 runes, 1 grapheme.
        const string FamilyEmoji = "\U0001F468‍\U0001F469‍\U0001F467";
        var target = Literal("ab").As("target").Preserve();
        var rule = AllOf(Literal(FamilyEmoji), target);
        var result = rule.Parse(FamilyEmoji + "ab");

        var symbol = result.Tree!.Find(target);
        var range = symbol!.SourceRange!.Value;

        // Start: family emoji = 8 chars / 5 runes / 1 grapheme.
        Assert.That(range.Start.CharIndex, Is.EqualTo(8));
        Assert.That(range.Start.RuneIndex, Is.EqualTo(5));
        Assert.That(range.Start.GraphemeIndex, Is.EqualTo(1));
        Assert.That(range.Start.Line, Is.EqualTo(0));
        Assert.That(range.Start.Column, Is.EqualTo(8));

        // End: family emoji + "ab" = 10 chars / 7 runes / 3 graphemes.
        Assert.That(range.End.CharIndex, Is.EqualTo(10));
        Assert.That(range.End.RuneIndex, Is.EqualTo(7));
        Assert.That(range.End.GraphemeIndex, Is.EqualTo(3));
    }

    [Test]
    public void ErrorPosition_returns_null_on_success()
    {
        var rule = Token('a').Preserve();
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.True);
        Assert.That(result.ErrorPosition, Is.Null);
    }

    [Test]
    public void ErrorPosition_returns_struct_with_all_units_on_failure()
    {
        // "ab" with grammar expecting just 'a' followed by Eof fails at
        // offset 1.
        var rule = AllOf(Token('a'), Eof());
        var result = rule.Parse("ab");

        Assert.That(result.Success, Is.False);
        var position = result.ErrorPosition;
        Assert.That(position, Is.Not.Null);
        Assert.That(position!.Value.CharIndex, Is.EqualTo(1));
        Assert.That(position.Value.RuneIndex, Is.EqualTo(1));
        Assert.That(position.Value.GraphemeIndex, Is.EqualTo(1));
        Assert.That(position.Value.Line, Is.EqualTo(0));
        Assert.That(position.Value.Column, Is.EqualTo(1));
    }
}
