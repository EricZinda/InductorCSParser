using System;
using System.Text;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// LiteralIgnoreAsciiCase shares LiteralRule's match shape: a single-
// transaction multi-token compare against a stored _expected string.
// The only difference is ASCII letters compare case-insensitively.
// Compile-time normalization rewrites _expected through TryConvertToForm
// the same way LiteralRule does, so the same staleness bug surface
// applies and the same matrix coverage applies.
[TestFixture]
public class LiteralIgnoreAsciiCaseRuleTests
{
    // -----------------------------------------------------------------
    // Compile-form normalization matrix
    //
    // See GraphemeRuleTests for the full matrix rationale.
    // LiteralIgnoreAsciiCase accepts multi-grapheme expected text by
    // design (same as LiteralRule), so the only Compile-throws case is
    // the lone surrogate where string.Normalize itself throws. The
    // ASCII case-folding behavior is orthogonal to normalization (it
    // only changes the runtime compare, not the cached _expected
    // string), so the matrix doesn't need separate upper/lower
    // variants per row.
    // -----------------------------------------------------------------

    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void LiteralIgnoreAsciiCase_bare_matches_input_under_form(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        var rule = LiteralIgnoreAsciiCase(row.Source);

        if (row.Category == NormalizationExamples.NormalizationCategory.LoneSurrogateNotNormalizable)
        {
            Assert.Throws<InvalidOperationException>(() => rule.Compile(form));
            return;
        }

        rule.Compile(form);
        var result = rule.Parse(row.Source);
        Assert.That(result.Success, Is.True,
            $"LiteralIgnoreAsciiCase(\"{NormalizationExamples.Hex(row.Source)}\").Compile({form}).Parse(\"{NormalizationExamples.Hex(row.Source)}\") " +
            $"should succeed. Error was: {result.ErrorMessage}");
    }

    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void LiteralIgnoreAsciiCase_in_OneOrMore_matches_input_under_form(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        var rule = OneOrMore(LiteralIgnoreAsciiCase(row.Source));

        if (row.Category == NormalizationExamples.NormalizationCategory.LoneSurrogateNotNormalizable)
        {
            Assert.Throws<InvalidOperationException>(() => rule.Compile(form));
            return;
        }

        rule.Compile(form);
        var result = rule.Parse(row.Source);
        Assert.That(result.Success, Is.True,
            $"OneOrMore(LiteralIgnoreAsciiCase(\"{NormalizationExamples.Hex(row.Source)}\")).Compile({form}).Parse(\"{NormalizationExamples.Hex(row.Source)}\") " +
            $"should succeed. Error was: {result.ErrorMessage}");
    }

    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void LiteralIgnoreAsciiCase_in_Or_with_fallback_matches_literal_branch_under_form(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        if (row.Category == NormalizationExamples.NormalizationCategory.LoneSurrogateNotNormalizable)
            return; // covered by LiteralIgnoreAsciiCase_in_OneOrMore's Compile-throws path

        var literalRule = LiteralIgnoreAsciiCase(row.Source).Preserve().As("literalBranch");
        var fallback = AnyToken().As("fallbackBranch");
        var rule = Or(literalRule, fallback);

        rule.Compile(form);
        var result = rule.Parse(row.Source);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.Find(literalRule), Is.Not.Null,
            $"Or(LiteralIgnoreAsciiCase(\"{NormalizationExamples.Hex(row.Source)}\"), AnyToken).Compile({form}).Parse(\"{NormalizationExamples.Hex(row.Source)}\") " +
            $"matched the AnyToken fallback instead of the LiteralIgnoreAsciiCase branch.");
    }

    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void LiteralIgnoreAsciiCase_in_AllOf_with_Eof_matches_full_input_under_form(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        var rule = And(LiteralIgnoreAsciiCase(row.Source), Eof());

        if (row.Category == NormalizationExamples.NormalizationCategory.LoneSurrogateNotNormalizable)
        {
            Assert.Throws<InvalidOperationException>(() => rule.Compile(form));
            return;
        }

        rule.Compile(form);
        var result = rule.Parse(row.Source);
        Assert.That(result.Success, Is.True,
            $"And(LiteralIgnoreAsciiCase(\"{NormalizationExamples.Hex(row.Source)}\"), Eof()).Compile({form}).Parse(\"{NormalizationExamples.Hex(row.Source)}\") " +
            $"should succeed. Error was: {result.ErrorMessage}");
    }

    // Matrix-driven SourceRange test. See docs/TestArchitecture.md
    // "Per-rule SourceRange-matrix tests live in each rule's own
    // test file." Shared scaffold lives in SourceRangeMatrixHelper.
    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void SourceRange_for_LiteralIgnoreAsciiCase_target_after_normalized_Literal_prefix_uses_original_coords(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        SourceRangeMatrixHelper.AssertTargetAfterLiteralPrefix(
            row, form,
            target: LiteralIgnoreAsciiCase("xyz").As("ci").Preserve(),
            targetText: "XYZ");
    }

    [Test]
    public void SourceText_on_LiteralIgnoreAsciiCase_returns_matched_text_under_every_FlattenType()
    {
        // Input "XYZ" matches the rule "xyz" case-insensitively. The
        // matched text is whatever the input contained ("XYZ"), not
        // the rule's spelling ("xyz"). SourceText preserves the input
        // form.
        SourceTextFlattenTypeMatrixHelper.AssertSourceTextUnderEveryFlattenType(
            ruleBuilder: () => LiteralIgnoreAsciiCase("xyz"),
            input: "XYZ",
            expectedSourceText: "XYZ");
    }

    [Test]
    public void Or_admits_case_variant_when_first_grapheme_is_multi_rune_under_FormD()
    {
        // FormD decomposes a precomposed letter+accent like 'é' (U+00E9)
        // into the two-rune cluster "é" (e + combining acute).
        // LiteralIgnoreAsciiCase("éclair").Compile(FormD) rewrites the
        // rule's stored text to "éclair", so its first grapheme
        // becomes a multi-rune cluster whose first rune is the ASCII
        // letter 'e'. The parse-time AsciiCaseEquals compare admits the
        // opposite-case input "Éclair" (decomposed "Éclair"),
        // because the e/E pair case-folds the same way 'e'/'E' would
        // standalone. The Or lookahead shortcut has to agree, or it
        // skips the case-insensitive branch on a peek whose first rune
        // is the opposite ASCII case.
        var literalBranch = LiteralIgnoreAsciiCase("éclair").As("literalBranch");
        var fallback = AnyToken().As("fallback");
        var rule = Or(literalBranch, fallback);

        rule.Compile(NormalizationForm.FormD);
        var result = rule.Parse("Éclair");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Find(literalBranch), Is.Not.Null,
            "Or should have committed to the LiteralIgnoreAsciiCase branch " +
            "instead of skipping it via the lookahead shortcut.");
    }

    [Test]
    public void OneOrMore_admits_case_variant_when_first_grapheme_is_multi_rune_under_FormD()
    {
        // Same shape as the Or test, but via BetweenInclusiveRule's
        // own lookahead shortcut on Inner. With Inner.AtLeast >= 1,
        // an incorrect skip turns into a "count=0" failure for the
        // whole repetition.
        var rule = OneOrMore(LiteralIgnoreAsciiCase("éclair"));

        rule.Compile(NormalizationForm.FormD);
        var result = rule.Parse("Éclair");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }
}
