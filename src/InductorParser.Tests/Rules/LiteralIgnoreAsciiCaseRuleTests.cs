using System;
using System.Text;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// LiteralIgnoreAsciiCase shares LiteralRule's match shape: a single-
// transaction multi-token compare against a stored _expected string.
// The only difference is ASCII letters compare case-insensitively, and
// the pattern is restricted to ASCII-only at construction. Non-ASCII
// patterns throw ArgumentException up front because ASCII case-folding
// doesn't apply to them and admitting them would mislead the reader.
[TestFixture]
public class LiteralIgnoreAsciiCaseRuleTests
{
    // -----------------------------------------------------------------
    // Compile-form normalization matrix
    //
    // LiteralIgnoreAsciiCase's pattern is restricted to ASCII, so the
    // matrix splits into two branches per row:
    //   * Pure-ASCII row: the pattern constructs, compiles, and parses.
    //   * Non-ASCII row: the pattern is rejected at construction, before
    //     Compile or Parse ever run.
    // The "AlreadyNormalized" category is the only one that holds pure
    // ASCII content ("a"); every other row's Source contains at least
    // one non-ASCII char by design (the matrix was built to exercise
    // Unicode normalization behavior). The branching below keeps both
    // halves under the same parameterized fixture so a future row that
    // adds an ASCII variant gets covered automatically.
    // -----------------------------------------------------------------

    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void LiteralIgnoreAsciiCase_bare_matches_input_under_form(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        if (!IsAsciiOnly(row.Source))
        {
            Assert.Throws<ArgumentException>(() => LiteralIgnoreAsciiCase(row.Source));
            return;
        }

        var rule = LiteralIgnoreAsciiCase(row.Source);
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
        if (!IsAsciiOnly(row.Source))
        {
            Assert.Throws<ArgumentException>(() => LiteralIgnoreAsciiCase(row.Source));
            return;
        }

        var rule = OneOrMore(LiteralIgnoreAsciiCase(row.Source));
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
        if (!IsAsciiOnly(row.Source))
        {
            Assert.Throws<ArgumentException>(() => LiteralIgnoreAsciiCase(row.Source));
            return;
        }

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
        if (!IsAsciiOnly(row.Source))
        {
            Assert.Throws<ArgumentException>(() => LiteralIgnoreAsciiCase(row.Source));
            return;
        }

        var rule = And(LiteralIgnoreAsciiCase(row.Source), Eof());
        rule.Compile(form);
        var result = rule.Parse(row.Source);
        Assert.That(result.Success, Is.True,
            $"And(LiteralIgnoreAsciiCase(\"{NormalizationExamples.Hex(row.Source)}\"), Eof()).Compile({form}).Parse(\"{NormalizationExamples.Hex(row.Source)}\") " +
            $"should succeed. Error was: {result.ErrorMessage}");
    }

    // Matrix-driven SourceRange test. See docs/TestArchitecture.md
    // "Per-rule SourceRange-matrix tests live in each rule's own
    // test file." Shared scaffold lives in SourceRangeMatrixHelper.
    // The target is a fixed ASCII literal ("xyz"), so this row's
    // ASCII-pattern restriction is satisfied independently of
    // row.Source (which is the prefix the helper splices in front).
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

    // Construction rejects any char outside 0x00..0x7F. Three shapes:
    // a non-ASCII letter (German sharp-s), a combining mark with an
    // ASCII base, and a lone high surrogate. All three are common
    // failure modes for grammars that reach for LiteralIgnoreAsciiCase
    // when they actually want Literal.
    [Test]
    public void LiteralIgnoreAsciiCase_rejects_non_ascii_letter_at_construction()
    {
        var exception = Assert.Throws<ArgumentException>(() => LiteralIgnoreAsciiCase("straße"));
        Assert.That(exception!.Message, Does.Contain("ASCII-only"));
        Assert.That(exception.Message, Does.Contain("U+00DF"));
        Assert.That(exception.Message, Does.Contain("index 4"));
    }

    [Test]
    public void LiteralIgnoreAsciiCase_rejects_combining_mark_at_construction()
    {
        string decomposed = "a" + (char)0x0301;
        var exception = Assert.Throws<ArgumentException>(() => LiteralIgnoreAsciiCase(decomposed));
        Assert.That(exception!.Message, Does.Contain("ASCII-only"));
        Assert.That(exception.Message, Does.Contain("U+0301"));
        Assert.That(exception.Message, Does.Contain("index 1"));
    }

    [Test]
    public void LiteralIgnoreAsciiCase_rejects_surrogate_at_construction()
    {
        string surrogatePrefix = "\uD800X";
        var exception = Assert.Throws<ArgumentException>(() => LiteralIgnoreAsciiCase(surrogatePrefix));
        Assert.That(exception!.Message, Does.Contain("ASCII-only"));
        Assert.That(exception.Message, Does.Contain("U+D800"));
    }

    [Test]
    public void LiteralIgnoreAsciiCase_accepts_ascii_control_characters_at_construction()
    {
        // 0x00..0x1F and 0x7F are inside the ASCII range and don't
        // trip the ASCII validator. They're not ASCII letters, so they
        // go through the bit-exact compare path at parse time.
        Assert.DoesNotThrow(() => LiteralIgnoreAsciiCase("a"));
        Assert.DoesNotThrow(() => LiteralIgnoreAsciiCase(" abc"));
        var rule = LiteralIgnoreAsciiCase("\r\n");
        Assert.That(rule.Parse("\r\n").Success, Is.True);
    }

    private static bool IsAsciiOnly(string text)
    {
        for (int index = 0; index < text.Length; index++)
            if (text[index] > 0x7F)
                return false;
        return true;
    }
}
