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
public class OneOfRuleTests
{
    [Test]
    public void OneOf_matches_a_letter_and_returns_a_single_rune_symbol()
    {
        var rule = OneOf(TokenSet.Letters);
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("a"));
    }

    [Test]
    public void OneOf_mismatch_without_WithError_falls_back_to_positional_message()
    {
        // No WithError anywhere, so OneOf records a null message at offset
        // 0 and BuildErrorMessage's positional fallback decides what to say.
        var rule = OneOrMore(OneOf(TokenSet.Letters));
        var result = rule.Parse("1abc");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Does.StartWith("Parse failed at offset 0"));
    }

    [Test]
    public void OneOf_EOF_on_empty_input_points_at_zero()
    {
        // OneOrMore requires at least one letter. Empty input can't satisfy
        // that. OneOf sees EOF on its first read and records at its pre-
        // read position 0 with its WithError message.
        var rule = OneOrMore(OneOf(TokenSet.Letters).WithError("need a letter"));

        var result = rule.Parse("");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("need a letter"));
    }

    [Test]
    public void OneOf_mismatch_at_start_points_at_offender()
    {
        // '1' is at offset 0. Not a letter. OneOf records its WithError
        // message at pre-read position 0.
        var rule = OneOrMore(OneOf(TokenSet.Letters).WithError("need a letter"));

        var result = rule.Parse("1abc");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("need a letter"));
    }

    [Test]
    public void OneOf_mismatch_after_successful_matches_points_at_first_bad_char()
    {
        // OneOrMore(Letters) commits "abc" up to offset 3. Then Token(';')
        // runs at offset 3, reads '1', and records its own WithError at
        // pre-read offset 3. That's deeper than the letter's WithError
        // (which is at offset 3 too, from the OneOrMore's terminating
        // attempt, but recorded first). First-writer at equal depth wins.
        //
        // To make the test unambiguous we only put a WithError on Token(';')
        // so there's no contention.
        var rule = And(OneOrMore(OneOf(TokenSet.Letters)),
                       Token(';').WithError("expected ';'"));

        var result = rule.Parse("abc1");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected ';'"));
    }

    [Test]
    [RecursiveEngineOnly]
    public void OneOf_trace_success_produces_expected_output()
    {
        var sink = NewSink();
        OneOf(TokenSet.Ascii.Letters).Parse("x", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: 'x', Consumed: 1",
            "   SUCC | OneOf: found 'x', wanted one of '[A-Z,a-z]'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    [RecursiveEngineOnly]
    public void OneOf_trace_failure_produces_expected_output()
    {
        var sink = NewSink();
        OneOf(TokenSet.Ascii.Letters).Parse("1", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: '1', Consumed: 1",
            "   FAIL | OneOf: found '1', wanted one of '[A-Z,a-z]'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Sealed_OneOf_rejects_Flatten()
    {
        var rule = OneOf("abc");
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.Flatten(FlattenType.Preserve));
    }

    [Test]
    public void Sealed_OneOf_rejects_WithError()
    {
        var rule = OneOf("abc");
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.WithError("late"));
    }

    [Test]
    public void Sealed_OneOf_rejects_As()
    {
        var rule = OneOf("abc");
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.As("late"));
    }

    // Multi-rune grapheme support -------------------------------------------

    [Test]
    public void OneOf_with_TokenSet_Empty_rejects_a_multi_rune_grapheme_without_NRE()
    {
        // OneOf(TokenSet.Empty) is the "match nothing" rule, which is
        // what a programmatically-built set lands on when nothing got
        // added. The cluster doesn't match (the set is empty), but the
        // membership probe must return false instead of NRE'ing on
        // _multiRuneGraphemes.Length when the underlying TokenSet is
        // default(TokenSet) (the public TokenSet.Empty alias).
        var rule = OneOf(TokenSet.Empty);
        rule.Compile(null);

        var result = rule.Parse(LatinEAcuteGrapheme);

        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void OneOf_matches_a_multi_rune_grapheme_under_grapheme_lexer()
    {
        // OneOf(set) where set has multi-rune entries: the lexer
        // hands back the whole grapheme as one token with RuneValue
        // == -1, and OneOf uses the multi-rune-array path to match it.
        // NormalizeInput stays default; the test inputs aren't
        // affected by NFC.
        var rule = OneOf(TokenSet.Runes(USFlagGrapheme + WomanShruggingGrapheme));

        Assert.That(rule.Parse(USFlagGrapheme).Success, Is.True);
        Assert.That(rule.Parse(WomanShruggingGrapheme).Success, Is.True);
        // A different multi-rune grapheme isn't a member.
        Assert.That(rule.Parse(SkinTonedWaveGrapheme).Success, Is.False);
        // EOF still fails.
        Assert.That(rule.Parse("").Success, Is.False);
    }

    [Test]
    public void OneOf_mixed_set_matches_both_letters_and_a_multi_rune_Token()
    {
        // Letters | Runes(USFlag) is the canonical mixed set: a
        // big rune-only class plus a single multi-rune entry. OneOf
        // uses the rune intervals for letter tokens and the
        // multi-rune array for the flag token.
        var rule = OneOf(TokenSet.Letters | TokenSet.Runes(USFlagGrapheme));

        Assert.That(rule.Parse("a").Success, Is.True);
        Assert.That(rule.Parse(USFlagGrapheme).Success, Is.True);
        // A digit isn't a letter and isn't the flag.
        Assert.That(rule.Parse("1").Success, Is.False);
    }

    [Test]
    public void Unnamed_OneOf_uses_the_rune_value_as_the_leaf_id_for_single_rune_tokens()
    {
        // The documented unnamed-OneOf optimization: tree consumers can
        // switch on which rune matched without going through a synthetic
        // per-OneOf id. An unnamed rule has Name == null, so the leaf
        // carries the rune's code point directly.
        var rule = OneOf(TokenSet.Ascii.Letters);
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Tree!.Id.Value, Is.EqualTo(0x61),
            "unnamed OneOf: leaf carries the matched rune's code point");
    }

    [Test]
    public void Named_OneOf_uses_rule_id_so_Find_resolves_the_named_rule()
    {
        // A named OneOf carries the rule's Id on every leaf. Tree.Find,
        // Tree.Is, and NameOf all resolve through the rule reference.
        var letter = OneOf(TokenSet.Ascii.Letters).As("letter");
        var result = letter.Parse("a");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Tree!.Is(letter), Is.True);
        Assert.That(result.Tree!.Find(letter), Is.Not.Null);
    }

    [Test]
    public void OneOf_with_multi_rune_match_uses_rule_id_regardless_of_naming()
    {
        // For a multi-rune match (Token.RuneValue == -1), the leaf carries
        // the rule's own Id either way: there's no rune code point that
        // fits in one int, so the rune-as-leaf-id branch can't fire.
        // Find and Is resolve through rule.Id for both unnamed and named
        // shapes.
        var unnamedRule = OneOf(TokenSet.Runes(USFlagGrapheme));
        var unnamedResult = unnamedRule.Parse(USFlagGrapheme);
        Assert.That(unnamedResult.Success, Is.True);
        Assert.That(unnamedResult.Tree!.Id, Is.EqualTo(unnamedRule.Id));
        Assert.That(unnamedResult.Tree!.Find(unnamedRule), Is.Not.Null);
        Assert.That(unnamedRule.NameOf(unnamedResult.Tree!.Id), Is.EqualTo("OneOf"));

        var namedRule = OneOf(TokenSet.Runes(USFlagGrapheme)).As("flag");
        var namedResult = namedRule.Parse(USFlagGrapheme);
        Assert.That(namedResult.Success, Is.True);
        Assert.That(namedResult.Tree!.Id, Is.EqualTo(namedRule.Id));
        Assert.That(namedResult.Tree!.Find(namedRule), Is.Not.Null);
        Assert.That(namedRule.NameOf(namedResult.Tree!.Id), Is.EqualTo("flag"));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Or_OneOf_skips_when_peek_is_outside_set()
    {
        // OneOf publishes (set, Always, MustBeIn). Or peeks 'b',
        // sees 'b' isn't in {a}, skips OneOf via the shortcut, falls to
        // the literal "b" alternative. The SKIP line proves the
        // shortcut fired.
        var sink = NewSink();
        var rule = Or(OneOf(TokenSet.Runes("a")), Literal("b"));
        var result = rule.Parse("b", new ParseOptions { TraceSink = sink });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(sink.ToString(), Does.Contain("SKIP | OneOf:"));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Or_OneOf_runs_when_peek_is_in_set()
    {
        // Mirror of the previous test: peek 'a' is in {a}, so the
        // shortcut doesn't skip. OneOf runs and matches. No SKIP line
        // for OneOf appears in the trace.
        var sink = NewSink();
        var rule = Or(OneOf(TokenSet.Runes("a")), Literal("b"));
        var result = rule.Parse("a", new ParseOptions { TraceSink = sink });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(sink.ToString(), Does.Not.Contain("SKIP | OneOf:"));
    }

    [Test]
    public void OneOf_in_OneOrMore_matches_after_FormC_canonical_singleton_substitution()
    {
        // U+212B ANGSTROM SIGN is a canonical singleton: under FormC it
        // converts to U+00C5 LATIN CAPITAL LETTER A WITH RING ABOVE.
        // OneOf(TokenSet.Single(0x212B)) compiled with FormC has its set
        // re-projected at Compile time: _set becomes {0x00C5}. The lexer
        // sees input runes in the same form (Parse normalizes the input
        // first), so a token of U+00C5 should match the projected set.
        //
        // The bug: ComputeRuleStartAll runs BEFORE the normalization-form
        // pass that mutates _set, so OneOfRule.FirstConsumedTokens stays
        // pinned to the pre-projection set {0x212B}. OneOrMore's lookahead
        // shortcut peeks the input's first rune (0x00C5), checks it
        // against the cached {0x212B} (not Contains), concludes the inner
        // can't match, and fails the OneOrMore at AtLeast=1. The inner
        // OneOf would have matched if it had been called.
        var rule = OneOrMore(OneOf(TokenSet.Single(0x212B)))
            .Compile(System.Text.NormalizationForm.FormC);
        var result = rule.Parse("Å");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void OneOf_with_pinned_SymbolId_uses_pinned_id_for_single_rune_leaves()
    {
        // .As(SymbolId) is the user's "pin a stable id on this rule"
        // signal, used for serialized parse trees and cross-version id
        // stability. The leaf has to carry that pinned id so
        // Tree.Find(rule), Tree.Is(rule), and any downstream lookup keyed
        // off SymbolId resolve back to the user's pinned value. The same
        // gate that respects .As("name") should respect .As(SymbolId)
        // since both are explicit "find me by reference" signals.
        var pinnedId = new SymbolId(SymbolRanges.CustomRangeStart + 100);
        var rule = OneOf(TokenSet.Ascii.Letters).As(pinnedId);
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Tree!.Id, Is.EqualTo(pinnedId),
            "leaf carries the user-pinned SymbolId, not the rune value");
        Assert.That(result.Tree!.Is(rule), Is.True);
        Assert.That(result.Tree!.Find(rule), Is.Not.Null);
    }

    // -----------------------------------------------------------------
    // Compile-form normalization matrix
    //
    // See GraphemeRuleTests for the full matrix rationale. OneOf's
    // matching data lives in OneOfRule._set rather than a string of
    // expected text; the same shape staleness bugs apply.
    // -----------------------------------------------------------------

    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void OneOf_bare_matches_input_under_form(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        if (row.Category == NormalizationExamples.NormalizationCategory.LoneSurrogateNotNormalizable)
        {
            Assert.Throws<ArgumentException>(() => TokenSet.Runes(row.Source));
            return;
        }

        var rule = OneOf(TokenSet.Runes(row.Source));

        if (!NormalizationExamples.PostFormIsSingleGrapheme(row, form))
        {
            Assert.Throws<InvalidOperationException>(() => rule.Compile(form));
            return;
        }

        rule.Compile(form);
        var result = rule.Parse(row.Source);
        Assert.That(result.Success, Is.True,
            $"OneOf(TokenSet.Runes(\"{NormalizationExamples.Hex(row.Source)}\")).Compile({form}).Parse(\"{NormalizationExamples.Hex(row.Source)}\") " +
            $"should succeed. Error was: {result.ErrorMessage}");
    }

    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void OneOf_in_OneOrMore_matches_input_under_form(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        if (row.Category == NormalizationExamples.NormalizationCategory.LoneSurrogateNotNormalizable)
        {
            // TokenSet.Runes validates each rune at construction and
            // rejects lone surrogates before any rule wraps it. The
            // form parameter is irrelevant; the throw is from
            // TokenSet.Runes itself.
            Assert.Throws<ArgumentException>(() => TokenSet.Runes(row.Source));
            return;
        }

        var rule = OneOrMore(OneOf(TokenSet.Runes(row.Source)));

        if (!NormalizationExamples.PostFormIsSingleGrapheme(row, form))
        {
            // OneOfRule reports the multi-grapheme entry as a Compile-
            // time offender, same as Token does.
            Assert.Throws<InvalidOperationException>(() => rule.Compile(form));
            return;
        }

        rule.Compile(form);
        var result = rule.Parse(row.Source);
        Assert.That(result.Success, Is.True,
            $"OneOrMore(OneOf(TokenSet.Runes(\"{NormalizationExamples.Hex(row.Source)}\"))).Compile({form}).Parse(\"{NormalizationExamples.Hex(row.Source)}\") " +
            $"should succeed. Error was: {result.ErrorMessage}");
    }

    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void OneOf_in_Or_with_fallback_matches_oneof_branch_under_form(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        if (row.Category == NormalizationExamples.NormalizationCategory.LoneSurrogateNotNormalizable)
            return; // covered by OneOf_in_OneOrMore's TokenSet.Runes-throws path
        if (!NormalizationExamples.PostFormIsSingleGrapheme(row, form))
            return; // multi-grapheme post-form is covered by the OneOf_in_OneOrMore Compile-throws path

        var oneOfRule = OneOf(TokenSet.Runes(row.Source)).As("oneOfBranch");
        var fallback = AnyToken().As("fallbackBranch");
        var rule = Or(oneOfRule, fallback);

        rule.Compile(form);
        var result = rule.Parse(row.Source);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.Find(oneOfRule), Is.Not.Null,
            $"Or(OneOf(TokenSet.Runes(\"{NormalizationExamples.Hex(row.Source)}\")), AnyToken).Compile({form}).Parse(\"{NormalizationExamples.Hex(row.Source)}\") " +
            $"matched the AnyToken fallback instead of the OneOf branch.");
    }

    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void OneOf_in_AllOf_with_Eof_matches_full_input_under_form(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        if (row.Category == NormalizationExamples.NormalizationCategory.LoneSurrogateNotNormalizable)
        {
            Assert.Throws<ArgumentException>(() => TokenSet.Runes(row.Source));
            return;
        }

        var rule = And(OneOf(TokenSet.Runes(row.Source)), Eof());

        if (!NormalizationExamples.PostFormIsSingleGrapheme(row, form))
        {
            Assert.Throws<InvalidOperationException>(() => rule.Compile(form));
            return;
        }

        rule.Compile(form);
        var result = rule.Parse(row.Source);
        Assert.That(result.Success, Is.True,
            $"And(OneOf(TokenSet.Runes(\"{NormalizationExamples.Hex(row.Source)}\")), Eof()).Compile({form}).Parse(\"{NormalizationExamples.Hex(row.Source)}\") " +
            $"should succeed. Error was: {result.ErrorMessage}");
    }

    // Matrix-driven SourceRange test. See docs/TestArchitecture.md
    // "Per-rule SourceRange-matrix tests live in each rule's own
    // test file." Shared scaffold lives in SourceRangeMatrixHelper.
    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void SourceRange_for_OneOf_target_after_normalized_Literal_prefix_uses_original_coords(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        SourceRangeMatrixHelper.AssertTargetAfterLiteralPrefix(
            row, form,
            target: OneOf(TokenSet.Single('X')).As("xOne").Preserve(),
            targetText: "X");
    }
}
