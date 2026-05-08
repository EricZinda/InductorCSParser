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
public class ScanWhileRuleTests
{
    [Test]
    public void ScanWhile_matches_a_run_into_one_leaf()
    {
        var result = ScanWhile(TokenSet.Ascii.Letters).Parse("abcXYZ");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("abcXYZ"));
        Assert.That(result.Tree!.Children.Count, Is.EqualTo(0));
    }

    [Test]
    public void ScanWhile_stops_before_first_rune_outside_the_set()
    {
        var rule = And(ScanWhile(TokenSet.Ascii.Letters), Token('!'));

        var result = rule.Parse("abc!");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("abc"));
    }

    [Test]
    public void ScanWhile_requires_at_least_one_rune()
    {
        var result = ScanWhile(TokenSet.Ascii.Letters)
            .WithError("need a letter")
            .Parse("123");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("need a letter"));
    }

    [Test]
    public void ScanWhile_honors_minimum_count()
    {
        var result = ScanWhile(TokenSet.Ascii.Letters, minimumCount: 4)
            .WithError("need four letters")
            .Parse("abc!");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(result.ErrorMessage, Is.EqualTo("need four letters"));
    }

    [Test]
    public void ScanWhile_rejects_zero_minimum_count()
    {
        Assert.That(
            () => ScanWhile(TokenSet.Ascii.Letters, minimumCount: 0),
            Throws.TypeOf<System.ArgumentOutOfRangeException>());
    }

    [Test]
    public void ScanWhile_rejects_multi_rune_grapheme_under_grapheme_lexer()
    {
        var rule = ScanWhile(TokenSet.Single(WavingHandRune));

        var result = rule.Parse(SkinTonedWaveGrapheme);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
    }

    [Test]
    [RecursiveEngineOnly]
    public void ScanWhile_trace_success_produces_expected_output()
    {
        var sink = NewSink();
        ScanWhile(TokenSet.Ascii.Letters).Parse("abc",
            new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.AdvanceWhileRuneIn: 'a', Consumed: 1",
            "   Lexer.AdvanceWhileRuneIn: 'b', Consumed: 2",
            "   Lexer.AdvanceWhileRuneIn: 'c', Consumed: 3",
            "   SUCC | ScanWhile: count= 3, 3 chars, wanted one or more of '[A-Z,a-z]'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Sealed_ScanWhile_rejects_Flatten()
    {
        var rule = ScanWhile(TokenSet.Ascii.Letters);
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.Flatten(FlattenType.Preserve));
    }

    [Test]
    public void Sealed_ScanWhile_rejects_WithError()
    {
        var rule = ScanWhile(TokenSet.Ascii.Letters);
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.WithError("late"));
    }

    [Test]
    public void Sealed_ScanWhile_rejects_As()
    {
        var rule = ScanWhile(TokenSet.Ascii.Letters);
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.As("late"));
    }

    [Test]
    public void ScanWhile_with_pinned_SymbolId_uses_pinned_id_for_run_leaf()
    {
        // Sibling of the OneOf / NoneOf / AnyToken / WithinToken pinned-
        // SymbolId tests added in p1nd. ScanWhile emits one leaf per
        // matched run with the rule's Id directly (no rune-as-leaf-id
        // shortcut, since a run of multiple tokens doesn't have one
        // distinguished rune to carry). .As(SymbolId) writes the user's
        // pinned value into Id, so the leaf carries it by construction.
        // Test locks in the matrix so a future leaf-id refactor that
        // routes ScanWhile through ResolveLeafId or a similar helper has
        // to keep .As(SymbolId) honored.
        var pinnedId = new SymbolId(SymbolRanges.CustomRangeStart + 104);
        var rule = ScanWhile(TokenSet.Ascii.Letters).As(pinnedId);
        var result = rule.Parse("abc");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Tree!.Id, Is.EqualTo(pinnedId),
            "leaf carries the user-pinned SymbolId");
        Assert.That(result.Tree!.Is(rule), Is.True);
        Assert.That(result.Tree!.Find(rule), Is.Not.Null);
    }

    [Test]
    public void ScanWhile_with_multi_rune_set_consumes_a_run_of_graphemes()
    {
        // Set: { USFlag, WomanShrugging }. Input: USFlag + WomanShrugging.
        // ScanWhile should consume both emoji graphemes as one leaf.
        var rule = ScanWhile(TokenSet.Runes(USFlagGrapheme + WomanShruggingGrapheme));

        var result = rule.Parse(USFlagGrapheme + WomanShruggingGrapheme);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(USFlagGrapheme + WomanShruggingGrapheme));
    }

    [Test]
    public void ScanWhile_with_precomposed_set_entry_matches_decomposed_input_under_FormD()
    {
        // Set: precomposed U+00E9. Under FormD the lexer feeds the rule
        // "e + combining acute" as one two-rune cluster. Without compile-
        // time set projection, the rune-only set has only U+00E9 and the
        // cluster fails the rune-fast-path's tokenLength == runeLen check.
        // OneOf with the same set / same input matches, so the asymmetry
        // is the bug.
        var rule = ScanWhile(TokenSet.Runes(LatinEAcutePrecomposedGrapheme));
        rule.Compile(System.Text.NormalizationForm.FormD);

        var result = rule.Parse(LatinEAcutePrecomposedGrapheme);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void ScanWhile_with_mixed_set_stops_at_first_token_outside_the_set()
    {
        // Mix of letters and one multi-rune entry. The scan should
        // pull as many letters or USFlag tokens as possible and stop
        // at the first token that's neither. AllowTrailingInput lets
        // the parse succeed even though ScanWhile doesn't consume the
        // trailing WomanShrugging that stopped it.
        var rule = ScanWhile(TokenSet.Ascii.Letters | TokenSet.Runes(USFlagGrapheme));
        var input = "abc" + USFlagGrapheme + "d" + WomanShruggingGrapheme;

        var result = rule.Parse(input, new ParseOptions { AllowTrailingInput = true });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("abc" + USFlagGrapheme + "d"));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Or_ScanWhile_skips_when_peek_is_outside_set()
    {
        // ScanWhile(set) publishes (set, Always, MustBeIn). minimumCount
        // is at least 1 so a successful match always consumes at least
        // one token. Peek '1' isn't in {a..z}, so the shortcut skips
        // ScanWhile and the literal "1" branch wins.
        var sink = NewSink();
        var rule = Or(ScanWhile(TokenSet.Ascii.Letters), Literal("1"));
        var result = rule.Parse("1", new ParseOptions { TraceSink = sink });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(sink.ToString(), Does.Contain("SKIP | ScanWhile:"));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Or_ScanWhile_runs_when_peek_is_in_set()
    {
        // Peek 'a' is in {a..z}, so the shortcut doesn't skip and
        // ScanWhile runs.
        var sink = NewSink();
        var rule = Or(ScanWhile(TokenSet.Ascii.Letters), Literal("1"));
        var result = rule.Parse("abc", new ParseOptions { TraceSink = sink });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(sink.ToString(), Does.Not.Contain("SKIP | ScanWhile:"));
    }

    // -----------------------------------------------------------------
    // Compile-form normalization matrix
    //
    // See GraphemeRuleTests for the full matrix rationale. ScanWhile's
    // matching data is the same TokenSet shape OneOf / NoneOf carry,
    // and the same OneOfRule.NormalizeAndValidate helper handles the
    // Compile-time projection. Default minimumCount=1 means the rule
    // isn't zero-matchable, so a stale set surfaces as outright
    // failure rather than a silent zero-length match.
    // -----------------------------------------------------------------

    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void ScanWhile_bare_matches_input_under_form(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        if (row.Category == NormalizationExamples.NormalizationCategory.LoneSurrogateNotNormalizable)
        {
            Assert.Throws<ArgumentException>(() => TokenSet.Runes(row.Source));
            return;
        }

        var rule = ScanWhile(TokenSet.Runes(row.Source));

        if (!NormalizationExamples.PostFormIsSingleGrapheme(row, form))
        {
            Assert.Throws<InvalidOperationException>(() => rule.Compile(form));
            return;
        }

        rule.Compile(form);
        var result = rule.Parse(row.Source);
        Assert.That(result.Success, Is.True,
            $"ScanWhile(TokenSet.Runes(\"{NormalizationExamples.Hex(row.Source)}\")).Compile({form}).Parse(\"{NormalizationExamples.Hex(row.Source)}\") " +
            $"should succeed. Error was: {result.ErrorMessage}");
    }

    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void ScanWhile_in_OneOrMore_matches_input_under_form(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        if (row.Category == NormalizationExamples.NormalizationCategory.LoneSurrogateNotNormalizable)
        {
            Assert.Throws<ArgumentException>(() => TokenSet.Runes(row.Source));
            return;
        }

        var rule = OneOrMore(ScanWhile(TokenSet.Runes(row.Source)));

        if (!NormalizationExamples.PostFormIsSingleGrapheme(row, form))
        {
            // Multi-grapheme post-form: ScanWhile reports the entry as
            // an offender via the same single-grapheme-per-token rule
            // OneOf / NoneOf use, so Compile throws.
            Assert.Throws<InvalidOperationException>(() => rule.Compile(form));
            return;
        }

        rule.Compile(form);
        var result = rule.Parse(row.Source);
        Assert.That(result.Success, Is.True,
            $"OneOrMore(ScanWhile(TokenSet.Runes(\"{NormalizationExamples.Hex(row.Source)}\"))).Compile({form}).Parse(\"{NormalizationExamples.Hex(row.Source)}\") " +
            $"should succeed. Error was: {result.ErrorMessage}");
    }

    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void ScanWhile_in_Or_with_fallback_matches_scanwhile_branch_under_form(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        if (row.Category == NormalizationExamples.NormalizationCategory.LoneSurrogateNotNormalizable)
            return; // covered by ScanWhile_in_OneOrMore's TokenSet.Runes-throws path
        if (!NormalizationExamples.PostFormIsSingleGrapheme(row, form))
            return; // multi-grapheme post-form is covered by the ScanWhile_in_OneOrMore Compile-throws path

        var scanWhileRule = ScanWhile(TokenSet.Runes(row.Source)).As("scanWhileBranch");
        var fallback = AnyToken().As("fallbackBranch");
        var rule = Or(scanWhileRule, fallback);

        rule.Compile(form);
        var result = rule.Parse(row.Source);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.Find(scanWhileRule), Is.Not.Null,
            $"Or(ScanWhile(TokenSet.Runes(\"{NormalizationExamples.Hex(row.Source)}\")), AnyToken).Compile({form}).Parse(\"{NormalizationExamples.Hex(row.Source)}\") " +
            $"matched the AnyToken fallback instead of the ScanWhile branch.");
    }

    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void ScanWhile_in_AllOf_with_Eof_matches_full_input_under_form(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        if (row.Category == NormalizationExamples.NormalizationCategory.LoneSurrogateNotNormalizable)
        {
            Assert.Throws<ArgumentException>(() => TokenSet.Runes(row.Source));
            return;
        }

        var rule = And(ScanWhile(TokenSet.Runes(row.Source)), Eof());

        if (!NormalizationExamples.PostFormIsSingleGrapheme(row, form))
        {
            Assert.Throws<InvalidOperationException>(() => rule.Compile(form));
            return;
        }

        rule.Compile(form);
        var result = rule.Parse(row.Source);
        Assert.That(result.Success, Is.True,
            $"And(ScanWhile(TokenSet.Runes(\"{NormalizationExamples.Hex(row.Source)}\")), Eof()).Compile({form}).Parse(\"{NormalizationExamples.Hex(row.Source)}\") " +
            $"should succeed. Error was: {result.ErrorMessage}");
    }

    // Matrix-driven SourceRange test. See docs/TestArchitecture.md
    // "Per-rule SourceRange-matrix tests live in each rule's own
    // test file." Shared scaffold lives in SourceRangeMatrixHelper.
    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void SourceRange_for_ScanWhile_target_after_normalized_Literal_prefix_uses_original_coords(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        SourceRangeMatrixHelper.AssertTargetAfterLiteralPrefix(
            row, form,
            target: ScanWhile(TokenSet.Ascii.Letters).As("run").Preserve(),
            targetText: "XYZ");
    }
}
