using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Tests for Rules.EndOfLine() in both forms (strict and
// eofIsEol: true), plus the backing
// TokenSet.LineTerminators set.
//
// Two things under test:
//
//   1. UTS #18 §1.6 (RL1.6) coverage: every single-rune terminator (LF, VT,
//      FF, CR, NEL, LS, PS) is accepted, and the two-rune CRLF is
//      consumed as a single terminator rather than split.
//   2. The eofIsEol flag and Optional wrapping compose as advertised:
//      EndOfLine(eofIsEol: true) accepts EOF, plain EndOfLine() rejects
//      EOF, and Optional(EndOfLine(eofIsEol: true)) always succeeds.
//
// Test inputs are built from char casts (e.g. ((char)0x000A).ToString())
// rather than inline escapes so this source file doesn't have to
// contain any literal control characters.
[TestFixture]
public class EndOfLineRuleTests
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
        // CRLF-first ordering in the Or means both runes are consumed.
        // Follow EndOfLine with Eof() so any leftover lone LF after a
        // half-consumed CR would surface as a failure.
        var rule = And(EndOfLine(), Eof());
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
        // Space (U+0020) and tab (U+0009) are Whitespace but aren't line
        // terminators under UTS #18 §1.6 (RL1.6).
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
    public void Optional_with_eofIsEol_succeeds_on_empty_input()
    {
        var result = Optional(EndOfLine(eofIsEol: true)).Parse("");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Optional_with_eofIsEol_succeeds_on_terminator()
    {
        var result = And(Optional(EndOfLine(eofIsEol: true)), Eof()).Parse(LF);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Optional_with_eofIsEol_consumes_crlf_as_pair()
    {
        // If the inner ordering leaked CR alone, Eof would find the LF
        // still sitting there and fail.
        var result = And(Optional(EndOfLine(eofIsEol: true)), Eof()).Parse(CRLF);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void EndOfLine_with_eofIsEol_matches_eof()
    {
        var result = EndOfLine(eofIsEol: true).Parse("");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void EndOfLine_with_eofIsEol_matches_terminator_then_reaches_eof()
    {
        var result = And(EndOfLine(eofIsEol: true), Eof()).Parse(LF);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void EndOfLine_with_eofIsEol_rejects_content_that_isnt_terminator_or_eof()
    {
        // A character that isn't a terminator and isn't end-of-input
        // has to fail both alternatives.
        var result = EndOfLine(eofIsEol: true).Parse("x");
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
    }

    [Test]
    public void TokenSet_LineTerminators_contains_expected_runes()
    {
        // Spot-check the set membership directly, independent of the
        // EndOfLine factory. A grammar that wanted "stop at any line
        // terminator rune" would read this set through NoneOf, so
        // its contents matter on their own.
        var set = TokenSet.LineTerminators;
        Assert.That(set.ContainsRune(0x000A), Is.True, "LF");
        Assert.That(set.ContainsRune(0x000B), Is.True, "VT");
        Assert.That(set.ContainsRune(0x000C), Is.True, "FF");
        Assert.That(set.ContainsRune(0x000D), Is.True, "CR");
        Assert.That(set.ContainsRune(0x0085), Is.True, "NEL");
        Assert.That(set.ContainsRune(0x2028), Is.True, "LS");
        Assert.That(set.ContainsRune(0x2029), Is.True, "PS");

        Assert.That(set.ContainsRune(0x0020), Is.False, "space not a terminator");
        Assert.That(set.ContainsRune(0x0009), Is.False, "tab not a terminator");
        Assert.That(set.ContainsRune('x'), Is.False, "letter not a terminator");
    }

    [Test]
    public void TokenSet_LineTerminators_contains_CRLF_cluster()
    {
        // CRLF is one user-perceived character (UAX #29 GB3 keeps CR and
        // LF in the same grapheme cluster). LineTerminators includes it
        // as a multi-rune entry so OneOf, NoneOf, ScanUntil, and
        // ScanWhile all treat the cluster as one terminator. Without
        // this entry, ScanUntil(LineTerminators) (which is
        // grapheme-scoped) would treat the CRLF cluster as body
        // because the cluster as a whole isn't equal to any single-rune
        // entry.
        var set = TokenSet.LineTerminators;
        Assert.That(set.ContainsToken("\r\n"), Is.True, "CRLF cluster");
        Assert.That(set.ContainsToken("\r"), Is.True, "bare CR still matches");
        Assert.That(set.ContainsToken("\n"), Is.True, "bare LF still matches");
        Assert.That(set.ContainsToken("ab"), Is.False, "non-terminator multi-char isn't in set");
    }

    [Test]
    public void OneOf_LineTerminators_matches_CRLF_as_one_cluster()
    {
        // Direct check that the multi-rune entry actually flows through
        // to OneOf semantics: a CRLF input is consumed as one token,
        // not split.
        var rule = OneOf(TokenSet.LineTerminators);
        var result = rule.Parse("\r\n");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("\r\n"));
    }

    [Test]
    public void Literal_crlf_spans_a_two_token_crlf_where_OneOf_stops_at_the_cr()
    {
        // Why EndOfLine() is Or(Literal("\r\n"), OneOf(TokenSet.LineTerminators))
        // rather than the OneOf alone: on legacy runtimes (.NET Framework,
        // .NET Core 3.x, Unity's Mono) StringInfo predates the UAX #29 rule
        // that glues CR to LF, so the lexer hands CR and LF back as two
        // separate tokens. OneOf reads exactly one token, so there it would
        // match the CR alone and leave the LF to count as a second
        // terminator. Literal matches its text across token boundaries, so
        // it consumes the pair as one terminator on every runtime.
        //
        // A UAX #29 runtime never serves CRLF as two tokens at the top
        // level, so this test recreates that stream shape with WithinToken:
        // its sub-lexer hands the inner rule the outer CRLF token one rune
        // per Read, the same two-token stream the legacy lexer serves, and
        // requires the inner rule to consume every rune.

        // Literal("\r\n") consumes both one-rune tokens as one match.
        var literalResult = WithinToken(Literal(CRLF)).Parse(CRLF);
        Assert.That(literalResult.Success, Is.True, literalResult.ErrorMessage);

        // OneOf(LineTerminators) matches the CR token and stops, leaving
        // the LF unconsumed, so WithinToken rejects the partial match.
        var oneOfResult = WithinToken(OneOf(TokenSet.LineTerminators)).Parse(CRLF);
        Assert.That(oneOfResult.Success, Is.False,
            "OneOf reads exactly one token, so it matches the CR and leaves the LF behind");
    }

    [Test]
    public void ScanUntil_LineTerminators_stops_at_CRLF_cluster()
    {
        // The line-comment shape: ScanUntil(LineTerminators) walks body
        // characters and stops at the next line terminator, including
        // a CRLF cluster as one stop unit. Use AllowTrailingInput because
        // ScanUntil doesn't consume the stopper.
        var rule = ScanUntil(TokenSet.LineTerminators);
        var result = rule.Parse("// comment\r\nrest",
            new ParseOptions { AllowTrailingInput = true });
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("// comment"),
            "scan should stop at the start of the CRLF cluster, not include it.");
    }

    [Test]
    public void Line_without_hede_recipe_accepts_zwj_family()
    {
        // Documents the recipe shape the readme uses: Not(Literal("hede"))
        // plus Not(EndOfLine()) plus AnyToken() in a ZeroOrMore, bounded
        // by EndOfLine(eofIsEol: true). This shape handles multi-rune
        // graphemes like ZWJ emoji because AnyToken consumes whole
        // graphemes.
        var lineWithoutHede = And(
            ZeroOrMore(And(
                Not(Literal("hede")),
                Not(EndOfLine()),
                AnyToken()
            )),
            EndOfLine(eofIsEol: true));

        var input = "hello" + Ch(0x1F468) + Ch(0x200D) + Ch(0x1F469) + Ch(0x200D) + Ch(0x1F467) + "world";
        var result = lineWithoutHede.Parse(input);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Line_without_hede_recipe_rejects_embedded_hede()
    {
        var lineWithoutHede = And(
            ZeroOrMore(And(
                Not(Literal("hede")),
                Not(EndOfLine()),
                AnyToken()
            )),
            EndOfLine(eofIsEol: true));

        var result = lineWithoutHede.Parse("hellohedeworld");
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(5));
    }

    // -----------------------------------------------------------------
    // Naming and flatten policy on factory-built EndOfLine rules.

    // EndOfLine().As(name) names the rule, and the named rule's Symbol is
    // findable in the parse tree.
    [Test]
    public void EndOfLine_factory_supports_As_for_tree_find()
    {
        var lineBreak = EndOfLine().As("lineBreak");
        var rule = And(Token('a'), lineBreak, Token('b'));
        rule.Compile();
        var result = rule.Parse("a" + LF + "b");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Find(lineBreak), Is.Not.Null,
            "EndOfLine().As(...) should produce a findable wrapper.");
    }

    // .Flatten(...) on a factory-built EndOfLine rule overrides the
    // factory's default flatten policy.
    [Test]
    public void EndOfLine_factory_default_flatten_is_overridable()
    {
        var preserved = EndOfLine().Flatten(FlattenType.Preserve);
        Assert.That(preserved.FlattenType, Is.EqualTo(FlattenType.Preserve));
    }

    // A user-written factory that sets its flatten policy with
    // FlattenByDefault can still be named with .As(name) by its caller,
    // and the named rule's Symbol is findable.
    [Test]
    public void User_factory_using_FlattenByDefault_stays_nameable()
    {
        // A user-written "match a line break, drop it from the tree by
        // default" factory built on the public FlattenByDefault method.
        static Rule Newline() =>
            Or(Literal("\r\n"), OneOf(TokenSet.LineTerminators))
                .FlattenByDefault(FlattenType.Delete);

        var named = Newline().As("nl");
        var rule = And(Token('a'), named, Token('b'));
        rule.Compile();
        var result = rule.Parse("a" + LF + "b");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Find(named), Is.Not.Null,
            "a factory built on FlattenByDefault should still be nameable by its caller.");
    }
}
