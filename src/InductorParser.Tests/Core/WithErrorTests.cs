using System;
using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Cross-cutting tests for the depth-primary error-resolution model.
// Tests that exercise a specific rule's WithError behavior (Or, And,
// OneOrMore, Not, ScanWhile, ...) live in that rule's own test file.
// See docs/ErrorArchitecture.md for the spec.
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
        Assert.That(result.ErrorMessage, Is.EqualTo("Expected a setting name at line 1, column 1."));
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
        // Two rules with different WithError messages. For "deepest wins"
        // to mean anything, both rules have to actually fail and record a
        // named failure. An And won't do that: if the first rule fails the
        // And short-circuits and the second never runs, so only one named
        // failure ever exists. An Or runs both branches from the same
        // start, and a rejected Or branch keeps its failure (Case 4 in
        // docs/ErrorArchitecture.md), so both named failures survive to be
        // ranked against each other.
        var letters = OneOrMore(OneOf(TokenSet.Letters))
            .WithError("need letters");
        var digits = And(
            Token('#'),
            OneOrMore(OneOf(TokenSet.Digits)).WithError("need digits"));
        var rule = Or(letters, digits);

        // On "#x": the letters branch fails immediately at offset 0 ('#'
        // isn't a letter), recording "need letters" there. The digits
        // branch consumes the '#', then OneOrMore(Digits) fails at offset
        // 1 ('x' isn't a digit), recording "need digits" there. Both
        // named failures survive the failed Or. Offset 1 is deeper than
        // offset 0, so "need digits" wins. If ranking ignored depth and
        // took the first failure written, "need letters" would surface.
        var result = rule.Parse("#x");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("need digits at line 1, column 2."));
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
    }

    [Test]
    public void Deeper_named_inner_wins_over_shallower_named_outer()
    {
        // Inner Token has its own .WithError ("unterminated string"),
        // recorded at the EOF after consuming "\"hello". The outer Or
        // has .WithError too. Composite anchoring records it at the
        // same deepest position its branches reached. Two named failures
        // at the same depth: the first one recorded wins, and the inner
        // Token recorded before the outer Or, so its message surfaces.
        var quoted = And(
            Token('"'),
            ZeroOrMore(NoneOf(TokenSet.Runes("\""))),
            Token('"').WithError("unterminated string"));
        var rule = Or(quoted, Token('x'))
            .WithError("expected one of: string, x");

        var result = rule.Parse("\"hello");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("unterminated string at line 1, column 7."));
        Assert.That(result.ErrorCharIndex, Is.EqualTo("\"hello".Length));
    }

    [Test]
    public void Forced_outer_overrides_deeper_named_inner()
    {
        // Same grammar shape as the previous test, but the outer Or is
        // marked forced: true. A forced failure is a hard override and
        // wins over every non-forced failure at any depth, so the outer's
        // summary message wins over the inner Token's named failure. The
        // escape hatch for authors who want a top-level message to
        // suppress inner specifics. forced overrides the message choice,
        // not the position: the failure still anchors where composite
        // anchoring puts it, the deepest position the Or's branches
        // reached (the EOF after "\"hello").
        var quoted = And(
            Token('"'),
            ZeroOrMore(NoneOf(TokenSet.Runes("\""))),
            Token('"').WithError("unterminated string"));
        var rule = Or(quoted, Token('x'))
            .WithError("expected one of: string, x", forced: true);

        var result = rule.Parse("\"hello");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("expected one of: string, x at line 1, column 7."));
        Assert.That(result.ErrorCharIndex, Is.EqualTo("\"hello".Length));
    }

    [Test]
    public void Forced_inner_beats_shallower_forced_outer()
    {
        // Two forced WithErrors. Forced beats forced on depth, so the
        // deeper inner forced message wins.
        var inner = And(
            Token('y'),
            OneOrMore(OneOf(TokenSet.Digits))
                .WithError("forced inner", forced: true));
        var rule = And(Token('x'), inner).WithError("forced outer", forced: true);

        var result = rule.Parse("xyz");

        Assert.That(result.Success, Is.False);
        // Inner OneOrMore records "forced inner" at the digit-attempt
        // position (2, where 'z' is). The outer And's forced WithError
        // anchors at the deepest position its subtree reached, also 2.
        // Two forced failures at the same depth: the first one recorded
        // wins, and the inner recorded before the outer.
        Assert.That(result.ErrorMessage, Is.EqualTo("forced inner at line 1, column 3."));
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
    }

    [Test]
    public void WithError_rejects_null_message_at_construction()
    {
        // Sequential asserts, not Assert.Multiple: this file syncs into
        // the IL2CPP pass, and Unity's included NUnit predates
        // Assert.Multiple.
        var namedException = Assert.Throws<ArgumentNullException>(
            () => Token('a').WithError(null!));
        Assert.That(namedException!.ParamName, Is.EqualTo("errorMessage"));

        var forcedException = Assert.Throws<ArgumentNullException>(
            () => Token('a').WithError(null!, forced: true));
        Assert.That(forcedException!.ParamName, Is.EqualTo("errorMessage"));
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

    [Test]
    public void WithError_set_once_advice_recommends_a_pattern_that_works()
    {
        // The set-once exception suggests how to attach a different error
        // to the same rule shape. The suggestion has to be a pattern that
        // actually surfaces the new message. A plain
        // Alias(rule).WithError("second") can't: the inner's existing
        // message wins the exact-depth tie as first writer, so the
        // alias's message never appears
        // (Alias_plain_WithError_does_not_override_an_inner_WithError in
        // AliasRuleTests proves it). The override that works is marking
        // the alias's .WithError forced
        // (Alias_forced_WithError_overrides_an_inner_WithError), so the
        // advice must include forced: true.
        var rule = OneOrMore(OneOf(TokenSet.Letters)).WithError("need letters");

        var exception = Assert.Throws<InvalidOperationException>(
            () => rule.WithError("need an identifier"));
        Assert.That(exception!.Message, Does.Contain("forced: true"));
    }

    [Test]
    public void WithError_rejects_null_message()
    {
        // .WithError(null!, forced: true) rejects the null at the API
        // boundary, before any field write. The follow-up assertion
        // verifies that no sibling state mutated: a subsequent legitimate
        // .WithError("real", forced: true) call passes the set-once check
        // and records both the message and the forced flag, because the
        // null-first call left _errorMessage and _errorForced untouched.
        var rule = OneOrMore(OneOf(TokenSet.Letters));
        var exception = Assert.Throws<ArgumentNullException>(
            () => rule.WithError(null!, forced: true));
        Assert.That(exception!.ParamName, Is.EqualTo("errorMessage"));
        Assert.DoesNotThrow(() => rule.WithError("need letters", forced: true));
    }
}
