using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Tests for Rules.InlineWhitespace() and Rules.AnyWhitespace().
//
// Pairs with EndOfLineRuleTests, which covers the line-terminator side of
// the same split. The split's contract:
//
//   - InlineWhitespace() matches intra-line whitespace runes only and
//     rejects every line terminator (\n, \r, NEL, LS, PS, VT, FF) and
//     CRLF.
//   - EndOfLine() matches every line terminator including CRLF as a unit.
//   - AnyWhitespace() is the union; it accepts both kinds.
//
// Test inputs use char.ConvertFromUtf32 rather than inline escapes so the
// source file doesn't carry any literal control characters.
[TestFixture]
public class WhitespaceRuleTests
{
    private static string Ch(int codepoint) => char.ConvertFromUtf32(codepoint);
    private static string LF => Ch(0x000A);
    private static string VT => Ch(0x000B);
    private static string FF => Ch(0x000C);
    private static string CR => Ch(0x000D);
    private static string NEL => Ch(0x0085);
    private static string LS => Ch(0x2028);
    private static string PS => Ch(0x2029);
    private static string CRLF => CR + LF;
    private static string NBSP => Ch(0x00A0);
    private static string EmSpace => Ch(0x2003);
    private static string IdeographicSpace => Ch(0x3000);

    [Test]
    public void InlineWhitespace_matches_space_and_tab()
    {
        Assert.That(InlineWhitespace().Parse(" ").Success, Is.True);
        Assert.That(InlineWhitespace().Parse("\t").Success, Is.True);
        Assert.That(InlineWhitespace().Parse("   \t  ").Success, Is.True);
    }

    [Test]
    public void InlineWhitespace_matches_unicode_intra_line_whitespace()
    {
        Assert.That(InlineWhitespace().Parse(NBSP).Success, Is.True);
        Assert.That(InlineWhitespace().Parse(EmSpace).Success, Is.True);
        Assert.That(InlineWhitespace().Parse(IdeographicSpace).Success, Is.True);
    }

    [Test]
    public void InlineWhitespace_rejects_lf()
    {
        Assert.That(InlineWhitespace().Parse(LF).Success, Is.False);
    }

    [Test]
    public void InlineWhitespace_rejects_cr()
    {
        Assert.That(InlineWhitespace().Parse(CR).Success, Is.False);
    }

    [Test]
    public void InlineWhitespace_rejects_crlf()
    {
        // The lexer reads CRLF as one grapheme. The single-rune
        // InlineWhitespace check sees a CRLF token and rejects it
        // because it isn't in TokenSet.InlineWhitespace.
        Assert.That(InlineWhitespace().Parse(CRLF).Success, Is.False);
    }

    [Test]
    public void InlineWhitespace_rejects_other_uax18_line_terminators()
    {
        Assert.That(InlineWhitespace().Parse(VT).Success, Is.False);
        Assert.That(InlineWhitespace().Parse(FF).Success, Is.False);
        Assert.That(InlineWhitespace().Parse(NEL).Success, Is.False);
        Assert.That(InlineWhitespace().Parse(LS).Success, Is.False);
        Assert.That(InlineWhitespace().Parse(PS).Success, Is.False);
    }

    [Test]
    public void InlineWhitespace_rejects_non_whitespace()
    {
        Assert.That(InlineWhitespace().Parse("a").Success, Is.False);
        Assert.That(InlineWhitespace().Parse("0").Success, Is.False);
    }

    [Test]
    public void AnyWhitespace_matches_intra_line_whitespace()
    {
        Assert.That(AnyWhitespace().Parse(" ").Success, Is.True);
        Assert.That(AnyWhitespace().Parse("\t").Success, Is.True);
        Assert.That(AnyWhitespace().Parse(NBSP).Success, Is.True);
    }

    [Test]
    public void AnyWhitespace_matches_lf_and_cr()
    {
        Assert.That(AnyWhitespace().Parse(LF).Success, Is.True);
        Assert.That(AnyWhitespace().Parse(CR).Success, Is.True);
    }

    [Test]
    public void AnyWhitespace_matches_crlf_as_one_unit()
    {
        // EndOfLine() comes first inside the FirstOf, so CRLF is
        // consumed as one terminator (the lexer treats CRLF as one
        // grapheme) rather than only matching the CR via the single-
        // rune side.
        Assert.That(AnyWhitespace().Parse(CRLF).Success, Is.True);
    }

    [Test]
    public void AnyWhitespace_matches_mixed_run()
    {
        // Spaces, a CRLF, more spaces, an LF, an em space.
        string input = "  " + CRLF + " \t" + LF + EmSpace;
        Assert.That(AnyWhitespace().Parse(input).Success, Is.True);
    }

    [Test]
    public void AnyWhitespace_rejects_non_whitespace()
    {
        Assert.That(AnyWhitespace().Parse("a").Success, Is.False);
        Assert.That(AnyWhitespace().Parse("0").Success, Is.False);
    }
}
