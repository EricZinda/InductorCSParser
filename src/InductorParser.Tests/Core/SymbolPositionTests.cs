using System.Text;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.UnicodeExamples;

namespace InductorParser.Tests;

// Tests for ParseResult.SourceRangeOf. The conversion math (char to
// rune / grapheme / line / column) is shared with ParseResult.Error*
// and covered by ErrorPositionTests; this fixture verifies that a
// Symbol's start and end char indices are correctly recovered from
// its leaves' captured memory and stitched into a SourceRange in the
// caller's original-input coordinates.
[TestFixture]
public class SymbolPositionTests
{
    [Test]
    public void Leaf_token_has_range_pointing_at_its_char_in_input()
    {
        var rule = Token('a').Preserve();
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.True);
        var range = result.SourceRangeOf(result.Tree!);
        Assert.That(range, Is.Not.Null);
        Assert.That(range!.Value.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.Value.End.CharIndex, Is.EqualTo(1));
    }

    [Test]
    public void Leaf_literal_range_spans_the_matched_text()
    {
        var rule = Literal("hello").Preserve();
        var result = rule.Parse("hello");

        var range = result.SourceRangeOf(result.Tree!)!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(5));
    }

    [Test]
    public void Composite_range_spans_leftmost_leaf_to_rightmost_leaf()
    {
        // And(Token, Token, Token) wrapped as a named composite. Its
        // range should run from the first 'a' to one past the last 'c'.
        // Preserve each Token explicitly: Token defaults to Delete, and
        // a composite whose leaves are all Delete-flattened has no
        // surviving text to report a range over.
        var rule = And(Token('a').Preserve(), Token('b').Preserve(), Token('c').Preserve())
            .As("triple").Preserve();
        var result = rule.Parse("abc");

        var range = result.SourceRangeOf(result.Tree!)!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(3));
    }

    [Test]
    public void Composite_with_leading_input_starts_at_first_consumed_char()
    {
        // The named subrule should report a range starting at offset 2,
        // even though the outer parse started at 0.
        var inner = Literal("XYZ").As("inner").Preserve();
        var rule = And(Literal("ab"), inner);
        var result = rule.Parse("abXYZ");

        var innerSymbol = result.Tree!.Find(inner);
        Assert.That(innerSymbol, Is.Not.Null);
        var range = result.SourceRangeOf(innerSymbol!)!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(2));
        Assert.That(range.End.CharIndex, Is.EqualTo(5));
    }

    [Test]
    public void Range_end_minus_start_equals_matched_char_length()
    {
        var rule = Literal("hello").Preserve();
        var result = rule.Parse("hello");

        var range = result.SourceRangeOf(result.Tree!)!.Value;
        Assert.That(range.End.CharIndex - range.Start.CharIndex, Is.EqualTo(5));
    }

    [Test]
    public void Range_reports_zero_based_line_and_column_on_multi_line_input()
    {
        // Input has the matched literal on line 2 (zero-based).
        var literal = Literal("target").As("target").Preserve();
        var rule = And(
            Literal("first\n"),
            Literal("second\n"),
            literal,
            Literal(" trailing"));
        var result = rule.Parse("first\nsecond\ntarget trailing");

        var symbol = result.Tree!.Find(literal);
        Assert.That(symbol, Is.Not.Null);
        var range = result.SourceRangeOf(symbol!)!.Value;

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
        var rule = And(Literal(FamilyEmoji), target);
        var result = rule.Parse(FamilyEmoji + "ab");

        var symbol = result.Tree!.Find(target);
        var range = result.SourceRangeOf(symbol!)!.Value;

        // Start: family emoji = 8 chars / 1 grapheme.
        Assert.That(range.Start.CharIndex, Is.EqualTo(8));
        Assert.That(range.Start.TokenIndex, Is.EqualTo(1));
        Assert.That(range.Start.Line, Is.EqualTo(0));
        Assert.That(range.Start.Column, Is.EqualTo(8));

        // End: family emoji + "ab" = 10 chars / 3 graphemes.
        Assert.That(range.End.CharIndex, Is.EqualTo(10));
        Assert.That(range.End.TokenIndex, Is.EqualTo(3));
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
        var rule = And(Token('a'), Eof());
        var result = rule.Parse("ab");

        Assert.That(result.Success, Is.False);
        var position = result.ErrorPosition;
        Assert.That(position, Is.Not.Null);
        Assert.That(position!.Value.CharIndex, Is.EqualTo(1));
        Assert.That(position.Value.TokenIndex, Is.EqualTo(1));
        Assert.That(position.Value.Line, Is.EqualTo(0));
        Assert.That(position.Value.Column, Is.EqualTo(1));
    }

    [Test]
    public void Composite_with_leading_deleted_children_starts_at_first_preserved_leaf()
    {
        // The "ab" prefix is matched but Delete-flattened, so it never
        // enters the tree. The named composite's SourceRange.Start must
        // come from the first PRESERVED leaf ("cd" at offset 2), not
        // from the And's start position. If a future change tried to
        // store "I consumed from offset 0" on the composite directly,
        // this test would catch it.
        var rule = And(Literal("ab"), Literal("cd").Preserve()).As("composite").Preserve();
        var result = rule.Parse("abcd");

        Assert.That(result.Success, Is.True);
        var range = result.SourceRangeOf(result.Tree!)!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(2));
        Assert.That(range.End.CharIndex, Is.EqualTo(4));
    }

    [Test]
    public void Composite_with_trailing_deleted_children_ends_at_last_preserved_leaf()
    {
        // Mirror of the leading-delete case: trailing "cd" is consumed
        // but Delete-flattened, so the composite's End comes from the
        // last preserved leaf, not from where the And finished.
        var rule = And(Literal("ab").Preserve(), Literal("cd")).As("composite").Preserve();
        var result = rule.Parse("abcd");

        var range = result.SourceRangeOf(result.Tree!)!.Value;
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
        var rule = And(
            Literal("ab").Preserve(),
            Literal("cd"),
            Literal("ef").Preserve()).As("composite").Preserve();
        var result = rule.Parse("abcdef");

        var range = result.SourceRangeOf(result.Tree!)!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(6));
    }

    [Test]
    public void Composite_with_only_deleted_children_has_null_SourceRange()
    {
        // Every child is Delete-flattened. The composite ends up with
        // an empty children list even though the And consumed 3 chars,
        // so SourceRange returns null. This pins the documented "no
        // associated text => null" behavior.
        var rule = And(Literal("a"), Literal("b"), Literal("c")).As("composite").Preserve();
        var result = rule.Parse("abc");

        Assert.That(result.Success, Is.True);
        Assert.That(result.SourceRangeOf(result.Tree!), Is.Null);
    }

    [Test]
    public void Optional_that_matched_zero_times_has_null_SourceRange()
    {
        // Optional is preserved but its inner didn't match, so the
        // composite has no children. SourceRange returns null because
        // there's no leaf text to span.
        var optional = Optional(Literal("X").Preserve()).As("opt").Preserve();
        var rule = And(optional, Literal("Y").Preserve()).Preserve();
        var result = rule.Parse("Y");

        Assert.That(result.Success, Is.True);
        var optionalSymbol = result.Tree!.Find(optional);
        Assert.That(optionalSymbol, Is.Not.Null);
        Assert.That(result.SourceRangeOf(optionalSymbol!), Is.Null);
    }

    [Test]
    public void Peek_preserved_has_null_SourceRange_because_it_consumes_nothing()
    {
        // Peek matched but consumed no input. Its preserved Symbol has
        // an empty children list, so SourceRange returns null. This
        // verifies the zero-width predicate path doesn't accidentally
        // pick up a phantom range from the lookahead.
        var peek = Peek(Literal("X")).As("peek").Preserve();
        var rule = And(peek, Literal("X").Preserve()).Preserve();
        var result = rule.Parse("X");

        Assert.That(result.Success, Is.True);
        var peekSymbol = result.Tree!.Find(peek);
        Assert.That(peekSymbol, Is.Not.Null);
        Assert.That(result.SourceRangeOf(peekSymbol!), Is.Null);
    }

    [Test]
    public void Standalone_empty_leaf_reports_zero_width_SourceRange_at_match_position()
    {
        // ScanUntil whose stopper is at the cursor matches a zero-width body
        // and emits a leaf Symbol with empty memory. The leaf still has a
        // well-defined position in the input (the offset where the stopper
        // sat), recoverable via MemoryMarshal.TryGetString on the leaf's
        // memory. SourceRange should report a zero-width range at that
        // position, not null.
        //
        // AllowTrailingInput so the parse succeeds with the empty body
        // even though 'X' is still unconsumed.
        var rule = ScanUntil(TokenSet.Runes("X")).Preserve();
        var options = new ParseOptions { AllowTrailingInput = true };
        var result = rule.Parse("X", options);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Tree, Is.Not.Null);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(""));

        var range = result.SourceRangeOf(result.Tree!);
        Assert.That(range, Is.Not.Null,
            "standalone empty leaf should still report a position");
        Assert.That(range!.Value.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.Value.End.CharIndex, Is.EqualTo(0));
    }

    [Test]
    public void Empty_string_body_in_composite_reports_position_between_delimiters()
    {
        // The user-visible canonical case: a JSON-style string grammar with
        // ScanUntil for the body. An empty input "" should let the user
        // highlight the position between the quotes (offset 1, zero width).
        // The body Symbol's SourceRange should report a zero-width range
        // at offset 1 — consumers that highlight bodies or read offsets
        // need a position even when the body is empty.
        var body = ScanUntil(TokenSet.Runes("\"")).As("body").Preserve();
        var rule = And(Token('"'), body, Token('"'));
        var result = rule.Parse("\"\"");

        Assert.That(result.Success, Is.True);
        var bodySymbol = result.Tree!.Find(body);
        Assert.That(bodySymbol, Is.Not.Null);
        Assert.That(bodySymbol!.ToString(), Is.EqualTo(""));

        var range = result.SourceRangeOf(bodySymbol);
        Assert.That(range, Is.Not.Null,
            "empty body should report its position between the quotes");
        Assert.That(range!.Value.Start.CharIndex, Is.EqualTo(1));
        Assert.That(range.Value.End.CharIndex, Is.EqualTo(1));
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
        var rule = And(
            Literal("a").Preserve(),
            ScanUntil(TokenSet.Runes("b")).Preserve(),
            Literal("b").Preserve()).As("composite").Preserve();
        var result = rule.Parse("ab");

        var range = result.SourceRangeOf(result.Tree!)!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(2));
    }

    [Test]
    public void Leading_empty_leaf_does_not_break_composite_range()
    {
        // Mirror of Empty_leaf_in_middle / Trailing_empty_leaf: pin that
        // a LEADING empty leaf doesn't change the range either. ScanUntil
        // with the stopper at the cursor produces an empty leaf at offset
        // 0; the following Literal matches at offset 0. Children:
        // [empty leaf at 0, "a" leaf at 0-1]. Range should be (0, 1).
        var rule = And(
            ScanUntil(TokenSet.Runes("a")).Preserve(),
            Literal("a").Preserve()).As("composite").Preserve();
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.True);
        var range = result.SourceRangeOf(result.Tree!)!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(1));
    }

    [Test]
    public void Composite_with_only_empty_leaves_reports_zero_width_range_at_their_position()
    {
        // Two ScanUntils whose stopper is at the cursor each produce an
        // empty leaf at the same offset. The composite's children list is
        // [empty, empty]. The Token('a') that lets the parse advance is
        // Delete-flattened so it never enters the tree. SourceRange
        // should report a zero-width range at the offset both empty
        // leaves sit at, not null.
        var rule = And(
            ScanUntil(TokenSet.Runes("a")).Preserve(),
            ScanUntil(TokenSet.Runes("a")).Preserve(),
            Token('a')).As("composite").Preserve();
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.True);
        var range = result.SourceRangeOf(result.Tree!);
        Assert.That(range, Is.Not.Null,
            "composite with only empty leaves should still report a position");
        Assert.That(range!.Value.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.Value.End.CharIndex, Is.EqualTo(0));
    }

    [Test]
    public void Nested_composite_whose_only_leaf_is_empty_reports_zero_width_range()
    {
        // Outer composite -> inner composite -> empty leaf. The recursive
        // walker should descend through the inner composite and find the
        // empty leaf at the bottom, then report a zero-width range at its
        // position. Verifies that the leaf-finding walk traverses arbitrary
        // depth rather than only looking at the immediate children.
        var inner = And(ScanUntil(TokenSet.Runes("a")).Preserve()).As("inner").Preserve();
        var rule = And(inner, Token('a')).As("outer").Preserve();
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.True);

        var outerRange = result.SourceRangeOf(result.Tree!);
        Assert.That(outerRange, Is.Not.Null,
            "outer composite should walk through inner to find the empty leaf");
        Assert.That(outerRange!.Value.Start.CharIndex, Is.EqualTo(0));
        Assert.That(outerRange.Value.End.CharIndex, Is.EqualTo(0));

        var innerSymbol = result.Tree!.Find(inner);
        Assert.That(innerSymbol, Is.Not.Null);
        var innerRange = result.SourceRangeOf(innerSymbol!);
        Assert.That(innerRange, Is.Not.Null,
            "inner composite should report the empty leaf's position");
        Assert.That(innerRange!.Value.Start.CharIndex, Is.EqualTo(0));
        Assert.That(innerRange.Value.End.CharIndex, Is.EqualTo(0));
    }

    [Test]
    public void Trailing_empty_leaf_does_not_extend_composite_range()
    {
        // ScanUntil at end-of-input returns a zero-width leaf. The
        // composite's End must come from the preceding non-empty "ab"
        // leaf, not from the empty trailing leaf's offset+length (which
        // would happen to give the same answer here, but the test pins
        // that lastLength==0 doesn't accidentally produce End at the
        // empty leaf's position). eofIsTerminator: true so the inner
        // scan succeeds with an empty leaf instead of failing at EOF
        // (the test is about the composite range, not the strict-stopper
        // check).
        var rule = And(
            Literal("ab").Preserve(),
            ScanUntil(TokenSet.Runes("z"), eofIsTerminator: true).Preserve()).As("composite").Preserve();
        var result = rule.Parse("ab");

        var range = result.SourceRangeOf(result.Tree!)!.Value;
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

        var range = result.SourceRangeOf(result.Tree!)!.Value;
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
        // exactly 1 and End.TokenIndex by 5 (a, b, \r\n, c, d).
        var rule = Literal("ab\r\ncd").Preserve();
        var result = rule.Parse("ab\r\ncd");

        var range = result.SourceRangeOf(result.Tree!)!.Value;
        Assert.That(range.End.CharIndex, Is.EqualTo(6));
        Assert.That(range.End.Line, Is.EqualTo(1));
        Assert.That(range.End.Column, Is.EqualTo(2));
        Assert.That(range.End.TokenIndex, Is.EqualTo(5));
    }

    [Test]
    public void Range_End_at_input_end_after_lone_CR_reports_next_line()
    {
        // A trailing lone "\r" (not followed by "\n") is its own line
        // terminator. End sits at input.Length after the \r, which
        // should be reported as line 1 column 0.
        var rule = Literal("ab\r").Preserve();
        var result = rule.Parse("ab\r");

        var range = result.SourceRangeOf(result.Tree!)!.Value;
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

        var range = result.SourceRangeOf(result.Tree!)!.Value;
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
        // consumes all of it via WithinToken leaves, and its
        // composite range should report End.CharIndex ==
        // End.RuneIndex == 4 but End.TokenIndex == 3. Verifies the
        // stitching across multi-rune-grapheme leaves.
        const string Input = "re" + CombiningAcuteText + "x";
        var rule = Identifier();
        rule.Compile(null);
        var result = rule.Parse(Input);

        Assert.That(result.Success, Is.True);
        var range = result.SourceRangeOf(result.Tree!)!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(4));
        Assert.That(range.End.TokenIndex, Is.EqualTo(3));
    }

    [Test]
    public void Range_for_two_supplementary_emojis_diverges_char_from_Token()
    {
        // Two waving-hand emojis (each 2 chars / 1 grapheme). End
        // should be at char 4, grapheme 2. Catches a bug where the
        // End walks treat surrogate pairs as two graphemes.
        const string Input = WavingHandGrapheme + WavingHandGrapheme;
        var rule = Literal(Input).Preserve();
        var result = rule.Parse(Input);

        var range = result.SourceRangeOf(result.Tree!)!.Value;
        Assert.That(range.End.CharIndex, Is.EqualTo(4));
        Assert.That(range.End.TokenIndex, Is.EqualTo(2));
    }

    [Test]
    public void Find_on_inner_composite_returns_inner_range_not_outer()
    {
        // Two named composites, one nested in the other. The outer's
        // range should span the whole match while the inner's range
        // should span only its own children. Verifies that Find +
        // SourceRange together don't leak the outer range when the
        // caller asks for the inner.
        var inner = And(Token('x').Preserve(), Token('y').Preserve()).As("inner").Preserve();
        var outer = And(Literal("ab").Preserve(), inner, Literal("cd").Preserve()).As("outer").Preserve();
        var result = outer.Parse("abxycd");

        var outerRange = result.SourceRangeOf(result.Tree!)!.Value;
        Assert.That(outerRange.Start.CharIndex, Is.EqualTo(0));
        Assert.That(outerRange.End.CharIndex, Is.EqualTo(6));

        var innerSymbol = result.Tree!.Find(inner);
        var innerRange = result.SourceRangeOf(innerSymbol!)!.Value;
        Assert.That(innerRange.Start.CharIndex, Is.EqualTo(2));
        Assert.That(innerRange.End.CharIndex, Is.EqualTo(4));
    }

    [Test]
    public void Range_End_at_input_end_equals_input_length_in_all_units()
    {
        // The composite ends exactly at input.Length. CharIndex /
        // TokenIndex should both clamp / report the same value,
        // and Column should be the full chars-on-this-line count (no
        // off-by-one at the boundary).
        var rule = And(Literal("xx").Preserve(), Literal("yy").Preserve()).As("composite").Preserve();
        var result = rule.Parse("xxyy");

        var range = result.SourceRangeOf(result.Tree!)!.Value;
        Assert.That(range.End.CharIndex, Is.EqualTo(4));
        Assert.That(range.End.TokenIndex, Is.EqualTo(4));
        Assert.That(range.End.Line, Is.EqualTo(0));
        Assert.That(range.End.Column, Is.EqualTo(4));
    }

    [Test]
    public void Nested_flatten_composite_with_all_deleted_children_does_not_contribute_to_range()
    {
        // The first And is Flatten with two Delete children, so it
        // contributes nothing to the outer's children list. The
        // composite range should start at the second And's first
        // preserved leaf, not at offset 0 where the deleted prefix
        // began. Locks in that flattened-but-empty subtrees never
        // add phantom leaves.
        var deletedPrefix = And(Literal("aa"), Literal("bb"));
        var rule = And(deletedPrefix, Literal("cc").Preserve()).As("composite").Preserve();
        var result = rule.Parse("aabbcc");

        var range = result.SourceRangeOf(result.Tree!)!.Value;
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
        var rule = And(Literal("first\nsecond\n  "), target);
        var result = rule.Parse("first\nsecond\n  hi");

        var symbol = result.Tree!.Find(target);
        var range = result.SourceRangeOf(symbol!)!.Value;
        // "first\n" = 6 chars, "second\n" = 7 chars, "  " = 2 chars.
        // target starts at char 15.
        Assert.That(range.Start.CharIndex, Is.EqualTo(15));
        Assert.That(range.Start.Line, Is.EqualTo(2));
        Assert.That(range.Start.Column, Is.EqualTo(2));
        Assert.That(range.End.Line, Is.EqualTo(2));
        Assert.That(range.End.Column, Is.EqualTo(4));
    }

    // -------------------------------------------------------------
    // Cross-cutting SourceRange-under-normalization tests.
    //
    // Per-rule SourceRange-matrix tests (one [TestCaseSource]
    // parameterized over NormalizationExamples.RowFormPairs per leaf
    // rule) live in each rule's own file under
    // src/InductorParser.Tests/Rules/. See docs/TestArchitecture.md
    // "Per-rule SourceRange-matrix tests live in each rule's own
    // test file."
    //
    // The tests below exercise the ParseResult.SourceRangeOf
    // machinery itself — FindFirstLeaf / FindLastLeaf walker
    // stitching both endpoints, position-unit derivation through the
    // translator, and the FormKC per-grapheme-expansion edge case
    // where one source cluster spawns multiple parseInput leaves —
    // without focusing on any one rule type. Keeping them centralized
    // here means a regression in the translator surfaces in one
    // place rather than scattered across nine per-rule files.
    // -------------------------------------------------------------

    // Decomposed "café" is 5 chars (c, a, f, e, U+0301). Under the
    // default FormC compile, it normalizes to precomposed "café"
    // which is 4 chars. Anything after the prefix sits one char
    // further along in the original input than in parseInput.
    private const string DecomposedCafePrefix = "café";

    // Composite range: leftmost (FindFirstLeaf) and rightmost
    // (FindLastLeaf) leaves both sit after the rewrite, so a fix that
    // translated only one endpoint would slip past the per-leaf tests
    // but break here.
    [Test]
    public void SourceRange_for_composite_under_FormC_translates_both_endpoints()
    {
        string input = DecomposedCafePrefix + "XYZ";
        var first = Token('X').As("first").Preserve();
        var last = Token('Z').As("last").Preserve();
        var composite = And(first, Token('Y').Preserve(), last).As("composite").Preserve();
        var rule = And(Literal("café"), composite);
        var result = rule.Parse(input);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        var range = result.SourceRangeOf(result.Tree!.Find(composite)!)!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(5));
        Assert.That(range.End.CharIndex, Is.EqualTo(8));
    }

    // Position units beyond CharIndex. Column for the FormC + decomposed
    // case differs by one (parseInput is one char shorter than original
    // on the same line). TokenIndex differs only when graphemes are
    // ADDED or REMOVED by normalization — covered by the FormKC ligature
    // test below. Line never differs (canonical / compatibility
    // normalization doesn't add or drop line break characters).
    [Test]
    public void SourceRange_under_FormC_Column_uses_original_input_coords()
    {
        string input = DecomposedCafePrefix + "X";
        var target = Token('X').As("x").Preserve();
        var rule = And(Literal("café"), target);
        var result = rule.Parse(input);

        var range = result.SourceRangeOf(result.Tree!.Find(target)!)!.Value;
        Assert.That(range.Start.Column, Is.EqualTo(5),
            "Column at the X should reflect original-input position (5), not parseInput position (4).");
        Assert.That(range.End.Column, Is.EqualTo(6));
    }

    // FormKC compatibility-form expansion. The ligature U+FB01 is one
    // grapheme in original (1 char) but expands to "fi" (2 chars / 2
    // graphemes) under FormKC. Two parse-time leaves come from one
    // source cluster, both should map back into the original ligature
    // span [0, 1). NormalizedPositionMap's per-grapheme walker maps
    // any parseInput offset INSIDE the rewritten run back to the
    // start of the original cluster, and the offset just PAST the
    // run to one past it; the natural per-endpoint translation gives
    // leaf 'f' [0, 0) (zero-width inside the cluster) and leaf 'i'
    // [0, 1) (full cluster span). 'X' lands cleanly one cluster past
    // the ligature.
    [Test]
    public void SourceRange_under_FormKC_ligature_expansion_maps_two_leaves_into_original_cluster()
    {
        string input = "ﬁX";
        var fLeaf = Token('f').As("f").Preserve();
        var iLeaf = Token('i').As("i").Preserve();
        var xLeaf = Token('X').As("x").Preserve();
        var rule = And(fLeaf, iLeaf, xLeaf).As("root").Preserve();
        rule.Compile(NormalizationForm.FormKC);
        var result = rule.Parse(input);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        var fRange = result.SourceRangeOf(result.Tree!.Find(fLeaf)!)!.Value;
        var iRange = result.SourceRangeOf(result.Tree!.Find(iLeaf)!)!.Value;
        var xRange = result.SourceRangeOf(result.Tree!.Find(xLeaf)!)!.Value;

        // Both ligature pieces map inside the original ligature cluster.
        Assert.That(fRange.Start.CharIndex, Is.GreaterThanOrEqualTo(0));
        Assert.That(fRange.End.CharIndex, Is.LessThanOrEqualTo(1));
        Assert.That(iRange.Start.CharIndex, Is.GreaterThanOrEqualTo(0));
        Assert.That(iRange.End.CharIndex, Is.LessThanOrEqualTo(1));

        // X follows at original offset 1.
        Assert.That(xRange.Start.CharIndex, Is.EqualTo(1));
        Assert.That(xRange.End.CharIndex, Is.EqualTo(2));
        Assert.That(xRange.Start.TokenIndex, Is.EqualTo(1),
            "X TokenIndex should be 1 (1 original grapheme before it: ﬁ), not 2 (2 parseInput graphemes before it: f, i).");
        Assert.That(xRange.End.TokenIndex, Is.EqualTo(2));
    }
}
