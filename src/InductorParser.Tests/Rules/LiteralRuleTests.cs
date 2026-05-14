using System;
using System.Text;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;
using static InductorParser.Tests.UnicodeExamples;

namespace InductorParser.Tests;

[TestFixture]
public class LiteralRuleTests
{
    [Test]
    public void Literal_matches_single_rune_ascii_string()
    {
        var rule = Literal("a");
        var result = rule.Parse("a");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Literal_matches_multi_character_ascii_word()
    {
        var rule = Literal("major");
        var result = rule.Parse("major");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Literal_matches_multi_rune_grapheme_under_grapheme_lexer()
    {
        // The e+combining-acute is one token whose Chars span covers
        // both chars. The lockstep compares the full token span
        // against the two-char expected portion in a single iteration.
        var rule = Literal(LatinEAcuteGrapheme);
        rule.Compile(null);
        var result = rule.Parse(LatinEAcuteGrapheme);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Literal_matches_word_then_grapheme_then_word()
    {
        // Mixed width: a two-char BMP prefix, a supplementary-plane rune
        // (2 UTF-16 chars), and a BMP suffix. The guitar emoji is one
        // token of length 2. The lockstep handles variable token widths
        // naturally.
        var literal = "hi" + GuitarGrapheme + "!";
        var rule = Literal(literal);
        var result = rule.Parse(literal);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Literal_rejects_mismatched_prefix_at_first_token()
    {
        var rule = Literal("major").WithError("expected 'major'");
        var result = rule.Parse("xajor");
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected 'major'"));
    }

    [Test]
    public void Literal_rejects_mismatched_middle_at_failing_token_position()
    {
        // The points-at-offender test: first three chars match, fourth
        // doesn't. Pre-read position for that failing read is 3, not 0.
        var rule = Literal("major").WithError("expected 'major'");
        var result = rule.Parse("majxr");
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected 'major'"));
    }

    [Test]
    public void Literal_rejects_eof_midway_at_eof_position()
    {
        // Input runs out in the middle of the expected string. The EOF
        // read happens at offset 3, so the failure records there.
        var rule = Literal("major").WithError("expected 'major'");
        var result = rule.Parse("maj");
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected 'major'"));
    }

    [Test]
    public void Literal_rejects_empty_input()
    {
        var rule = Literal("hi").WithError("expected 'hi'");
        var result = rule.Parse("");
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected 'hi'"));
    }

    [Test]
    public void Literal_rejects_empty_string_at_construction()
    {
        var ex = Assert.Throws<ArgumentException>(() => Literal(""));
        Assert.That(ex!.Message, Does.Contain("non-empty"));
    }

    [Test]
    public void Literal_symbol_text_round_trips_via_ToString()
    {
        // Leaf symbol carries a Memory range over the matched input. The
        // ToString round-trip should produce the original literal.
        var rule = Literal("select").Flatten(SyntaxTree.FlattenType.Preserve);
        var result = rule.Parse("select");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("select"));
    }

    [Test]
    public void Literal_in_composite_reports_offender_in_context()
    {
        // Prefix And-member consumes "select " then Literal fails at offset 7.
        // Confirms Literal's RecordFailure uses the token-local pre-read
        // position, not transaction.StartPosition of some outer frame.
        var rule = And(
            Literal("select "),
            Literal("FROM").WithError("expected 'FROM'"));
        var result = rule.Parse("select XXXX");
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(7));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected 'FROM'"));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Literal_trace_success_produces_expected_output()
    {
        var sink = NewSink();
        Literal("hi").Parse("hi", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: 'h', Consumed: 1",
            "   Lexer.Read: 'i', Consumed: 2",
            "   SUCC | Literal: found 'hi'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Literal_trace_failure_produces_expected_output()
    {
        // Failure on the first token of the literal: deepest-failure stays
        // at 0, so RecordFailure doesn't trace, matching Token's fail-at-0
        // shape. The mid-literal-fail case is covered by the RecordFailure
        // trace line that appears when position advances past 0.
        var sink = NewSink();
        Literal("hi").Parse("xo", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: 'x', Consumed: 1",
            "   FAIL | Literal: found 'x', wanted 'hi'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void LiteralIgnoreAsciiCase_matches_same_case()
    {
        var rule = LiteralIgnoreAsciiCase("SELECT");
        var result = rule.Parse("SELECT");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void LiteralIgnoreAsciiCase_matches_different_case()
    {
        var rule = LiteralIgnoreAsciiCase("SELECT");
        Assert.That(rule.Parse("select").Success, Is.True);
        Assert.That(rule.Parse("Select").Success, Is.True);
        Assert.That(rule.Parse("sElEcT").Success, Is.True);
    }

    [Test]
    public void LiteralIgnoreAsciiCase_leaves_non_letter_chars_strict()
    {
        // The 0x20 bit difference between '[' and '{', '@' and '`', etc.
        // must NOT be treated as a case-insensitive match. Only A-Za-z
        // get that treatment.
        var rule = LiteralIgnoreAsciiCase("a[b");
        Assert.That(rule.Parse("A[B").Success, Is.True);
        Assert.That(rule.Parse("A{B").Success, Is.False);
    }

    [Test]
    public void LiteralIgnoreAsciiCase_rejects_non_ascii_pattern_at_construction()
    {
        // Patterns must be ASCII-only. German sharp s in the pattern
        // would never participate in case-folding (the rule is named
        // LiteralIgnoreAsciiCase, and ASCII case-folding doesn't reach
        // U+00DF), so admitting it at construction would mislead the
        // reader. Construction throws instead, pointing at the offending
        // char. Grammars that want a non-ASCII keyword should use
        // Literal("straße") directly.
        var exception = Assert.Throws<ArgumentException>(() => LiteralIgnoreAsciiCase("straße"));
        Assert.That(exception!.Message, Does.Contain("ASCII-only"));
        Assert.That(exception.Message, Does.Contain("U+00DF"));
    }

    [Test]
    public void LiteralIgnoreAsciiCase_rejects_empty_string_at_construction()
    {
        Assert.Throws<ArgumentException>(() => LiteralIgnoreAsciiCase(""));
    }

    [Test]
    public void LiteralIgnoreAsciiCase_records_failure_at_failing_token_position()
    {
        var rule = LiteralIgnoreAsciiCase("major").WithError("expected 'major'");
        var result = rule.Parse("maJxr");
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected 'major'"));
    }

    [Test]
    [RecursiveEngineOnly]
    public void LiteralIgnoreAsciiCase_trace_uses_LiteralIgnoreAsciiCase_label()
    {
        // The trace label must read "LiteralIgnoreAsciiCase", not "Literal",
        // so a reader can tell the two rule kinds apart in a mixed trace.
        var sink = NewSink();
        LiteralIgnoreAsciiCase("hi").Parse("HI", new ParseOptions { TraceSink = sink });
        Assert.That(sink.ToString(), Does.Contain("LiteralIgnoreAsciiCase"));
    }

    [Test]
    public void Sealed_Literal_rejects_Flatten()
    {
        var rule = Literal("abc");
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.Flatten(FlattenType.Preserve));
    }

    [Test]
    public void Sealed_Literal_rejects_WithError()
    {
        var rule = Literal("abc");
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.WithError("late"));
    }

    [Test]
    public void Sealed_Literal_rejects_As()
    {
        var rule = Literal("abc");
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.As("late"));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Or_Literal_skips_when_peek_is_not_first_grapheme()
    {
        // Literal publishes (first-grapheme set, Always, MustBeIn). For
        // Literal("hello") that's {'h'}. Peek 'x' isn't in {'h'}, so the
        // shortcut skips Literal and the second alternative wins.
        var sink = NewSink();
        var rule = Or(Literal("hello"), Literal("xyz"));
        var result = rule.Parse("xyz", new ParseOptions { TraceSink = sink });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(sink.ToString(), Does.Contain("SKIP | Literal:"));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Or_Literal_runs_when_peek_is_first_grapheme()
    {
        // Peek 'h' is in {'h'}, so the shortcut doesn't skip. Literal
        // runs and matches.
        var sink = NewSink();
        var rule = Or(Literal("hello"), Literal("xyz"));
        var result = rule.Parse("hello", new ParseOptions { TraceSink = sink });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(sink.ToString(), Does.Not.Contain("SKIP | Literal:"));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Or_LiteralIgnoreAsciiCase_skips_when_peek_is_outside_case_folded_set()
    {
        // LiteralIgnoreAsciiCase("Select") publishes a first-grapheme
        // set with both ASCII cases of 'S' (so {'S','s'}). Peek 'x'
        // isn't in that set, so the shortcut skips and the second
        // alternative wins.
        var sink = NewSink();
        var rule = Or(LiteralIgnoreAsciiCase("Select"), Literal("xxx"));
        var result = rule.Parse("xxx", new ParseOptions { TraceSink = sink });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(sink.ToString(), Does.Contain("SKIP | LiteralIgnoreAsciiCase:"));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Or_LiteralIgnoreAsciiCase_runs_when_peek_is_either_case()
    {
        // Both 'S' and 's' should pass the shortcut (the case-folded
        // first-grapheme set covers both). Test the lower-case path
        // explicitly so the case-fold logic is exercised, not just the
        // exact-match path.
        var sink = NewSink();
        var rule = Or(LiteralIgnoreAsciiCase("Select"), Literal("xxx"));
        var result = rule.Parse("select", new ParseOptions { TraceSink = sink });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(sink.ToString(), Does.Not.Contain("SKIP | LiteralIgnoreAsciiCase:"));
    }

    // -----------------------------------------------------------------
    // Compile-form normalization matrix
    //
    // See GraphemeRuleTests for the full matrix rationale. Literal
    // accepts multi-grapheme expected text by design (unlike Token),
    // so the only Compile-throws case is the lone surrogate where
    // string.Normalize itself throws.
    // -----------------------------------------------------------------

    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void Literal_bare_matches_input_under_form(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        var rule = Literal(row.Source);

        if (row.Category == NormalizationExamples.NormalizationCategory.LoneSurrogateNotNormalizable)
        {
            Assert.Throws<InvalidOperationException>(() => rule.Compile(form));
            return;
        }

        rule.Compile(form);
        var result = rule.Parse(row.Source);
        Assert.That(result.Success, Is.True,
            $"Literal(\"{NormalizationExamples.Hex(row.Source)}\").Compile({form}).Parse(\"{NormalizationExamples.Hex(row.Source)}\") " +
            $"should succeed. Error was: {result.ErrorMessage}");
    }

    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void Literal_in_OneOrMore_matches_input_under_form(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        var rule = OneOrMore(Literal(row.Source));

        if (row.Category == NormalizationExamples.NormalizationCategory.LoneSurrogateNotNormalizable)
        {
            Assert.Throws<InvalidOperationException>(() => rule.Compile(form));
            return;
        }

        rule.Compile(form);
        var result = rule.Parse(row.Source);
        Assert.That(result.Success, Is.True,
            $"OneOrMore(Literal(\"{NormalizationExamples.Hex(row.Source)}\")).Compile({form}).Parse(\"{NormalizationExamples.Hex(row.Source)}\") " +
            $"should succeed. Error was: {result.ErrorMessage}");
    }

    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void Literal_in_Or_with_fallback_matches_literal_branch_under_form(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        if (row.Category == NormalizationExamples.NormalizationCategory.LoneSurrogateNotNormalizable)
            return; // covered by Literal_in_OneOrMore's Compile-throws path

        var literalRule = Literal(row.Source).As("literalBranch");
        var fallback = AnyToken().As("fallbackBranch");
        var rule = Or(literalRule, fallback);

        rule.Compile(form);
        var result = rule.Parse(row.Source);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.Find(literalRule), Is.Not.Null,
            $"Or(Literal(\"{NormalizationExamples.Hex(row.Source)}\"), AnyToken).Compile({form}).Parse(\"{NormalizationExamples.Hex(row.Source)}\") " +
            $"matched the AnyToken fallback instead of the Literal branch.");
    }

    // And(Literal, Eof). Catches a Literal whose _expected was projected
    // to a different length under the form (e.g., compatibility ligature
    // ﬁ -> "fi" doubles the consumed characters under FormKC). Eof
    // verifies the lexer consumed exactly the post-form-projected source.
    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void Literal_in_AllOf_with_Eof_matches_full_input_under_form(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        var rule = And(Literal(row.Source), Eof());

        if (row.Category == NormalizationExamples.NormalizationCategory.LoneSurrogateNotNormalizable)
        {
            Assert.Throws<InvalidOperationException>(() => rule.Compile(form));
            return;
        }

        rule.Compile(form);
        var result = rule.Parse(row.Source);
        Assert.That(result.Success, Is.True,
            $"And(Literal(\"{NormalizationExamples.Hex(row.Source)}\"), Eof()).Compile({form}).Parse(\"{NormalizationExamples.Hex(row.Source)}\") " +
            $"should succeed. Error was: {result.ErrorMessage}");
    }

    // Matrix-driven SourceRange test. See docs/TestArchitecture.md
    // "Per-rule SourceRange-matrix tests live in each rule's own
    // test file." Shared scaffold lives in SourceRangeMatrixHelper.
    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void SourceRange_for_Literal_target_after_normalized_Literal_prefix_uses_original_coords(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        SourceRangeMatrixHelper.AssertTargetAfterLiteralPrefix(
            row, form,
            target: Literal("XYZ").As("xyzLiteral"),
            targetText: "XYZ");
    }

    // FlattenType-matrix SourceText test. Verifies the SourceText
    // invariant: a Literal Symbol's SourceText is the matched text
    // regardless of whether the rule was Delete, Flatten, or Preserve.
    // See docs/TestArchitecture.md "Per-rule SourceText / SourceRange
    // behavior tests live in each rule's own test file."
    [Test]
    public void SourceText_on_Literal_returns_matched_text_under_every_FlattenType()
    {
        SourceTextFlattenTypeMatrixHelper.AssertSourceTextUnderEveryFlattenType(
            ruleBuilder: () => Literal("hello"),
            input: "hello",
            expectedSourceText: "hello");
    }

    [Test]
    public void Literal_SourceRange_spans_the_matched_text()
    {
        var rule = Literal("hello").Preserve();
        var result = rule.Parse("hello");

        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(5));
        Assert.That(range.End.CharIndex - range.Start.CharIndex, Is.EqualTo(5),
            "End - Start should equal the matched-text length.");
    }

    [Test]
    public void Literal_SourceRange_spans_multiple_lines_when_match_crosses_newline()
    {
        // Literal whose body straddles "\n". Start at line 0 col 0,
        // End at line 1 col 2. Verifies SourcePosition line/column
        // derivation across the newline at the End position.
        var rule = Literal("ab\ncd").Preserve();
        var result = rule.Parse("ab\ncd");

        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.Start.Line, Is.EqualTo(0));
        Assert.That(range.Start.Column, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(5));
        Assert.That(range.End.Line, Is.EqualTo(1));
        Assert.That(range.End.Column, Is.EqualTo(2));
    }
}
