using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Tests for ParseOptions.AllowTrailingInput. By default Parse requires
// the grammar to consume every token of the input, so trailing tokens
// the rule didn't claim turn the parse into a failure. With
// AllowTrailingInput = true, Parse succeeds as soon as the root rule
// matches, even if the lexer hasn't reached EOF. The flag does not
// affect failures inside the rule.
[TestFixture]
public class AllowTrailingInputTests
{
    [Test]
    public void Default_rejects_trailing_input()
    {
        var rule = OneOrMore(Token('a'));
        var result = rule.Parse("aabb");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
        Assert.That(result.ErrorMessage, Does.StartWith("Parse failed at offset 2"));
    }

    [Test]
    public void AllowTrailingInput_lets_parse_succeed_with_unconsumed_tail()
    {
        // Same input shape as the default-rejects test above. With the flag
        // on, the OneOrMore claims "aa" and the parse returns success
        // instead of failing at offset 2 on the unconsumed "bb".
        var rule = OneOrMore(Token('a'));
        var result = rule.Parse("aabb", new ParseOptions { AllowTrailingInput = true });

        Assert.That(result.Success, Is.True);
    }

    [Test]
    public void AllowTrailingInput_still_fails_when_rule_itself_does_not_match()
    {
        // The rule never matches a single 'a', so the parse must fail
        // even with trailing input allowed. AllowTrailingInput only relaxes
        // the post-rule EOF check, not the rule's own success condition.
        var rule = OneOrMore(Token('a'));
        var result = rule.Parse("bbbb", new ParseOptions { AllowTrailingInput = true });

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
    }

    [Test]
    public void AllowTrailingInput_with_full_match_still_succeeds()
    {
        // No tail to swallow: the grammar consumes everything. The flag
        // shouldn't change the outcome on inputs that the strict default
        // would already accept.
        var rule = OneOrMore(Token('a'));
        var result = rule.Parse("aaa", new ParseOptions { AllowTrailingInput = true });

        Assert.That(result.Success, Is.True);
    }

    [Test]
    public void AllowTrailingInput_with_empty_input_and_zero_or_more_succeeds()
    {
        // ZeroOrMore on empty input matches zero times. Default would still
        // succeed (lexer is at EOF), but verify explicitly that
        // AllowTrailingInput doesn't perturb the empty-input path.
        var rule = ZeroOrMore(Token('a')).Preserve();
        var result = rule.Parse("", new ParseOptions { AllowTrailingInput = true });

        Assert.That(result.Success, Is.True);
    }

    [Test]
    public void AllowTrailingInput_prefix_parse_returns_only_consumed_text()
    {
        // A realistic prefix-parsing case: extract the first integer at
        // the start of a longer string, leaving the tail for someone else.
        var rule = Integer().Preserve();
        var result = rule.Parse("123 hello world", new ParseOptions { AllowTrailingInput = true });

        Assert.That(result.Success, Is.True);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("123"));
    }

    [Test]
    public void AllowTrailingInput_does_not_affect_inner_failure_position()
    {
        // The rule itself fails part-way through, and AllowTrailingInput
        // doesn't paper over that. Failure still reports the in-rule
        // position via deepest-failure-wins, just like the default path.
        var rule = AllOf(Token('a'), Token('b'), Token('c'));
        var result = rule.Parse("abXdef", new ParseOptions { AllowTrailingInput = true });

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2),
            "Token('c') fails at offset 2 because 'X' isn't 'c'; trailing input flag is irrelevant here");
    }
}
