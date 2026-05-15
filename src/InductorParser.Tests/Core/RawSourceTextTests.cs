using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

using static InductorParser.Tests.CanaryHelper;
namespace InductorParser.Tests;

// Framework-level and TOML-integration tests for Symbol.SourceText.
//
// Rule-shape-specific cases (Optional zero/one match, And wraps Delete
// children, Or returns winning alternative, Peek/Not/Eof zero-width)
// live in each rule's own fixture under Rules/, per the
// TestArchitecture.md "Per-rule SourceText / SourceRange behavior
// tests live in each rule's own test file." convention.
//
// This fixture covers what doesn't fit any one rule's file:
//   - TOML-shaped integration patterns (date-time, float, special-
//     float, ml-string) that motivated the SourceText accessor by
//     showing the "Preserve every contributing punctuation char"
//     workaround the port shipped before this work could be removed.
//   - The FormC-normalization translation invariant: Symbol.SourceText
//     returns the user's original (pre-normalization) text, not the
//     parser's internal normalized form.
//   - The hand-built-composite-without-ParseContext fallback (empty
//     string, never throws).
[TestFixture]
public class RawSourceTextTests
{
    [Test]
    public void Toml_style_date_time_round_trips_through_SourceText()
    {
        // The canonical TOML use case from the backlog item: a grammar
        // with multiple Delete-default punctuation chars spread through
        // a long match. Five Delete-flattened punctuation chars (`-`,
        // `-`, `:`, `:`) sit between preserved digit groups. Symbol.ToString
        // walks only the preserved children and collapses to
        // "19790527T073200Z" — DateTimeOffset.Parse rejects that.
        // SourceText returns the verbatim "1979-05-27T07:32:00Z" because
        // the engine recorded the consumed span on the composite at
        // parse time, independent of which children survived flattening.
        //
        // The `.Preserve()`-on-every-punctuation workaround the TOML
        // port shipped before this work would have meant adding five
        // .Preserve() calls to the grammar (and remembering to do so on
        // every new punctuation rule that bleeds into a parent's
        // text-bearing match). SourceText is the systematic fix.
        var rule = And(
            Literal("1979").Preserve(),
            Token('-'),
            Literal("05").Preserve(),
            Token('-'),
            Literal("27").Preserve(),
            Token('T').Preserve(),
            Literal("07").Preserve(),
            Token(':'),
            Literal("32").Preserve(),
            Token(':'),
            Literal("00").Preserve(),
            Token('Z').Preserve()).As("dateTime");
        const string Input = "1979-05-27T07:32:00Z";
        var result = rule.Parse(Input);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("19790527T073200Z"),
            "Sanity check: ToString collapses to the compacted form; consumer can't feed it to DateTimeOffset.Parse.");
        Assert.That(result.Tree!.SourceText, Is.EqualTo(Input));
    }

    [Test]
    public void Float_with_Delete_flattened_dot_recovers_decimal_point()
    {
        // The TOML float case: Token('.') in the fraction rule is
        // Delete, so ToString of "3.14" gives "314". SourceText gives
        // "3.14".
        var rule = And(Literal("3").Preserve(), Token('.'), Literal("14").Preserve())
            .As("float");
        var result = rule.Parse("3.14");

        Assert.That(result.Tree!.ToString(), Is.EqualTo("314"));
        Assert.That(result.Tree!.SourceText, Is.EqualTo("3.14"));
    }

    [Test]
    public void Special_float_with_Delete_flattened_literal_recovers_keyword()
    {
        // The TOML special-float case: Literal("inf") is Delete by
        // factory, so ToString of a "-inf" composite gives just "-".
        // SourceText gives "-inf".
        var rule = And(Token('-').Preserve(), Literal("inf"))
            .As("specialFloat");
        var result = rule.Parse("-inf");

        Assert.That(result.Tree!.ToString(), Is.EqualTo("-"));
        Assert.That(result.Tree!.SourceText, Is.EqualTo("-inf"));
    }

    [Test]
    public void Returns_text_for_named_subrule_starting_after_leading_input()
    {
        var inner = Literal("XYZ").As("inner");
        var rule = And(Literal("ab"), inner);
        var result = rule.Parse("abXYZ");

        var innerSymbol = result.Tree!.Find(inner)!;
        Assert.That(innerSymbol.SourceText, Is.EqualTo("XYZ"));
    }

    [Test]
    public void Text_spans_across_a_newline_when_match_crosses_one()
    {
        // The "ml-basic-string body" case: EndOfLine() defaults to
        // Delete, but if the match span runs over a newline, the
        // verbatim source text still contains it. Mirrors the TOML
        // ml-basic-string case where ToString collapses "line1\nline2"
        // to "line1line2".
        var rule = And(
            Literal("line1").Preserve(),
            EndOfLine(),
            Literal("line2").Preserve()).As("body");
        var result = rule.Parse("line1\nline2");

        Assert.That(result.Tree!.ToString(), Is.EqualTo("line1line2"));
        Assert.That(result.Tree!.SourceText, Is.EqualTo("line1\nline2"));
    }

    [Test]
    public void Returns_original_text_under_FormC_normalization()
    {
        // The grammar normalizes the input from decomposed "café"
        // (5 chars: c, a, f, e, U+0301) to precomposed "café" (4 chars).
        // SourceText should return the user's original 5-char form,
        // not the 4-char normalized form. The verbatim "what the user
        // typed" guarantee is what makes the accessor safe for
        // round-tripping back to a downstream parser that doesn't
        // normalize. SourceRange's endpoints should also point at the
        // original (decomposed) string by reference so consumers
        // substringing Input get the user's typed bytes back.
        string DecomposedCafe = UnicodeExamples.CafePrecomposedGrapheme;
        var rule = Literal(UnicodeExamples.CafePrecomposedGrapheme).As("cafe");
        rule.Compile(System.Text.NormalizationForm.FormC);
        var result = rule.Parse(DecomposedCafe);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Tree!.SourceText, Is.EqualTo(DecomposedCafe));

        var range = result.Tree!.SourceRange!.Value;
        Assert.That(ReferenceEquals(range.Start.Input, DecomposedCafe), Is.True,
            "SourceRange.Start.Input should be the user's original (decomposed) string by reference, not the normalized one.");
        Assert.That(ReferenceEquals(range.End.Input, DecomposedCafe), Is.True);
        Assert.That(range.End.CharIndex - range.Start.CharIndex, Is.EqualTo(DecomposedCafe.Length),
            "The range should cover all 5 chars of the original string, not the 4 of the normalized form.");
    }

    [Test]
    public void SourceText_on_hand_built_composite_without_context_is_empty()
    {
        // A composite constructed without a ParseContext and without a
        // consumed span has no _leafChars to decode, so SourceText
        // returns the empty string. Pinning the documented "no
        // associated text" behavior.
        var composite = new Symbol(new SymbolId(1), FlattenType.Preserve, new Symbol[0]);
        Assert.That(composite.SourceText, Is.EqualTo(string.Empty));
    }
}
