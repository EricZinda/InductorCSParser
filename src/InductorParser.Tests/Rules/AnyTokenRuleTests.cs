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
public class AnyTokenRuleTests
{
    [Test]
    public void AnyToken_matches_a_single_ascii_character()
    {
        var rule = AnyToken();
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("a"));
    }

    [Test]
    public void AnyToken_fails_at_EOF()
    {
        var rule = AnyToken().WithError("wanted any character");
        var result = rule.Parse("");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("wanted any character"));
    }

    [Test]
    public void AnyToken_under_grapheme_lexer_consumes_whole_Token()
    {
        // LatinEAcuteGrapheme is one token (two runes, one grapheme).
        // AnyToken consumes the whole token as a single match.
        //
        // NormalizeInput = null so the two-rune decomposed form survives to
        // the lexer. The default NFC would compose to a one-rune grapheme
        // and undo this test's premise.
        var rule = AnyToken();
        rule.Compile(null);
        var result = rule.Parse(LatinEAcuteGrapheme);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(LatinEAcuteGrapheme));
    }

    [Test]
    public void AnyToken_repeated_consumes_arbitrary_text_to_EOF()
    {
        var rule = ZeroOrMore(AnyToken());
        var result = rule.Parse("anything at all 123 " + GuitarGrapheme);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.ToString(),
            Is.EqualTo("anything at all 123 " + GuitarGrapheme));
    }

    [Test]
    [RecursiveEngineOnly]
    public void AnyToken_trace_success_produces_expected_output()
    {
        var sink = NewSink();
        AnyToken().Parse("q", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: 'q', Consumed: 1",
            "   SUCC | AnyToken: found 'q'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    [RecursiveEngineOnly]
    public void AnyToken_trace_failure_produces_expected_output()
    {
        var sink = NewSink();
        AnyToken().Parse("", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: '<EOF>', Consumed: 0",
            "   FAIL | AnyToken: found '<EOF>'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Sealed_AnyToken_rejects_Flatten()
    {
        var rule = AnyToken();
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.Flatten(FlattenType.Preserve));
    }

    [Test]
    public void Sealed_AnyToken_rejects_WithError()
    {
        var rule = AnyToken();
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.WithError("late"));
    }

    [Test]
    public void Sealed_AnyToken_rejects_As()
    {
        var rule = AnyToken();
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.As("late"));
    }

    [Test]
    public void Unnamed_AnyToken_uses_the_rune_value_as_the_leaf_id_for_single_rune_tokens()
    {
        // Same Name-gated leaf-Id story as OneOfRule: an unnamed rule with
        // a single-rune match uses the rune as the leaf id so tree
        // consumers can dispatch on the rune.
        var rule = AnyToken();
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Tree!.Id.Value, Is.EqualTo(0x61));
    }

    [Test]
    public void Named_AnyToken_uses_rule_id_so_Find_resolves_the_named_rule()
    {
        // .As("name") makes the rule findable via Tree.Find / Tree.Is /
        // NameOf, regardless of whether the matched grapheme is one rune
        // or several.
        var anyChar = AnyToken().As("anyChar");
        var result = anyChar.Parse("a");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Tree!.Is(anyChar), Is.True);
        Assert.That(result.Tree!.Find(anyChar), Is.Not.Null);
    }

    [Test]
    public void AnyToken_with_pinned_SymbolId_uses_pinned_id_for_single_rune_leaves()
    {
        // .As(SymbolId) is the user's "pin a stable id" signal, parallel
        // to .As("name") for findability. The leaf has to carry the
        // pinned id so Tree.Find / Tree.Is resolve through the user's
        // pinned reference. Same shape as the OneOf pinned-id test.
        var pinnedId = new SymbolId(SymbolRanges.CustomRangeStart + 102);
        var rule = AnyToken().As(pinnedId);
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Tree!.Id, Is.EqualTo(pinnedId),
            "leaf carries the user-pinned SymbolId, not the rune value");
        Assert.That(result.Tree!.Is(rule), Is.True);
        Assert.That(result.Tree!.Find(rule), Is.Not.Null);
    }

    [Test]
    public void AnyToken_with_multi_rune_match_uses_rule_id_regardless_of_naming()
    {
        // A multi-rune cluster (regional-indicator US flag) has
        // Token.RuneValue == -1 because two runes don't fit in one int,
        // so the leaf carries the rule's own Id whether the rule is named
        // or not.
        var unnamedRule = AnyToken();
        var unnamedResult = unnamedRule.Parse(USFlagGrapheme);
        Assert.That(unnamedResult.Success, Is.True);
        Assert.That(unnamedResult.Tree!.Id, Is.EqualTo(unnamedRule.Id));
        Assert.That(unnamedResult.Tree!.Find(unnamedRule), Is.Not.Null);
        Assert.That(unnamedRule.NameOf(unnamedResult.Tree!.Id), Is.EqualTo("AnyToken"));

        var namedRule = AnyToken().As("anyChar");
        var namedResult = namedRule.Parse(USFlagGrapheme);
        Assert.That(namedResult.Success, Is.True);
        Assert.That(namedResult.Tree!.Id, Is.EqualTo(namedRule.Id));
        Assert.That(namedResult.Tree!.Find(namedRule), Is.Not.Null);
        Assert.That(namedRule.NameOf(namedResult.Tree!.Id), Is.EqualTo("anyChar"));
    }

    // Matrix-driven SourceRange test. See docs/TestArchitecture.md
    // "Per-rule SourceRange-matrix tests live in each rule's own
    // test file." Shared scaffold lives in SourceRangeMatrixHelper.
    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void SourceRange_for_AnyToken_target_after_normalized_Literal_prefix_uses_original_coords(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        SourceRangeMatrixHelper.AssertTargetAfterLiteralPrefix(
            row, form,
            target: AnyToken().As("any").Preserve(),
            targetText: "X");
    }
}
