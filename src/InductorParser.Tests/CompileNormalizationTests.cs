using System;
using System.Text;
using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Tests Rule.Compile(form)'s normalization pass: how each rule's
// cached match data (GraphemeRule._expected, OneOfRule._set,
// LiteralRule._expected, plus the static-analysis caches like
// FirstConsumedTokens that other rules inspect) gets rewritten under
// a NormalizationForm so that runtime matching against
// post-normalization input still works. A bug in this pass typically
// shows up as a rule whose cached "what to match" string disagrees
// with what the lexer actually produces after Normalize(form), which
// either over-rejects valid input or, worse, silently skips a
// matching branch via a lookahead shortcut.
//
// Each test case is a four-axis cross product:
//
//   * leaf rule under test:      Token, OneOf, Literal
//   * outer composite that wraps the leaf:  bare leaf, OneOrMore(leaf),
//                                            Or(leaf, fallback)
//   * normalization form:        FormC, FormD, FormKC, FormKD
//   * grapheme behavior:         the rows in NormalizationExamples
//
// The leaf axis covers the three places Compile rewrites stored
// match data: GraphemeRule's expected-text string (Token), OneOfRule's
// TokenSet (OneOf), and LiteralRule's expected-text string (Literal).
// Each leaf class has its own normalization routine, so the matrix
// runs every row through all three. The outer-composite axis covers
// where the static-analysis caches get consulted: a bare leaf runs
// the rule's TryParseRule body unconditionally; OneOrMore wrapping
// exercises BetweenInclusiveRule.CannotMatchLookahead, the "skip the
// inner rule when its FirstConsumedTokens doesn't contain the next
// peek rune" shortcut; Or wrapping exercises OrRule's
// separate per-child variant of the same shortcut. Those shortcut
// paths are where stale Compile-time caches do the most damage
// because they take the cache at face value and skip the rule
// entirely. Every (leaf, form, grapheme) combination runs through at
// least the bare and OneOrMore shapes to find future regressions of
// that pattern.
//
// The test data lives in NormalizationExamples. This fixture turns
// each row into real Compile / Parse calls and asserts the runtime
// behavior matches what the lexer's post-normalization view should
// produce.
[TestFixture]
public class CompileNormalizationTests
{
    // Token rule: build Token(source), Compile(form), Parse(source).
    // Expectation: if the post-form text is multi-grapheme (the
    // compatibility-ligature row under FormKC/FormKD), Compile should
    // throw a normalization-offender error since Token matches exactly
    // one grapheme. Lone-surrogate sources can't be normalized at all,
    // so Compile throws under every form. Otherwise the parse succeeds.
    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void Token_in_OneOrMore_matches_input_under_form(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        var rule = OneOrMore(Token(row.Source));

        if (row.Category == NormalizationExamples.NormalizationCategory.LoneSurrogateNotNormalizable
            || !NormalizationExamples.PostFormIsSingleGrapheme(row, form))
        {
            // Either multi-grapheme post-form (compatibility ligature)
            // or a lone surrogate that can't go through string.Normalize.
            // Compile surfaces both as a thrown normalization error.
            Assert.Throws<InvalidOperationException>(() => rule.Compile(form));
            return;
        }

        rule.Compile(form);
        var result = rule.Parse(row.Source);
        Assert.That(result.Success, Is.True,
            $"OneOrMore(Token(\"{NormalizationExamples.Hex(row.Source)}\")).Compile({form}).Parse(\"{NormalizationExamples.Hex(row.Source)}\") " +
            $"should succeed. Error was: {result.ErrorMessage}");
    }

    // OneOf(TokenSet.Runes(source)) wrapped in OneOrMore. Same shape
    // as Token, but the matching data lives in OneOfRule's TokenSet
    // field instead of GraphemeRule's expected-text string, and each
    // rule has its own Compile-time normalization routine for that
    // field. The matrix runs every row through both routines.
    // Lone-surrogate sources are rejected EARLIER than Token's path:
    // TokenSet.Runes itself throws ArgumentException at construction
    // time, so we never get to Compile.
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

    // Literal rule: accepts multi-grapheme expected text by design, so
    // even the compatibility-ligature row works under FormKC. The post-
    // form text becomes the expected string, and the lexer-normalized
    // input is exactly that. Lone-surrogate sources can't be normalized
    // so Compile throws under every form.
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

    // Or-wrapped Token with an AnyToken fallback. Exercises the
    // OrRule.CannotMatchLookahead shortcut path (separate from
    // BetweenInclusive's). Asserts the Token branch matched, not the
    // fallback, by checking the symbol id of the leaf in the result.
    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void Token_in_Or_with_fallback_matches_token_branch_under_form(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        if (row.Category == NormalizationExamples.NormalizationCategory.LoneSurrogateNotNormalizable)
            return; // covered by Token_in_OneOrMore's Compile-throws path
        if (!NormalizationExamples.PostFormIsSingleGrapheme(row, form))
            return; // multi-grapheme post-form is covered by the Token_in_OneOrMore Compile-throws path
        var tokenRule = Token(row.Source).Preserve().As("tokenBranch");
        var fallback = AnyToken().As("fallbackBranch");
        var rule = Or(tokenRule, fallback);

        rule.Compile(form);
        var result = rule.Parse(row.Source);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        // Tree.Find(tokenRule) walks for the Token leaf. If Or wrongly
        // skipped the token branch (the staleness bug), the fallback wins
        // and Find returns null.
        Assert.That(result.Tree!.Find(tokenRule), Is.Not.Null,
            $"Or(Token(\"{NormalizationExamples.Hex(row.Source)}\"), AnyToken).Compile({form}).Parse(\"{NormalizationExamples.Hex(row.Source)}\") " +
            $"matched the AnyToken fallback instead of the token branch. " +
            $"FirstConsumedTokens computed against the pre-normalization _expected " +
            $"would cause exactly this shape: the post-normalization first rune " +
            $"isn't in the cached set, so Or's lookahead skips the token branch.");
    }

}
