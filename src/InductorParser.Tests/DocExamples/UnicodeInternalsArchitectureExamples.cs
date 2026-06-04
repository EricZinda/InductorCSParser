using System.Text;
using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;

namespace InductorParser.Tests.DocExamples;

// Verifies the runnable claims in docs/UnicodeInternalsArchitecture.md.
// Like UnicodeModel.md before it, this doc had no backing example test,
// which let its "Normalization" section repeat the same drifted round-trip
// claim: that compiling with null makes tree.ToString() match the original
// input character for character. That's wrong. tree.ToString() rebuilds
// text only from the nodes left in the tree, so it drops whatever the
// Delete rules matched, and Token / Literal default to Delete. The
// verbatim accessor is Symbol.SourceText, and it round-trips under every
// form, not just null. What null actually buys is that the tree's leaf
// text (and so ToString on a content-preserving grammar) carries the
// original characters instead of the normalized ones.
//
// Non-ASCII test data is built from hex code points (no raw glyphs in
// source) so the UnicodeLiteralCanary scanner stays happy.
[TestFixture]
public class UnicodeInternalsArchitectureExamples
{
    // U+FB01 LATIN SMALL LIGATURE FI, which NFKC expands to the two
    // chars "fi". "a" + ligature + "b", built from the hex code point so
    // no raw glyph sits in the source.
    private static readonly string LigatureFiInput = "a" + ((char)0xFB01) + "b";

    // The three Compile lines from the "The form is a grammar-level
    // decision" code block compile and parse.
    [Test]
    public void Compile_form_overloads_from_the_doc_all_work()
    {
        var defaultForm = And(Literal("hi")).Compile();                           // FormC default
        var compatibility = And(Literal("hi")).Compile(NormalizationForm.FormKC); // explicit FormKC
        var unnormalized = And(Literal("hi")).Compile(null);                      // no normalization

        Assert.That(defaultForm.Parse("hi").Success, Is.True);
        Assert.That(compatibility.Parse("hi").Success, Is.True);
        Assert.That(unnormalized.Parse("hi").Success, Is.True);
    }

    // The corrected round-trip claim. The doc's "Normalization" section
    // said callers who want character-exact round-trippability (where
    // tree.ToString() matches the original input character for character)
    // compile with null. That's wrong: ToString() drops Delete content
    // (the default for Token / Literal), so even under Compile(null) it is
    // not a verbatim round-trip. SourceText is the verbatim accessor.
    [Test]
    public void SourceText_is_the_verbatim_accessor_ToString_drops_Delete_content()
    {
        // Token(',') and both Literals default to FlattenType.Delete.
        var greeting = And(Literal("hello"), Token(','), Literal("world"))
            .As("greeting")
            .Compile(null);

        var result = greeting.Parse("hello,world");
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        // SourceText gives the verbatim original input back.
        Assert.That(result.Tree!.SourceText, Is.EqualTo("hello,world"));

        // ToString() drops every Delete child, so it is NOT a verbatim
        // round-trip even under Compile(null). The old doc claim
        // ("compile with null and tree.ToString() matches character for
        // character") asserts the opposite and would fail here.
        Assert.That(result.Tree!.ToString(), Is.EqualTo(string.Empty));
    }

    // What Compile(null) actually buys: the tree's leaf text (and so
    // ToString on a content-preserving grammar) carries the original
    // characters instead of the normalized ones. AnyToken is Preserve, so
    // OneOrMore(AnyToken()) preserves everything it matches.
    [Test]
    public void Compile_null_keeps_leaf_text_in_original_characters()
    {
        // A fresh rule per form: the normalization form is committed at
        // first compile, so one rule instance can't be compiled twice.
        var unnormalized = OneOrMore(AnyToken()).As("all").Compile(null).Parse(LigatureFiInput);
        var compatibility = OneOrMore(AnyToken()).As("all").Compile(NormalizationForm.FormKC).Parse(LigatureFiInput);

        Assert.That(unnormalized.Success, Is.True, unnormalized.ErrorMessage);
        Assert.That(compatibility.Success, Is.True, compatibility.ErrorMessage);

        // null: ToString carries the original ligature; FormKC normalizes it.
        Assert.That(unnormalized.Tree!.ToString(), Is.EqualTo(LigatureFiInput));
        Assert.That(compatibility.Tree!.ToString(), Is.EqualTo("afib"));

        // SourceText is the original under both forms.
        Assert.That(unnormalized.Tree!.SourceText, Is.EqualTo(LigatureFiInput));
        Assert.That(compatibility.Tree!.SourceText, Is.EqualTo(LigatureFiInput));
    }

    // The doc's "Positions reported in ParseResult ... are always into the
    // caller's original input string" claim. Parse decomposed input under
    // the default FormC: the lexer recomposes internally, but the failure
    // position is reported in original-string coordinates.
    [Test]
    public void Error_positions_are_in_original_input_coordinates()
    {
        // "café!" with café decomposed: e + U+0301 combining acute, then a
        // bang the grammar rejects. Built from hex so no raw glyph appears.
        string decomposed = "cafe" + ((char)0x0301) + "!";
        var grammar = OneOrMore(Identifier()).As("id").Compile(); // FormC default

        var result = grammar.Parse(decomposed);
        Assert.That(result.Success, Is.False);
        // The '!' sits at index 5 in the ORIGINAL (decomposed) string:
        // c a f e U+0301 ! -> indices 0..5. A position into the recomposed
        // "café!" ("café" is 4 chars) would report 4 instead.
        Assert.That(result.ErrorCharIndex, Is.EqualTo(5));
    }
}
