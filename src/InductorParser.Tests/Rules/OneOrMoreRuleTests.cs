using NUnit.Framework;
using static InductorParser.Rules;

namespace InductorParser.Tests;

[TestFixture]
public class OneOrMoreRuleTests
{
    [Test]
    public void OneOrMore_matches_a_single_occurrence()
    {
        var rule = OneOrMore(Char('a'));
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("a"));
    }

    [Test]
    public void OneOrMore_matches_multiple_occurrences_greedily()
    {
        var rule = OneOrMore(Char('a'));
        var result = rule.Parse("aaaa");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aaaa"));
    }

    [Test]
    public void OneOrMore_stops_at_first_inner_mismatch_and_surrounding_rule_continues()
    {
        // OneOrMore is greedy but stops as soon as its inner fails. Here it
        // matches "aa", then the inner Char('a') sees 'b' on the third try
        // and fails. OneOrMore commits the two successful iterations and
        // hands control to the next rule in the And, which consumes "bb".
        // (The top-level Parse requires consuming all input, so a follow-up
        // rule is needed to pick up the remainder.)
        var rule = And(OneOrMore(Char('a')), OneOrMore(Char('b')));
        var result = rule.Parse("aabb");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aabb"));
    }

    [Test]
    public void OneOrMore_failure_without_WithError_falls_back_to_positional_message()
    {
        var rule = OneOrMore(Char('a'));
        var result = rule.Parse("bbb");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Does.StartWith("Parse failed at offset 0"));
    }

    [Test]
    public void OneOrMore_no_matches_reports_inner_rules_message()
    {
        // OneOrMore requires at least one match. The inner Char('a') tries
        // at offset 0, reads 'b', fails and records its WithError message.
        // OneOrMore then records at the same offset with its own WithError,
        // but the slot is already filled by the inner's more-specific
        // message, so the inner wins (first-writer at equal depth).
        var rule = OneOrMore(Char('a').WithError("want 'a'"))
                       .WithError("want at least one 'a'");

        var result = rule.Parse("bbb");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("want 'a'"));
    }

    [Test]
    public void OneOrMore_outer_message_wins_when_inner_has_none()
    {
        // Without a WithError on the inner, Char('a') records at offset 0
        // with a null message. OneOrMore then records at offset 0 with its
        // own WithError, which claims the empty slot via the equal-depth
        // rule.
        var rule = OneOrMore(Char('a')).WithError("want at least one 'a'");

        var result = rule.Parse("bbb");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("want at least one 'a'"));
    }
}
