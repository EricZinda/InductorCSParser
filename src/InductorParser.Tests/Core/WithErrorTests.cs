using System;
using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Cross-cutting tests for the three-tier error-resolution system and the
// success-clears-errors rule. Tests that exercise a specific rule's
// WithError behavior (Or, And, OneOrMore, Not, ScanWhile, ...) live in
// that rule's own test file. See docs/ErrorArchitecture.md for the spec.
[TestFixture]
public class WithErrorTests
{
    [Test]
    public void WithError_message_appears_when_that_rule_is_deepest_failure()
    {
        // Name must be letters only. WithError gives the user-friendly message.
        var settingName = OneOrMore(OneOf(TokenSet.Letters))
            .WithError("Expected a setting name");

        var document = And(settingName, Token('='), Token(';'));

        // "1 = ;" fails at offset 0 because a digit isn't a letter.
        var result = document.Parse("1 = ;");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("Expected a setting name"));
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
    }

    [Test]
    public void Generic_error_when_no_rule_set_WithError()
    {
        var document = And(OneOrMore(OneOf(TokenSet.Letters)), Token('='), Token(';'));

        var result = document.Parse("ab");

        Assert.That(result.Success, Is.False);
        // OneOrMore consumes "ab", advancing to offset 2. Token('=') tries
        // at offset 2 and finds EOF. It records at offset 2. Since no rule
        // set WithError, the message falls back to the positional version.
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
        Assert.That(result.ErrorMessage, Does.StartWith("Unexpected end of input"));
    }

    [Test]
    public void Deepest_failure_wins_across_multiple_WithError_rules()
    {
        // Two rules with different WithError messages. The one whose failure
        // is deepest in the input should be the one the user sees.
        var name = OneOrMore(OneOf(TokenSet.Letters)).WithError("need letters");
        var digits = OneOrMore(OneOf(TokenSet.Digits)).WithError("need digits");
        var doc = And(name, Token('='), digits);

        // "ab=x" reaches the digits rule before failing (x isn't a digit).
        // "need digits" should win over "need letters" because the digit
        // failure is at a deeper position.
        var result = doc.Parse("ab=x");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("need digits"));
        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
    }

    [Test]
    public void Deeper_named_inner_wins_over_shallower_named_outer()
    {
        // Inner Token has its own .WithError ("unterminated string") at
        // a deeper position (the EOF after consuming "\"hello"). The
        // outer Or also has .WithError at its shallower start. Named-vs-
        // named is deepest-wins, so the more specific inner message wins.
        var quoted = And(
            Token('"'),
            ZeroOrMore(NoneOf(TokenSet.Runes("\""))),
            Token('"').WithError("unterminated string"));
        var rule = Or(quoted, Token('x'))
            .WithError("expected one of: string, x");

        var result = rule.Parse("\"hello");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("unterminated string"));
        Assert.That(result.ErrorCharIndex, Is.EqualTo("\"hello".Length));
    }

    [Test]
    public void Forced_outer_overrides_deeper_named_inner()
    {
        // Same grammar shape as the previous test, but the outer Or is
        // marked forced: true. Tier 3 beats tier 2 regardless of depth,
        // so the outer's summary message wins despite the inner being
        // deeper. The escape hatch for authors who want a top-level
        // message to suppress inner specifics.
        var quoted = And(
            Token('"'),
            ZeroOrMore(NoneOf(TokenSet.Runes("\""))),
            Token('"').WithError("unterminated string"));
        var rule = Or(quoted, Token('x'))
            .WithError("expected one of: string, x", forced: true);

        var result = rule.Parse("\"hello");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("expected one of: string, x"));
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
    }

    [Test]
    public void Forced_inner_beats_shallower_forced_outer()
    {
        // Two forced WithErrors. Tier 3 vs tier 3 falls back to
        // deepest-wins, so the deeper inner forced message wins.
        var inner = And(
            Token('y'),
            OneOrMore(OneOf(TokenSet.Digits))
                .WithError("forced inner", forced: true));
        var rule = And(Token('x'), inner).WithError("forced outer", forced: true);

        var result = rule.Parse("xyz");

        Assert.That(result.Success, Is.False);
        // Inner OneOrMore records "forced inner" at the digit-attempt
        // position (2, where 'z' is). Outer And's WithError records at
        // the failing child's start (1, where the inner And began).
        // Inner is deeper, inner wins.
        Assert.That(result.ErrorMessage, Is.EqualTo("forced inner"));
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
    }

    [Test]
    public void WithError_is_set_once()
    {
        // The fluent API encourages chaining, and a second .WithError on the
        // same shared rule looks like it's tagging a different position. But
        // .WithError mutates in place, so the second call silently overwrites
        // the first message on every shared use site. The set-once gate
        // catches that at the call rather than letting the wrong message
        // surface in parse failures.
        var rule = OneOrMore(OneOf(TokenSet.Letters)).WithError("need letters");

        var exception = Assert.Throws<InvalidOperationException>(
            () => rule.WithError("need an identifier"));
        Assert.That(exception!.Message, Does.Contain("need an identifier"));
        Assert.That(exception.Message, Does.Contain("need letters"));
        Assert.That(exception.Message, Does.Contain("set-once"));
    }
}
