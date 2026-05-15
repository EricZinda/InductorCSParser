using System;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;

namespace InductorParser.Tests;

[TestFixture]
public class AndRuleTests
{
    [Test]
    public void And_all_children_succeed_concatenates_consumed_input()
    {
        // Token defaults to FlattenType.Delete, so its text is filtered
        // out of the tree at parse time. PreserveAllSymbols keeps
        // every grammar node so Tree.ToString() sees the full matched
        // input.
        var rule = And(Token('a'), Token('b'), Token('c'));
        var result = rule.Parse("abc", new ParseOptions { PreserveAllSymbols = true });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("abc"));
    }

    [Test]
    public void And_failure_without_WithError_falls_back_to_positional_message()
    {
        // No WithError on any child or on And itself. Token('b') records a
        // null message at offset 1. The positional fallback renders.
        var rule = And(Token('a'), Token('b'));
        var result = rule.Parse("ax");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
        Assert.That(result.ErrorMessage, Does.StartWith("Parse failed at offset 1"));
    }

    [Test]
    public void And_first_child_failure_propagates_position()
    {
        // Each child has its own WithError message. When the first child
        // fails, its message wins because it's the only one that ran.
        var rule = And(Token('a').WithError("need an 'a'"),
                         Token('b').WithError("need a 'b'"));

        var result = rule.Parse("xb");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("need an 'a'"));
    }

    [Test]
    public void And_later_child_failure_reports_at_deeper_position()
    {
        // Token('a') succeeds. Token('b') runs at offset 1 and fails on 'x'.
        // The second child's pre-read position is deeper than anywhere
        // the first child could have recorded, and its WithError message
        // is the one that surfaces.
        var rule = And(Token('a').WithError("need an 'a'"),
                         Token('b').WithError("need a 'b'"));

        var result = rule.Parse("ax");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
        Assert.That(result.ErrorMessage, Is.EqualTo("need a 'b'"));
    }

    [Test]
    [RecursiveEngineOnly]
    public void And_trace_success_produces_expected_output()
    {
        var sink = NewSink();
        And(Token('a'), Token('b')).Parse("ab", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'a', Consumed: 1",
            "      SUCC | Token: found 'a'",
            "      Lexer.Read: 'b', Consumed: 2",
            "      SUCC | Token: found 'b'",
            "   SUCC | And: found 2"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    [RecursiveEngineOnly]
    public void And_trace_failure_produces_expected_output()
    {
        // "ax" advances past 'a', then Token('b') fails at position 1
        // which is > the initial deepest (0), so the
        // Lexer.RecordFailure trace fires too.
        var sink = NewSink();
        And(Token('a'), Token('b')).Parse("ax", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'a', Consumed: 1",
            "      SUCC | Token: found 'a'",
            "      Lexer.Read: 'x', Consumed: 2",
            "      FAIL | Token: found 'x', wanted 'b'",
            "      Lexer.RecordFailure: new deepest failure at char 1",
            "   FAIL | And: symbol #1"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Sealed_And_rejects_Flatten()
    {
        var rule = And(Token('a'), Token('b'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.Flatten(FlattenType.Preserve));
    }

    [Test]
    public void Sealed_And_rejects_WithError()
    {
        var rule = And(Token('a'), Token('b'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.WithError("late"));
    }

    [Test]
    public void Sealed_And_rejects_As()
    {
        var rule = And(Token('a'), Token('b'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.As("late"));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Or_And_skips_when_first_child_cannot_match_peek()
    {
        // And publishes the first-token set of its leading
        // Always-consuming child. And(Token('a'), Token('b'))'s
        // first-set is {'a'}. Peek 'x' isn't in {'a'}, so the outer
        // Or skips the And branch entirely (one SKIP line for
        // the And, none for its inner Token children).
        var sink = NewSink();
        var rule = Or(And(Token('a'), Token('b')), Literal("x"));
        var result = rule.Parse("x", new ParseOptions { TraceSink = sink });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(sink.ToString(), Does.Contain("SKIP | And:"));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Or_And_runs_when_first_child_can_match_peek()
    {
        // Peek 'a' is in And's first-set ({'a'}), so the outer Or
        // doesn't skip the And — it runs and matches "ab".
        var sink = NewSink();
        var rule = Or(And(Token('a'), Token('b')), Literal("x"));
        var result = rule.Parse("ab", new ParseOptions { TraceSink = sink });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(sink.ToString(), Does.Not.Contain("SKIP | And:"));
    }

    [Test]
    public void SourceText_on_And_recovers_consumed_span_even_when_all_children_are_Delete()
    {
        // Three Literal children, all Delete by default. The
        // composite's Children list is empty (everything was filtered),
        // ToString returns "" because there's nothing to render. The
        // engine recorded the consumed span on the composite at parse
        // time, so SourceText returns "abc" anyway. This is the
        // single most important SourceText invariant: a Preserve
        // composite's SourceText covers what the rule ACTUALLY
        // consumed, not just the surviving children's text.
        var rule = And(Literal("a"), Literal("b"), Literal("c")).As("composite");
        var result = rule.Parse("abc");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(""),
            "Sanity check: ToString sees no surviving children.");
        Assert.That(result.Tree!.SourceText, Is.EqualTo("abc"));
        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(3));
    }

    [Test]
    public void SourceText_on_And_covers_leading_and_trailing_Delete_children()
    {
        // Mix of Preserve and Delete: the leading Literal("ab") is
        // Preserve, the trailing Literal("cd") is Delete (factory
        // default). SourceText covers ALL 4 chars even though
        // ToString returns just "ab". Mirror with leading Delete +
        // trailing Preserve flipped, to show the symmetry — leading
        // Delete content doesn't push the start past offset 0, and
        // trailing Delete content does extend the end.
        var trailingDelete = And(Literal("ab").Preserve(), Literal("cd")).As("trail");
        var trailingResult = trailingDelete.Parse("abcd");
        Assert.That(trailingResult.Tree!.ToString(), Is.EqualTo("ab"));
        Assert.That(trailingResult.Tree!.SourceText, Is.EqualTo("abcd"));

        var leadingDelete = And(Literal("ab"), Literal("cd").Preserve()).As("lead");
        var leadingResult = leadingDelete.Parse("abcd");
        Assert.That(leadingResult.Tree!.ToString(), Is.EqualTo("cd"));
        Assert.That(leadingResult.Tree!.SourceText, Is.EqualTo("abcd"));
    }

    [Test]
    public void SourceText_on_And_returns_matched_text_under_every_FlattenType()
    {
        SourceTextFlattenTypeMatrixHelper.AssertSourceTextUnderEveryFlattenType(
            ruleBuilder: () => And(Literal("a"), Literal("b")),
            input: "ab",
            expectedSourceText: "ab");
    }

    [Test]
    public void And_composite_range_spans_first_to_last_consumed_char()
    {
        // The And consumed all three tokens. SourceRange covers [0, 3).
        var rule = And(Token('a').Preserve(), Token('b').Preserve(), Token('c').Preserve())
            .As("triple");
        var result = rule.Parse("abc");

        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(3));
    }

    [Test]
    public void And_named_subrule_after_leading_prefix_reports_its_own_starting_offset()
    {
        // The inner named rule sits after a 2-char prefix the outer And
        // consumed. Find(inner) + SourceRange should report a range
        // starting at offset 2, not 0.
        var inner = Literal("XYZ").As("inner");
        var rule = And(Literal("ab"), inner);
        var result = rule.Parse("abXYZ");

        var innerSymbol = result.Tree!.Find(inner)!;
        var range = innerSymbol.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(2));
        Assert.That(range.End.CharIndex, Is.EqualTo(5));
    }

    [Test]
    public void And_with_leading_Delete_children_spans_the_consumed_input()
    {
        // The "ab" prefix is matched but Delete-flattened, so it never
        // enters the tree. The And's SourceRange covers EVERYTHING the
        // rule consumed, including the leading Delete'd "ab". To
        // recover just the preserved tail, name that child and call
        // SourceRange on the subsymbol.
        var rule = And(Literal("ab"), Literal("cd").Preserve()).As("composite");
        var result = rule.Parse("abcd");

        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(4));
    }

    [Test]
    public void And_with_trailing_Delete_children_spans_the_consumed_input()
    {
        // Mirror of the leading case: trailing "cd" is consumed but
        // Delete-flattened. The composite's range still covers all 4
        // chars. This is the TOML "Token('-').Preserve() + Literal('inf')"
        // shape with inf Delete — recovers the full "-inf" span instead
        // of just the "-" the surviving leaf points at.
        var rule = And(Literal("ab").Preserve(), Literal("cd")).As("composite");
        var result = rule.Parse("abcd");

        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(4));
    }

    [Test]
    public void And_with_Delete_middle_child_still_spans_full_consumed_input()
    {
        // The "cd" middle is consumed but Delete-flattened. The
        // composite reports a range of 6 chars even though only 4 are
        // physically in the tree.
        var rule = And(
            Literal("ab").Preserve(),
            Literal("cd"),
            Literal("ef").Preserve()).As("composite");
        var result = rule.Parse("abcdef");

        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(6));
    }

    [Test]
    public void And_empty_leaf_in_middle_does_not_truncate_composite_range()
    {
        // ScanUntil with the stopper already at the cursor produces a
        // zero-width preserved leaf in the middle. The composite's
        // recorded consumed span still spans the full 2-char match
        // because it was recorded at parse time, not stitched from
        // children.
        var rule = And(
            Literal("a").Preserve(),
            ScanUntil(TokenSet.Runes("b")).Preserve(),
            Literal("b").Preserve()).As("composite");
        var result = rule.Parse("ab");

        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(2));
    }

    [Test]
    public void And_leading_empty_leaf_does_not_break_composite_range()
    {
        // Mirror of the middle/trailing empty-leaf cases: a LEADING
        // zero-width leaf doesn't change the range. ScanUntil with the
        // stopper at the cursor produces an empty leaf at offset 0;
        // Literal("a") follows. Range is [0, 1).
        var rule = And(
            ScanUntil(TokenSet.Runes("a")).Preserve(),
            Literal("a").Preserve()).As("composite");
        var result = rule.Parse("a");

        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(1));
    }

    [Test]
    public void And_trailing_empty_leaf_does_not_extend_composite_range()
    {
        // ScanUntil at end-of-input returns a zero-width leaf. The
        // composite's End comes from the engine's recorded consumed
        // span (which ended at 2 chars), not from the trailing empty
        // leaf's offset. eofIsTerminator: true so the inner scan
        // succeeds with an empty leaf instead of failing at EOF (the
        // test is about the composite range, not the strict-stopper
        // check).
        var rule = And(
            Literal("ab").Preserve(),
            ScanUntil(TokenSet.Runes("z"), eofIsTerminator: true).Preserve()).As("composite");
        var result = rule.Parse("ab");

        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(2));
    }

    [Test]
    public void And_spans_what_it_consumed_even_when_only_empty_leaves_survive_children()
    {
        // Two ScanUntils whose stopper is at the cursor each produce
        // an empty leaf. The Token('a') is Delete-flattened. The And
        // consumed 1 char even though the surviving children are both
        // zero-width.
        var rule = And(
            ScanUntil(TokenSet.Runes("a")).Preserve(),
            ScanUntil(TokenSet.Runes("a")).Preserve(),
            Token('a')).As("composite");
        var result = rule.Parse("a");

        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(1));
    }

    [Test]
    public void Nested_And_reports_each_level_consumed_span_independently()
    {
        // Outer And -> inner And -> empty leaf. Each composite reports
        // the span IT consumed, not the union of preserved leaves below
        // it. Inner consumed 0 chars; outer consumed 1 (inner's 0 +
        // Token('a')'s 1).
        var inner = And(ScanUntil(TokenSet.Runes("a")).Preserve()).As("inner");
        var rule = And(inner, Token('a')).As("outer");
        var result = rule.Parse("a");

        var outerRange = result.Tree!.SourceRange!.Value;
        Assert.That(outerRange.Start.CharIndex, Is.EqualTo(0));
        Assert.That(outerRange.End.CharIndex, Is.EqualTo(1));

        var innerRange = result.Tree!.Find(inner)!.SourceRange!.Value;
        Assert.That(innerRange.Start.CharIndex, Is.EqualTo(0));
        Assert.That(innerRange.End.CharIndex, Is.EqualTo(0));
    }

    [Test]
    public void And_Find_on_inner_composite_returns_inner_range_not_outer()
    {
        // Two named composites, one nested in the other. The outer's
        // range spans the whole match; the inner's range spans only
        // its own children. Verifies Find + SourceRange together don't
        // leak the outer range.
        var inner = And(Token('x').Preserve(), Token('y').Preserve()).As("inner");
        var outer = And(Literal("ab").Preserve(), inner, Literal("cd").Preserve()).As("outer");
        var result = outer.Parse("abxycd");

        var outerRange = result.Tree!.SourceRange!.Value;
        Assert.That(outerRange.Start.CharIndex, Is.EqualTo(0));
        Assert.That(outerRange.End.CharIndex, Is.EqualTo(6));

        var innerRange = result.Tree!.Find(inner)!.SourceRange!.Value;
        Assert.That(innerRange.Start.CharIndex, Is.EqualTo(2));
        Assert.That(innerRange.End.CharIndex, Is.EqualTo(4));
    }

    [Test]
    public void And_range_End_at_input_end_equals_input_length_in_all_units()
    {
        // The composite ends exactly at input.Length. CharIndex,
        // TokenIndex, and Column should all report the same value (no
        // off-by-one at the boundary). Line stays at 0 since the input
        // has no line breaks.
        var rule = And(Literal("xx").Preserve(), Literal("yy").Preserve()).As("composite");
        var result = rule.Parse("xxyy");

        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.End.CharIndex, Is.EqualTo(4));
        Assert.That(range.End.TokenIndex, Is.EqualTo(4));
        Assert.That(range.End.Line, Is.EqualTo(0));
        Assert.That(range.End.Column, Is.EqualTo(4));
    }

    [Test]
    public void And_outer_composite_spans_what_it_consumed_when_inner_subtree_is_Flatten_with_Delete_children()
    {
        // The first And is Flatten with two Delete children — it
        // contributes no surviving Children to the outer. But the
        // outer And consumed all 6 chars: its 4-char Flatten prefix
        // plus the 2-char preserved tail. The outer's recorded span
        // covers the full consumption.
        var deletedPrefix = And(Literal("aa"), Literal("bb"));
        var rule = And(deletedPrefix, Literal("cc").Preserve()).As("composite");
        var result = rule.Parse("aabbcc");

        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(6));
    }

    [Test]
    public void And_named_WithError_beats_deeper_mechanical_inner()
    {
        // Literal("ab") reads 'a' then fails on second char, recording
        // a mechanical failure at position 1. And records its named
        // WithError at the failing child's start (0, where Literal began).
        // Named beats mechanical regardless of depth.
        // See docs/ErrorArchitecture.md.
        var rule = And(Literal("ab"), Token('!')).WithError("expected ab!");

        var result = rule.Parse("axyz");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("expected ab!"));
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
    }

    [Test]
    public void And_named_WithError_position_is_the_failing_child_start()
    {
        // The position attached to And's WithError points at the failing
        // child's start (1, where Token('!') began trying after Literal
        // ate the leading 'a'), not at the And's overall start (0). That
        // puts the cursor where the user needs to fix the input, not
        // inside input the parser already accepted.
        // See docs/ErrorArchitecture.md.
        var rule = And(Literal("a"), Token('!')).WithError("compound msg");

        var result = rule.Parse("axyz");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
    }
}
