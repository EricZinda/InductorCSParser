using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Tests for Rules.EndOfLine(), Rules.OptionalEndOfLine(),
// Rules.EndOfLineOrEof(), and the backing
// RuneSet.SingleRuneLineTerminators set.
//
// Two things under test:
//
//   1. UAX #18 Annex C coverage: every single-rune terminator (LF, VT,
//      FF, CR, NEL, LS, PS) is accepted, and the two-rune CRLF is
//      consumed as a single terminator rather than split.
//   2. The variant factories compose as advertised: OptionalEndOfLine
//      always succeeds, EndOfLineOrEof accepts EOF, and plain EndOfLine
//      rejects EOF.
//
// Test inputs are built from char casts (e.g. ((char)0x000A).ToString())
// rather than inline escapes so this source file doesn't have to
// contain any literal control characters.
[TestFixture]
public class EndOfLineTests
{
    // char.ConvertFromUtf32 handles both BMP and supplementary-plane
    // code points (returns a surrogate pair for the latter). Plain
    // (char)cp would truncate anything above U+FFFF.
    private static string Ch(int codepoint) => char.ConvertFromUtf32(codepoint);
    private static string LF => Ch(0x000A);
    private static string VT => Ch(0x000B);
    private static string FF => Ch(0x000C);
    private static string CR => Ch(0x000D);
    private static string NEL => Ch(0x0085);
    private static string LS => Ch(0x2028);
    private static string PS => Ch(0x2029);
    private static string CRLF => CR + LF;

    [Test]
    public void Matches_lf()
    {
        var result = EndOfLine().Parse(LF);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Matches_cr_alone()
    {
        var result = EndOfLine().Parse(CR);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Matches_crlf_as_one_terminator()
    {
        // CRLF-first ordering in the FirstOf means both runes are consumed.
        // Follow EndOfLine with Eof() so any leftover lone LF after a
        // half-consumed CR would surface as a failure.
        var rule = AllOf(EndOfLine(), Eof());
        var result = rule.Parse(CRLF);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Matches_vt()
    {
        var result = EndOfLine().Parse(VT);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Matches_ff()
    {
        var result = EndOfLine().Parse(FF);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Matches_nel()
    {
        var result = EndOfLine().Parse(NEL);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Matches_line_separator()
    {
        var result = EndOfLine().Parse(LS);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Matches_paragraph_separator()
    {
        var result = EndOfLine().Parse(PS);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Rejects_ordinary_character()
    {
        var result = EndOfLine().Parse("x");
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
    }

    [Test]
    public void Rejects_ordinary_whitespace()
    {
        // Space (U+0020) and tab (U+0009) are Whitespace but NOT line
        // terminators under UAX #18 Annex C.
        var spaceResult = EndOfLine().Parse(Ch(0x0020));
        Assert.That(spaceResult.Success, Is.False);

        var tabResult = EndOfLine().Parse(Ch(0x0009));
        Assert.That(tabResult.Success, Is.False);
    }

    [Test]
    public void Rejects_empty_input()
    {
        var result = EndOfLine().Parse("");
        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void OptionalEndOfLine_succeeds_on_empty_input()
    {
        var result = OptionalEndOfLine().Parse("");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void OptionalEndOfLine_succeeds_on_terminator()
    {
        var result = AllOf(OptionalEndOfLine(), Eof()).Parse(LF);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void OptionalEndOfLine_consumes_crlf_as_pair()
    {
        // If the inner ordering leaked CR alone, Eof would find the LF
        // still sitting there and fail.
        var result = AllOf(OptionalEndOfLine(), Eof()).Parse(CRLF);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void EndOfLineOrEof_matches_eof()
    {
        var result = EndOfLineOrEof().Parse("");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void EndOfLineOrEof_matches_terminator_then_reaches_eof()
    {
        var result = AllOf(EndOfLineOrEof(), Eof()).Parse(LF);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void EndOfLineOrEof_rejects_content_that_isnt_terminator_or_eof()
    {
        // A character that isn't a terminator and isn't end-of-input
        // has to fail both alternatives.
        var result = EndOfLineOrEof().Parse("x");
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
    }

    [Test]
    public void RuneSet_SingleRuneLineTerminators_contains_expected_runes()
    {
        // Spot-check the set membership directly, independent of the
        // EndOfLine factory. A grammar that wanted "stop at any line
        // terminator rune" would read this set through NoneOf, so
        // its contents matter on their own.
        var set = RuneSet.SingleRuneLineTerminators;
        Assert.That(set.Contains(0x000A), Is.True, "LF");
        Assert.That(set.Contains(0x000B), Is.True, "VT");
        Assert.That(set.Contains(0x000C), Is.True, "FF");
        Assert.That(set.Contains(0x000D), Is.True, "CR");
        Assert.That(set.Contains(0x0085), Is.True, "NEL");
        Assert.That(set.Contains(0x2028), Is.True, "LS");
        Assert.That(set.Contains(0x2029), Is.True, "PS");

        Assert.That(set.Contains(0x0020), Is.False, "space not a terminator");
        Assert.That(set.Contains(0x0009), Is.False, "tab not a terminator");
        Assert.That(set.Contains('x'), Is.False, "letter not a terminator");
    }

    [Test]
    public void Line_without_hede_recipe_accepts_zwj_family()
    {
        // Documents the recipe shape the readme uses: Not(Literal("hede"))
        // plus Not(EndOfLine()) plus AnyToken() in a ZeroOrMore, bounded
        // by EndOfLineOrEof. This shape handles multi-rune graphemes
        // like ZWJ emoji because AnyToken consumes whole graphemes.
        var lineWithoutHede = AllOf(
            ZeroOrMore(AllOf(
                Not(Literal("hede")),
                Not(EndOfLine()),
                AnyToken()
            )),
            EndOfLineOrEof());

        var input = "hello" + Ch(0x1F468) + Ch(0x200D) + Ch(0x1F469) + Ch(0x200D) + Ch(0x1F467) + "world";
        var result = lineWithoutHede.Parse(input);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Line_without_hede_recipe_rejects_embedded_hede()
    {
        var lineWithoutHede = AllOf(
            ZeroOrMore(AllOf(
                Not(Literal("hede")),
                Not(EndOfLine()),
                AnyToken()
            )),
            EndOfLineOrEof());

        var result = lineWithoutHede.Parse("hellohedeworld");
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(5));
    }
}
