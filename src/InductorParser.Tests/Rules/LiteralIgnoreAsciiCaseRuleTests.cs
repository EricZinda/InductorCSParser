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
}
