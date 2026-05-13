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
public class NoneOfRuleTests
{
    [Test]
    public void NoneOf_matches_a_rune_outside_the_set()
    {
        // 'x' isn't a digit, so NoneOf(Digits) succeeds on it.
        var rule = NoneOf(TokenSet.Digits);
        var result = rule.Parse("x");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("x"));
    }

    [Test]
    public void NoneOf_fails_when_rune_is_in_the_set()
    {
        // '5' is a digit, so NoneOf(Digits) fails at offset 0.
        var rule = NoneOf(TokenSet.Digits).WithError("no digits here");
        var result = rule.Parse("5");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("no digits here"));
    }

    [Test]
    public void NoneOf_fails_at_EOF()
    {
        // EOF isn't "a rune not in the set". It's no rune at all. Fail.
        var rule = NoneOf(TokenSet.Digits).WithError("wanted a non-digit");
        var result = rule.Parse("");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("wanted a non-digit"));
    }

    [Test]
    public void NoneOf_matches_multi_rune_Token()
    {
        // A multi-rune grapheme like LatinEAcuteGrapheme arrives as a
        // single token whose RuneValue is -1. The "not a single rune in
        // the set" predicate is trivially true for it: the token isn't
        // any single rune at all. This is the property that lets
        // ZeroOrMore(NoneOf(...)) sweep up arbitrary Unicode text.
        // NormalizeInput = null so the decomposed "e\u0301" arrives at the
        // lexer verbatim. The default NFC would compose it to "\u00E9" and
        // collapse this test's "multi-rune grapheme" premise.
        var rule = NoneOf(TokenSet.Ascii.Letters);
        rule.Compile(null);
        var result = rule.Parse(LatinEAcuteGrapheme);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(LatinEAcuteGrapheme));
    }

    [Test]
    public void NoneOf_sweeps_passthrough_text_up_to_a_delimiter()
    {
        // The pass-through-text idiom: ZeroOrMore(NoneOf(stopSet)) matches
        // everything that isn't in the stop set, then the surrounding rule
        // handles the stop character. Here the stop is a single '\n'.
        //
        // WARNING: this idiom is LF-only under grapheme tokenization.
        // A CRLF grapheme passes NoneOf unconditionally (it isn't a
        // single rune, so it can't be in any single-rune set), which
        // means the sweep silently consumes the CRLF and the trailing
        // Token('\n') terminator then fails. For real line-based
        // grammars, don't use NoneOf as the line sweep at all. Use
        // Not(EndOfLine()) + AnyToken() for the sweep and EndOfLine()
        // for the terminator, which together handle CRLF, LF, CR, NEL,
        // LS, and PS as one terminator each.
        // See docs/UnicodeGotchas.md § "CRLF Line Endings".
        var rule = And(
            ZeroOrMore(NoneOf(TokenSet.Single('\n'))),
            Token('\n'));

        // PreserveAllSymbols keeps the trailing Token('\n') in the
        // tree so Tree.ToString reproduces the full matched line.
        var result = rule.Parse("hello world\n",
            new ParseOptions { PreserveAllSymbols = true });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("hello world\n"));
    }

    [Test]
    [RecursiveEngineOnly]
    public void NoneOf_trace_success_produces_expected_output()
    {
        var sink = NewSink();
        NoneOf(TokenSet.Ascii.Digits).Parse("x",
            new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: 'x', Consumed: 1",
            "   SUCC | NoneOf: found 'x', wanted one not in '[0-9]'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    [RecursiveEngineOnly]
    public void NoneOf_trace_failure_produces_expected_output()
    {
        var sink = NewSink();
        NoneOf(TokenSet.Ascii.Digits).Parse("5",
            new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: '5', Consumed: 1",
            "   FAIL | NoneOf: found '5', wanted one not in '[0-9]'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Sealed_NoneOf_rejects_Flatten()
    {
        var rule = NoneOf("abc");
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.Flatten(FlattenType.Preserve));
    }

    [Test]
    public void Sealed_NoneOf_rejects_WithError()
    {
        var rule = NoneOf("abc");
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.WithError("late"));
    }

    [Test]
    public void Sealed_NoneOf_rejects_As()
    {
        var rule = NoneOf("abc");
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.As("late"));
    }

    // Multi-rune grapheme support -------------------------------------------

    [Test]
    public void NoneOf_with_TokenSet_Empty_admits_a_multi_rune_grapheme_without_NRE()
    {
        // Programmatically-constructed sets sometimes wind up empty (a
        // conditional stop list that nothing got added to, an
        // intersection that came out empty, etc.). NoneOf(TokenSet.Empty)
        // is then the "match any token" rule. TokenSet.Empty is the
        // public name for default(TokenSet), whose internal
        // _multiRuneGraphemes field is null because nothing ever ran
        // the constructor that coalesces null to Array.Empty<string>().
        // ContainsToken's multi-rune branch reads _multiRuneGraphemes
        // .Length directly, so a multi-rune grapheme arriving as the
        // next token throws NullReferenceException instead of returning
        // false. Compile(null) keeps the decomposed grapheme out of NFC
        // composition so the lexer hands the rule a true two-char token.
        var rule = NoneOf(TokenSet.Empty);
        rule.Compile(null);

        var result = rule.Parse(LatinEAcuteGrapheme);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(LatinEAcuteGrapheme));
    }

    [Test]
    public void NoneOf_with_multi_rune_set_rejects_the_listed_Token()
    {
        // A multi-rune set as the exclude list. The flag arrives as
        // one token and NoneOf finds it in the multi-rune array, so
        // it fails. Other multi-rune graphemes pass.
        var rule = NoneOf(TokenSet.Graphemes(USFlagGrapheme));

        Assert.That(rule.Parse(USFlagGrapheme).Success, Is.False);
        Assert.That(rule.Parse(WomanShruggingGrapheme).Success, Is.True);
        // Single-rune tokens not in the set's runes also pass.
        Assert.That(rule.Parse("a").Success, Is.True);
    }

    [Test]
    public void NoneOf_with_mixed_set_rejects_both_listed_runes_and_listed_graphemes()
    {
        // The mixed-set version of the previous test: include a
        // letter range and a multi-rune entry. Tokens that hit
        // either get rejected.
        var rule = NoneOf(TokenSet.Ascii.Letters | TokenSet.Graphemes(USFlagGrapheme));

        Assert.That(rule.Parse("a").Success, Is.False, "letters are rejected");
        Assert.That(rule.Parse(USFlagGrapheme).Success, Is.False, "the flag grapheme is rejected");
        Assert.That(rule.Parse("1").Success, Is.True, "digits are not in the set");
        Assert.That(rule.Parse(WomanShruggingGrapheme).Success, Is.True, "other multi-rune graphemes pass");
    }

    [Test]
    public void Unnamed_NoneOf_uses_the_rune_value_as_the_leaf_id_for_single_rune_tokens()
    {
        // Same Name-gated leaf-Id story as OneOfRule.
        var rule = NoneOf(TokenSet.Ascii.Digits);
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Tree!.Id.Value, Is.EqualTo(0x61));
    }

    [Test]
    public void Named_NoneOf_uses_rule_id_so_Find_resolves_the_named_rule()
    {
        // .As("name") makes the rule findable in the tree, regardless of
        // whether the matched grapheme is one rune or several.
        var notDigit = NoneOf(TokenSet.Ascii.Digits).As("notDigit");
        var result = notDigit.Parse("a");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Tree!.Is(notDigit), Is.True);
        Assert.That(result.Tree!.Find(notDigit), Is.Not.Null);
    }

    [Test]
    public void NoneOf_with_pinned_SymbolId_uses_pinned_id_for_single_rune_leaves()
    {
        // .As(SymbolId) is the user's "pin a stable id" signal, parallel
        // to .As("name") for findability. The leaf has to carry the
        // pinned id so Tree.Find / Tree.Is resolve through the user's
        // pinned reference. Same shape as the OneOf pinned-id test.
        var pinnedId = new SymbolId(SymbolRanges.CustomRangeStart + 101);
        var rule = NoneOf(TokenSet.Ascii.Digits).As(pinnedId);
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Tree!.Id, Is.EqualTo(pinnedId),
            "leaf carries the user-pinned SymbolId, not the rune value");
        Assert.That(result.Tree!.Is(rule), Is.True);
        Assert.That(result.Tree!.Find(rule), Is.Not.Null);
    }

    [Test]
    public void NoneOf_with_multi_rune_match_uses_rule_id_regardless_of_naming()
    {
        // The matched cluster (regional-indicator US flag) is multi-rune,
        // so Token.RuneValue == -1 and the leaf carries the rule's own Id
        // whether the rule is named or not.
        var unnamedRule = NoneOf(TokenSet.Ascii.Digits);
        var unnamedResult = unnamedRule.Parse(USFlagGrapheme);
        Assert.That(unnamedResult.Success, Is.True);
        Assert.That(unnamedResult.Tree!.Id, Is.EqualTo(unnamedRule.Id));
        Assert.That(unnamedResult.Tree!.Find(unnamedRule), Is.Not.Null);
        Assert.That(unnamedRule.NameOf(unnamedResult.Tree!.Id), Is.EqualTo("NoneOf"));

        var namedRule = NoneOf(TokenSet.Ascii.Digits).As("notDigit");
        var namedResult = namedRule.Parse(USFlagGrapheme);
        Assert.That(namedResult.Success, Is.True);
        Assert.That(namedResult.Tree!.Id, Is.EqualTo(namedRule.Id));
        Assert.That(namedResult.Tree!.Find(namedRule), Is.Not.Null);
        Assert.That(namedRule.NameOf(namedResult.Tree!.Id), Is.EqualTo("notDigit"));
    }

    [Test]
    public void Or_NoneOf_admits_multi_rune_cluster_starting_with_set_rune()
    {
        // OrRule peeks the next token (one grapheme cluster) and asks
        // each child CannotMatchLookahead. NoneOf publishes
        // Polarity.MustNotBeIn with its set as the fail-set: skip iff
        // peek IS in the set. The peek "a"+combining-acute is one
        // multi-rune cluster under Compile(null); ContainsToken on the
        // rune-only set {'a'} returns false (cluster isn't in the
        // multi-rune entries, isn't single-rune 'a' either), so the
        // shortcut doesn't skip and NoneOf actually runs and matches.
        // If ComputeRuleStart wrongly published a rune-only complement,
        // the shortcut would skip NoneOf and the parse would fail.
        var rule = Or(NoneOf(TokenSet.Single('a')), Literal("zzzZZZ"));
        rule.Compile(null);
        var result = rule.Parse("a" + CombiningAcuteText);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("a" + CombiningAcuteText));
    }

    [Test]
    public void OneOrMore_NoneOf_admits_multi_rune_cluster_starting_with_set_rune()
    {
        // BetweenInclusiveRule's shortcut shape: peek the next cluster,
        // ask Inner.CannotMatchLookahead. Same MustNotBeIn semantics
        // apply: the multi-rune cluster "a"+combining-acute isn't
        // strictly in the rune-only set {'a'}, so the shortcut doesn't
        // skip, the loop iterates, NoneOf reads the cluster and
        // accepts it, count goes from 0 to 1, success. Pre-fix this
        // failed because the rune-set complement excluded 'a' and the
        // shortcut wrongly took the count==0 branch.
        var rule = OneOrMore(NoneOf(TokenSet.Single('a')));
        rule.Compile(null);
        var result = rule.Parse("a" + CombiningAcuteText);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("a" + CombiningAcuteText));
    }

    [Test]
    public void Or_NoneOf_skips_when_peek_is_strictly_in_set()
    {
        // The other direction: when peek IS strictly in NoneOf's
        // fail-set, the shortcut SHOULD skip. Here peek is the
        // single-rune cluster 'a', NoneOf({'a'}) would fail at
        // runtime, and the shortcut precisely skips it. Wrap in
        // Or with a literal fallback so we can observe that
        // NoneOf was passed over and the literal alternative ran.
        var rule = Or(NoneOf(TokenSet.Single('a')), Literal("a"));
        rule.Compile(null);
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    // -----------------------------------------------------------------
    // Compile-form normalization matrix
    //
    // See GraphemeRuleTests for the full matrix rationale. NoneOf has
    // the inverse polarity of OneOf: feeding the source through a rule
    // that rejects exactly the source's runes should FAIL after a
    // correct Compile (the post-form rune IS in the post-form stop
    // set). The staleness bug surfaces as NoneOf wrongly succeeding
    // because the cached set still holds the pre-form entry while the
    // input arrives in the post-form shape.
    // -----------------------------------------------------------------

    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void NoneOf_bare_does_not_match_source_under_form(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        if (row.Category == NormalizationExamples.NormalizationCategory.LoneSurrogateNotNormalizable)
        {
            Assert.Throws<ArgumentException>(() => TokenSet.Graphemes(row.Source));
            return;
        }

        var rule = NoneOf(TokenSet.Graphemes(row.Source));

        if (!NormalizationExamples.PostFormIsSingleGrapheme(row, form))
        {
            Assert.Throws<InvalidOperationException>(() => rule.Compile(form));
            return;
        }

        rule.Compile(form);
        var result = rule.Parse(row.Source);
        Assert.That(result.Success, Is.False,
            $"NoneOf(TokenSet.Runes(\"{NormalizationExamples.Hex(row.Source)}\")).Compile({form}).Parse(\"{NormalizationExamples.Hex(row.Source)}\") " +
            $"should fail. A pass means the post-form rune isn't in the cached stop set.");
    }

    // OneOrMore wrapping also exercises the BetweenInclusive shortcut
    // path on NoneOf's FirstConsumedTokens (the complement of the stop
    // set, which has its own staleness bug shape).
    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void NoneOf_in_OneOrMore_does_not_match_source_under_form(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        if (row.Category == NormalizationExamples.NormalizationCategory.LoneSurrogateNotNormalizable)
        {
            Assert.Throws<ArgumentException>(() => TokenSet.Graphemes(row.Source));
            return;
        }

        var rule = OneOrMore(NoneOf(TokenSet.Graphemes(row.Source)));

        if (!NormalizationExamples.PostFormIsSingleGrapheme(row, form))
        {
            Assert.Throws<InvalidOperationException>(() => rule.Compile(form));
            return;
        }

        rule.Compile(form);
        var result = rule.Parse(row.Source);
        Assert.That(result.Success, Is.False,
            $"OneOrMore(NoneOf(TokenSet.Runes(\"{NormalizationExamples.Hex(row.Source)}\"))).Compile({form}).Parse(\"{NormalizationExamples.Hex(row.Source)}\") " +
            $"should fail (input rune is in the stop set after normalization). " +
            $"A pass here means NoneOf's _set is still in its pre-normalization shape.");
    }

    // NoneOf in Or with AnyToken fallback. Correct: NoneOf fails (input
    // is in stop set), Or falls through to AnyToken which matches. The
    // bug shape (stale _set) flips the outcome: NoneOf wrongly succeeds
    // and the fallback never runs.
    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void NoneOf_in_Or_with_fallback_takes_fallback_branch_under_form(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        if (row.Category == NormalizationExamples.NormalizationCategory.LoneSurrogateNotNormalizable)
            return; // covered by NoneOf_in_OneOrMore's TokenSet.Runes-throws path
        if (!NormalizationExamples.PostFormIsSingleGrapheme(row, form))
            return; // multi-grapheme post-form is covered by the NoneOf_in_OneOrMore Compile-throws path

        var noneOfRule = NoneOf(TokenSet.Graphemes(row.Source)).As("noneOfBranch");
        var fallback = AnyToken().As("fallbackBranch");
        var rule = Or(noneOfRule, fallback);

        rule.Compile(form);
        var result = rule.Parse(row.Source);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.Find(fallback), Is.Not.Null,
            $"Or(NoneOf(TokenSet.Runes(\"{NormalizationExamples.Hex(row.Source)}\")), AnyToken).Compile({form}).Parse(\"{NormalizationExamples.Hex(row.Source)}\") " +
            $"didn't take the AnyToken fallback. NoneOf wrongly succeeded, " +
            $"which means its _set didn't get rewritten under {form}.");
    }

    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void NoneOf_in_AllOf_with_Eof_does_not_match_source_under_form(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        if (row.Category == NormalizationExamples.NormalizationCategory.LoneSurrogateNotNormalizable)
        {
            Assert.Throws<ArgumentException>(() => TokenSet.Graphemes(row.Source));
            return;
        }

        var rule = And(NoneOf(TokenSet.Graphemes(row.Source)), Eof());

        if (!NormalizationExamples.PostFormIsSingleGrapheme(row, form))
        {
            Assert.Throws<InvalidOperationException>(() => rule.Compile(form));
            return;
        }

        rule.Compile(form);
        var result = rule.Parse(row.Source);
        Assert.That(result.Success, Is.False,
            $"And(NoneOf(TokenSet.Runes(\"{NormalizationExamples.Hex(row.Source)}\")), Eof()).Compile({form}).Parse(\"{NormalizationExamples.Hex(row.Source)}\") " +
            $"should fail. A pass means NoneOf's _set didn't get rewritten under {form}.");
    }

    // Matrix-driven SourceRange test. See docs/TestArchitecture.md
    // "Per-rule SourceRange-matrix tests live in each rule's own
    // test file." Shared scaffold lives in SourceRangeMatrixHelper.
    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void SourceRange_for_NoneOf_target_after_normalized_Literal_prefix_uses_original_coords(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        SourceRangeMatrixHelper.AssertTargetAfterLiteralPrefix(
            row, form,
            target: NoneOf("Y").As("notY").Preserve(),
            targetText: "X");
    }

    [Test]
    public void SourceText_on_NoneOf_returns_matched_text_under_every_FlattenType()
    {
        SourceTextFlattenTypeMatrixHelper.AssertSourceTextUnderEveryFlattenType(
            ruleBuilder: () => NoneOf("Y"),
            input: "X",
            expectedSourceText: "X");
    }
}
