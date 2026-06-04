// Why this project exists
//
// This assembly is deliberately NOT named in any InternalsVisibleTo grant
// in src/InductorParser/InductorParser.csproj, so it sees only the public +
// protected surface of InductorParser, exactly as a third-party consumer
// would. Most of the checking is the build itself: the built-in rule
// sources are link-compiled here (see the .csproj), so if any of them ever
// used an internal member, or the surface they need were narrowed, this
// assembly wouldn't compile.
//
// LookbehindRule is the one hand-written custom rule the project keeps. The
// built-in rules already cover the leaf, bulk-scan, and failure surfaces a
// custom rule would touch, so a hand-written clone of those adds no coverage
// the link-compile doesn't already give. A lookbehind is the exception: it's
// the only thing here that exercises public surface no built-in does. It's a
// Rule-valued lookbehind (LPeg's lpeg.B), built entirely on the public
// surface: Lexer.SetPosition moves the cursor to an earlier token boundary,
// ParseChild runs the inner rule there, and Lexer.PeekTokenLength walks the
// boundaries. Compiling in this no-IVT project proves an outside author can
// build it using nothing internal.

using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;

namespace InductorParser.ExternalContractTests;

[TestFixture]
public class LookbehindRuleTests
{
    [Test]
    public void Lookbehind_factory_rejects_null_inner()
    {
        Assert.Throws<System.ArgumentNullException>(() => new LookbehindRule(null!));
    }

    [Test]
    public void Lookbehind_succeeds_when_the_previous_token_matches()
    {
        // After Token('a') the cursor is at offset 1; the lookbehind confirms
        // 'a' is behind it, then Token('b') consumes 'b'. The lookbehind
        // contributes nothing to the tree.
        var rule = And(Token('a'), new LookbehindRule(Token('a')), Token('b'));
        var result = rule.Parse("ab");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Lookbehind_consumes_nothing()
    {
        // Two lookbehind checks back to back, then the real consumer. If
        // either moved the cursor, the trailing Token('b') wouldn't line up.
        var rule = And(
            Token('a'),
            new LookbehindRule(Token('a')),
            new LookbehindRule(Token('a')),
            Token('b'));
        var result = rule.Parse("ab");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Lookbehind_fails_at_position_zero_because_nothing_is_behind()
    {
        var rule = new LookbehindRule(Token('a')).WithError("nothing precedes the start");
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("nothing precedes the start"));
    }

    [Test]
    public void Lookbehind_fails_when_the_previous_token_does_not_match()
    {
        var rule = And(Token('a'), new LookbehindRule(Token('b')).WithError("expected 'b' behind"));
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected 'b' behind"));
    }

    [Test]
    public void Lookbehind_matches_a_fixed_length_multi_token_literal()
    {
        // Walks boundaries back from offset 6 until Literal("bar") matches
        // ending exactly at 6 (it starts at offset 3).
        var rule = And(Literal("foobar"), new LookbehindRule(Literal("bar")), Eof());
        var result = rule.Parse("foobar");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Negative_lookbehind_matches_an_unescaped_delimiter()
    {
        // Not(LookbehindRule(Token('\\'))) is "not preceded by a backslash".
        // Built fresh per parse so each call compiles its own grammar.
        Rule UnescapedQuote() => And(Not(new LookbehindRule(Token('\\'))), Token('"'));

        var ok = And(Token('a'), UnescapedQuote()).Parse("a\"");
        Assert.That(ok.Success, Is.True, ok.ErrorMessage);

        var escaped = And(Token('\\'), UnescapedQuote()).Parse("\\\"");
        Assert.That(escaped.Success, Is.False);
    }

    [Test]
    public void Lookbehind_expresses_a_left_word_boundary()
    {
        // Match "cat" only at a word start: not preceded by a letter. The
        // start of input is a boundary (lookbehind fails at 0, so Not
        // succeeds there).
        Rule CatAtWordStart() => And(
            Optional(Token('x')),
            Not(new LookbehindRule(OneOf(TokenSet.Ascii.Letters))),
            Literal("cat"));

        Assert.That(CatAtWordStart().Parse("cat").Success, Is.True);
        // "xcat": the optional 'x' is consumed, so "cat" is now preceded by a
        // letter and the boundary check fails.
        Assert.That(CatAtWordStart().Parse("xcat").Success, Is.False);
    }
}
