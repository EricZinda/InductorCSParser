using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Tests for ParseOptions.AllowTrailingInput. By default Parse requires
// the grammar to consume every token of the input, so trailing tokens
// the rule didn't claim turn the parse into a failure. With
// AllowTrailingInput = true, Parse succeeds as soon as the root rule
// matches, even if the lexer hasn't reached EOF. The flag doesn't
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
        // instead of failing at offset 2 on the unconsumed "bb". Token
        // defaults to FlattenType.Delete, so .Preserve() the inner token
        // to keep its leaf in the tree; that's what makes the consumed
        // text observable below.
        var rule = OneOrMore(Token('a').Preserve());
        var result = rule.Parse("aabb", new ParseOptions { AllowTrailingInput = true });

        Assert.That(result.Success, Is.True);
        Assert.That(result.ToString(), Is.EqualTo("aa"),
            "OneOrMore should have consumed only the leading 'aa' run, leaving 'bb' as the unclaimed tail");
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
        var rule = OneOrMore(Token('a').Preserve());
        var result = rule.Parse("aaa", new ParseOptions { AllowTrailingInput = true });

        Assert.That(result.Success, Is.True);
        Assert.That(result.ToString(), Is.EqualTo("aaa"),
            "with no tail to leave behind, the rule should still consume the full input");
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
        Assert.That(result.Tree!.ToString(), Is.EqualTo(string.Empty),
            "zero matches on empty input should consume nothing");
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
    public void Trailing_input_failure_reports_position_of_first_unconsumed_char()
    {
        // Or tries Literal("hello") which matches four chars before
        // failing on 'z' at offset 4, then falls back to Token('h') and
        // succeeds at offset 0 (consuming one char). Parsing stops at
        // offset 1 with trailing "ellz" unconsumed. The doc contract
        // (InductorParserDesignDecisions.md "Trailing Input Is a Failure")
        // is that ErrorCharIndex is the first leftover character (offset
        // 1, the 'e'), not a position from a rolled-back alternative
        // (offset 4, the 'z' that the abandoned Literal hit and that
        // lexer.DeepestFailure still records).
        var rule = Or(Literal("hello"), Token('h'));
        var result = rule.Parse("hellz");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1),
            "trailing input begins at offset 1; the 'z' at offset 4 belongs to a rolled-back alternative");
        Assert.That(result.ErrorMessage,
            Is.EqualTo("Parse failed at offset 1: unexpected 'e'."));
    }

    [Test]
    public void Trailing_input_failure_does_not_surface_message_from_rolled_back_alternative()
    {
        // The rolled-back Literal here has a WithError attached. The
        // trailing-input failure shouldn't surface that message: the
        // rule that owned it isn't on the success path. The standard
        // PositionalErrorTemplate should describe the trailing tail.
        var rule = Or(
            Literal("hello").WithError("Expected the word 'hello'"),
            Token('h'));
        var result = rule.Parse("hellz");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage,
            Is.EqualTo("Parse failed at offset 1: unexpected 'e'."),
            "WithError on a rolled-back alternative shouldn't surface as the trailing-input message");
    }

    [Test]
    public void AllowTrailingInput_does_not_affect_inner_failure_position()
    {
        // The rule itself fails part-way through, and AllowTrailingInput
        // doesn't paper over that. Failure still reports the in-rule
        // position via deepest-failure-wins, just like the default path.
        var rule = And(Token('a'), Token('b'), Token('c'));
        var result = rule.Parse("abXdef", new ParseOptions { AllowTrailingInput = true });

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2),
            "Token('c') fails at offset 2 because 'X' isn't 'c'; trailing input flag is irrelevant here");
    }
}
