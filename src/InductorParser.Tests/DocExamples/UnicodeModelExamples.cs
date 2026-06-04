using System.Text;
using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;

namespace InductorParser.Tests.DocExamples;

// Verifies the runnable claims in docs/UnicodeModel.md. UnicodeModel.md
// had no backing example test (the "ozpb gap"), which let its
// "Step 1: Text Normalization" round-trip claim drift: the doc used to
// say that compiling with null makes tree.ToString() match the original
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
public class UnicodeModelExamples
{
    // U+FB01 LATIN SMALL LIGATURE FI, which NFKC expands to the two
    // chars "fi". "a" + ligature + "b", built from the hex code point so
    // no raw glyph sits in the source.
    private static readonly string LigatureFiInput = "a" + ((char)0xFB01) + "b";

    // The three Compile lines from the doc compile and parse (smoke test
    // for the "The form is a grammar-level decision" code block).
    [Test]
    public void Compile_form_overloads_from_the_doc_all_work()
    {
        var defaultForm = And(Literal("hi")).Compile();                          // FormC default
        var compatibility = And(Literal("hi")).Compile(NormalizationForm.FormKC); // explicit FormKC
        var unnormalized = And(Literal("hi")).Compile(null);                      // no normalization

        Assert.That(defaultForm.Parse("hi").Success, Is.True);
        Assert.That(compatibility.Parse("hi").Success, Is.True);
        Assert.That(unnormalized.Parse("hi").Success, Is.True);
    }

    // The corrected round-trip claim: SourceText is the verbatim
    // accessor. tree.ToString() is NOT, because Delete content (the
    // default for Token / Literal) never reaches the tree. The old doc
    // claim ("compile with null and tree.ToString() matches the original
    // character for character") would fail this exact grammar.
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
        // round-trip even under Compile(null). This is what the old doc
        // claim got wrong.
        Assert.That(result.Tree!.ToString(), Is.EqualTo(string.Empty));
    }

    // SourceText round-trips under a normalizing form too, not just null:
    // it always returns the caller's original characters. ToString under
    // a normalizing form returns the normalized characters.
    [Test]
    public void SourceText_round_trips_under_a_normalizing_form()
    {
        var id = Identifier(NormalizationForm.FormKC).As("id").Compile(NormalizationForm.FormKC);

        var result = id.Parse(LigatureFiInput); // "a" + U+FB01 + "b"
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        // SourceText is the original, ligature intact.
        Assert.That(result.Tree!.SourceText, Is.EqualTo(LigatureFiInput));
        // ToString reflects the normalized (compatibility-expanded) text.
        Assert.That(result.Tree!.ToString(), Is.EqualTo("afib"));
    }

    // What Compile(null) actually buys: the tree's leaf text (and so
    // ToString on a content-preserving grammar) carries the original
    // characters instead of the normalized ones. AnyToken is Preserve,
    // so OneOrMore(AnyToken()) preserves everything it matches.
    [Test]
    public void Compile_null_keeps_leaf_text_in_original_characters()
    {
        // A fresh rule per form: the normalization form is committed at
        // first compile, so one rule instance can't be compiled twice.
        var unnormalized = OneOrMore(AnyToken()).As("all").Compile(null).Parse(LigatureFiInput);
        var compatibility = OneOrMore(AnyToken()).As("all").Compile(NormalizationForm.FormKC).Parse(LigatureFiInput);

        Assert.That(unnormalized.Success, Is.True, unnormalized.ErrorMessage);
        Assert.That(compatibility.Success, Is.True, compatibility.ErrorMessage);

        // null: ToString carries the original ligature.
        Assert.That(unnormalized.Tree!.ToString(), Is.EqualTo(LigatureFiInput));
        // FormKC: ToString carries the normalized expansion.
        Assert.That(compatibility.Tree!.ToString(), Is.EqualTo("afib"));

        // SourceText is the original under both forms.
        Assert.That(unnormalized.Tree!.SourceText, Is.EqualTo(LigatureFiInput));
        Assert.That(compatibility.Tree!.SourceText, Is.EqualTo(LigatureFiInput));
    }
}
