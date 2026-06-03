// LookbehindRule is the externally-authored custom rule this project adds
// alongside FailRule (failure-only leaf), OneRuneRule (consuming leaf), and
// ScanRunRule. It exercises a surface none of those do: a zero-width rule
// that runs its inner rule against the input BEHIND the cursor.
//
// It's a Rule-valued lookbehind (LPeg's lpeg.B), built entirely on the
// public surface: Lexer.SetPosition moves the cursor to an earlier token
// boundary, ParseChild runs the inner rule there, and
// Lexer.PeekTokenLength walks the boundaries. Compiling in this no-IVT
// project proves an outside author can build it using nothing internal.

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
