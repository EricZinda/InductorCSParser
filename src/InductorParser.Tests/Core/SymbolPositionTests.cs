using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.UnicodeExamples;

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
        var rule = Grapheme('a').Preserve();
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
        // AllOf(Grapheme, Grapheme, Grapheme) wrapped as a named composite. Its
        // range should run from the first 'a' to one past the last 'c'.
        // Preserve each Grapheme explicitly: Grapheme defaults to Delete, and
        // a composite whose leaves are all Delete-flattened has no
        // surviving text to report a range over.
        var rule = AllOf(Grapheme('a').Preserve(), Grapheme('b').Preserve(), Grapheme('c').Preserve())
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

        // Start: family emoji = 8 chars / 1 grapheme.
        Assert.That(range.Start.CharIndex, Is.EqualTo(8));
        Assert.That(range.Start.GraphemeIndex, Is.EqualTo(1));
        Assert.That(range.Start.Line, Is.EqualTo(0));
        Assert.That(range.Start.Column, Is.EqualTo(8));

        // End: family emoji + "ab" = 10 chars / 3 graphemes.
        Assert.That(range.End.CharIndex, Is.EqualTo(10));
        Assert.That(range.End.GraphemeIndex, Is.EqualTo(3));
    }

    [Test]
    public void ErrorPosition_returns_null_on_success()
    {
        var rule = Grapheme('a').Preserve();
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.True);
        Assert.That(result.ErrorPosition, Is.Null);
    }

    [Test]
    public void ErrorPosition_returns_struct_with_all_units_on_failure()
    {
        // "ab" with grammar expecting just 'a' followed by Eof fails at
        // offset 1.
        var rule = AllOf(Grapheme('a'), Eof());
        var result = rule.Parse("ab");

        Assert.That(result.Success, Is.False);
        var position = result.ErrorPosition;
        Assert.That(position, Is.Not.Null);
        Assert.That(position!.Value.CharIndex, Is.EqualTo(1));
        Assert.That(position.Value.GraphemeIndex, Is.EqualTo(1));
        Assert.That(position.Value.Line, Is.EqualTo(0));
        Assert.That(position.Value.Column, Is.EqualTo(1));
    }

    [Test]
    public void Composite_with_leading_deleted_children_starts_at_first_preserved_leaf()
    {
        // The "ab" prefix is matched but Delete-flattened, so it never
        // enters the tree. The named composite's SourceRange.Start must
        // come from the first PRESERVED leaf ("cd" at offset 2), not
        // from the AllOf's start position. If a future change tried to
        // store "I consumed from offset 0" on the composite directly,
        // this test would catch it.
        var rule = AllOf(Literal("ab"), Literal("cd").Preserve()).As("composite").Preserve();
        var result = rule.Parse("abcd");

        Assert.That(result.Success, Is.True);
        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(2));
        Assert.That(range.End.CharIndex, Is.EqualTo(4));
    }

    [Test]
    public void Composite_with_trailing_deleted_children_ends_at_last_preserved_leaf()
    {
        // Mirror of the leading-delete case: trailing "cd" is consumed
        // but Delete-flattened, so the composite's End comes from the
        // last preserved leaf, not from where the AllOf finished.
        var rule = AllOf(Literal("ab").Preserve(), Literal("cd")).As("composite").Preserve();
        var result = rule.Parse("abcd");

        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(2));
    }

    [Test]
    public void Composite_with_deleted_middle_spans_first_to_last_preserved_leaf()
    {
        // The "cd" middle is consumed but Delete-flattened. The
        // leftmost-leaf-to-rightmost-leaf contract means the gap is
        // INCLUDED in End - Start: the composite reports a range of 6
        // chars even though only 4 are physically in the tree.
        var rule = AllOf(
            Literal("ab").Preserve(),
            Literal("cd"),
            Literal("ef").Preserve()).As("composite").Preserve();
        var result = rule.Parse("abcdef");

        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(6));
    }

    [Test]
    public void Composite_with_only_deleted_children_has_null_SourceRange()
    {
        // Every child is Delete-flattened. The composite ends up with
        // an empty children list even though the AllOf consumed 3 chars,
        // so SourceRange returns null. This pins the documented "no
        // associated text => null" behavior.
        var rule = AllOf(Literal("a"), Literal("b"), Literal("c")).As("composite").Preserve();
        var result = rule.Parse("abc");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Tree!.SourceRange, Is.Null);
    }

    [Test]
    public void Optional_that_matched_zero_times_has_null_SourceRange()
    {
        // Optional is preserved but its inner didn't match, so the
        // composite has no children. SourceRange returns null because
        // there's no leaf text to span.
        var optional = Optional(Literal("X").Preserve()).As("opt").Preserve();
        var rule = AllOf(optional, Literal("Y").Preserve()).Preserve();
        var result = rule.Parse("Y");

        Assert.That(result.Success, Is.True);
        var optionalSymbol = result.Tree!.Find(optional);
        Assert.That(optionalSymbol, Is.Not.Null);
        Assert.That(optionalSymbol!.SourceRange, Is.Null);
    }

    [Test]
    public void Peek_preserved_has_null_SourceRange_because_it_consumes_nothing()
    {
        // Peek matched but consumed no input. Its preserved Symbol has
        // an empty children list, so SourceRange returns null. This
        // verifies the zero-width predicate path doesn't accidentally
        // pick up a phantom range from the lookahead.
        var peek = Peek(Literal("X")).As("peek").Preserve();
        var rule = AllOf(peek, Literal("X").Preserve()).Preserve();
        var result = rule.Parse("X");

        Assert.That(result.Success, Is.True);
        var peekSymbol = result.Tree!.Find(peek);
        Assert.That(peekSymbol, Is.Not.Null);
        Assert.That(peekSymbol!.SourceRange, Is.Null);
    }

    [Test]
    public void Empty_leaf_in_middle_does_not_truncate_composite_range()
    {
        // ScanUntil with the stopper already at the cursor position
        // produces a zero-width preserved leaf. The IsEmpty check in
        // FindFirstLeafWithText / FindLastLeafWithText should skip it
        // and the composite range should still span "ab" through "cd".
        // If those walkers ever stopped on the empty leaf instead of
        // recursing past it, this test would catch it.
        var rule = AllOf(
            Literal("a").Preserve(),
            ScanUntil(TokenSet.Runes("b")).Preserve(),
            Literal("b").Preserve()).As("composite").Preserve();
        var result = rule.Parse("ab");

        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(2));
    }

    [Test]
    public void Trailing_empty_leaf_does_not_extend_composite_range()
    {
        // ScanUntil at end-of-input returns a zero-width leaf. The
        // composite's End must come from the preceding non-empty "ab"
        // leaf, not from the empty trailing leaf's offset+length (which
        // would happen to give the same answer here, but the test pins
        // that lastLength==0 doesn't accidentally produce End at the
        // empty leaf's position).
        var rule = AllOf(
            Literal("ab").Preserve(),
            ScanUntil(TokenSet.Runes("z")).Preserve()).As("composite").Preserve();
        var result = rule.Parse("ab");

        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(2));
    }

    [Test]
    public void Range_spans_multiple_lines_when_match_crosses_newline()
    {
        // Literal whose body straddles a "\n". Start sits on line 0
        // column 0, End sits on line 1 column 2. Verifies that the
        // line/column derivation for the End position correctly counts
        // across the newline.
        var rule = Literal("ab\ncd").Preserve();
        var result = rule.Parse("ab\ncd");

        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.Start.Line, Is.EqualTo(0));
        Assert.That(range.Start.Column, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(5));
        Assert.That(range.End.Line, Is.EqualTo(1));
        Assert.That(range.End.Column, Is.EqualTo(2));
    }

    [Test]
    public void Range_treats_CRLF_as_one_line_break()
    {
        // CRLF is a single LSP line terminator and a single UAX #29
        // grapheme. A literal that spans it should bump End.Line by
        // exactly 1 and End.GraphemeIndex by 5 (a, b, \r\n, c, d).
        var rule = Literal("ab\r\ncd").Preserve();
        var result = rule.Parse("ab\r\ncd");

        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.End.CharIndex, Is.EqualTo(6));
        Assert.That(range.End.Line, Is.EqualTo(1));
        Assert.That(range.End.Column, Is.EqualTo(2));
        Assert.That(range.End.GraphemeIndex, Is.EqualTo(5));
    }

    [Test]
    public void Range_End_at_input_end_after_lone_CR_reports_next_line()
    {
        // A trailing lone "\r" (not followed by "\n") is its own line
        // terminator. End sits at input.Length after the \r, which
        // should be reported as line 1 column 0.
        var rule = Literal("ab\r").Preserve();
        var result = rule.Parse("ab\r");

        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.End.CharIndex, Is.EqualTo(3));
        Assert.That(range.End.Line, Is.EqualTo(1));
        Assert.That(range.End.Column, Is.EqualTo(0));
    }

    [Test]
    public void OneOrMore_range_spans_all_matched_iterations()
    {
        // Three matches of "abc". The composite range should cover the
        // first 'a' through the last 'c', not just the most recent
        // iteration. Catches a bug where a buggy implementation might
        // overwrite Start/End on each iteration instead of stitching
        // first to last.
        var rule = OneOrMore(Literal("abc").Preserve()).As("repeat").Preserve();
        var result = rule.Parse("abcabcabc");

        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(9));
        Assert.That(range.End.Column, Is.EqualTo(9));
    }

    [Test]
    public void Identifier_range_diverges_grapheme_from_rune_for_combining_mark()
    {
        // "réx" is r + (e + combining acute) + x. Three graphemes,
        // four runes, four UTF-16 chars. With normalization disabled
        // (default NFC would otherwise compose e+combining into the
        // precomposed "e-acute" and collapse to 3 chars), Identifier
        // consumes all of it via WithinGrapheme leaves, and its
        // composite range should report End.CharIndex ==
        // End.RuneIndex == 4 but End.GraphemeIndex == 3. Verifies the
        // stitching across multi-rune-grapheme leaves.
        const string Input = "re" + CombiningAcuteText + "x";
        var rule = Identifier();
        var result = rule.Parse(Input, new ParseOptions { NormalizeInput = null });

        Assert.That(result.Success, Is.True);
        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(4));
        Assert.That(range.End.GraphemeIndex, Is.EqualTo(3));
    }

    [Test]
    public void Range_for_two_supplementary_emojis_diverges_char_from_grapheme()
    {
        // Two waving-hand emojis (each 2 chars / 1 grapheme). End
        // should be at char 4, grapheme 2. Catches a bug where the
        // End walks treat surrogate pairs as two graphemes.
        const string Input = WavingHandGrapheme + WavingHandGrapheme;
        var rule = Literal(Input).Preserve();
        var result = rule.Parse(Input);

        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.End.CharIndex, Is.EqualTo(4));
        Assert.That(range.End.GraphemeIndex, Is.EqualTo(2));
    }

    [Test]
    public void Find_on_inner_composite_returns_inner_range_not_outer()
    {
        // Two named composites, one nested in the other. The outer's
        // range should span the whole match while the inner's range
        // should span only its own children. Verifies that Find +
        // SourceRange together don't leak the outer range when the
        // caller asks for the inner.
        var inner = AllOf(Grapheme('x').Preserve(), Grapheme('y').Preserve()).As("inner").Preserve();
        var outer = AllOf(Literal("ab").Preserve(), inner, Literal("cd").Preserve()).As("outer").Preserve();
        var result = outer.Parse("abxycd");

        var outerRange = result.Tree!.SourceRange!.Value;
        Assert.That(outerRange.Start.CharIndex, Is.EqualTo(0));
        Assert.That(outerRange.End.CharIndex, Is.EqualTo(6));

        var innerSymbol = result.Tree.Find(inner);
        var innerRange = innerSymbol!.SourceRange!.Value;
        Assert.That(innerRange.Start.CharIndex, Is.EqualTo(2));
        Assert.That(innerRange.End.CharIndex, Is.EqualTo(4));
    }

    [Test]
    public void Range_End_at_input_end_equals_input_length_in_all_units()
    {
        // The composite ends exactly at input.Length. CharIndex /
        // GraphemeIndex should both clamp / report the same value,
        // and Column should be the full chars-on-this-line count (no
        // off-by-one at the boundary).
        var rule = AllOf(Literal("xx").Preserve(), Literal("yy").Preserve()).As("composite").Preserve();
        var result = rule.Parse("xxyy");

        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.End.CharIndex, Is.EqualTo(4));
        Assert.That(range.End.GraphemeIndex, Is.EqualTo(4));
        Assert.That(range.End.Line, Is.EqualTo(0));
        Assert.That(range.End.Column, Is.EqualTo(4));
    }

    [Test]
    public void Nested_flatten_composite_with_all_deleted_children_does_not_contribute_to_range()
    {
        // The first AllOf is Flatten with two Delete children, so it
        // contributes nothing to the outer's children list. The
        // composite range should start at the second AllOf's first
        // preserved leaf, not at offset 0 where the deleted prefix
        // began. Locks in that flattened-but-empty subtrees never
        // add phantom leaves.
        var deletedPrefix = AllOf(Literal("aa"), Literal("bb"));
        var rule = AllOf(deletedPrefix, Literal("cc").Preserve()).As("composite").Preserve();
        var result = rule.Parse("aabbcc");

        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(4));
        Assert.That(range.End.CharIndex, Is.EqualTo(6));
    }

    [Test]
    public void Range_starts_on_later_line_when_named_rule_begins_after_newline()
    {
        // The named rule's first preserved leaf sits on line 2. Verify
        // the Start position carries the correct line and a
        // line-relative column even though the outer parse started at
        // line 0.
        var target = Literal("hi").As("target").Preserve();
        var rule = AllOf(Literal("first\nsecond\n  "), target);
        var result = rule.Parse("first\nsecond\n  hi");

        var symbol = result.Tree!.Find(target);
        var range = symbol!.SourceRange!.Value;
        // "first\n" = 6 chars, "second\n" = 7 chars, "  " = 2 chars.
        // target starts at char 15.
        Assert.That(range.Start.CharIndex, Is.EqualTo(15));
        Assert.That(range.Start.Line, Is.EqualTo(2));
        Assert.That(range.Start.Column, Is.EqualTo(2));
        Assert.That(range.End.Line, Is.EqualTo(2));
        Assert.That(range.End.Column, Is.EqualTo(4));
    }
}
