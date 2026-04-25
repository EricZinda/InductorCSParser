using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;

namespace InductorParser.Tests;

[TestFixture]
public class OneOrMoreRuleTests
{
    // Tree.ToString() assertions use PreserveAllSymbols so the
    // Token leaves (default FlattenType.Delete) stay in the tree and
    // their text contributes to the concatenated view.
    private static ParseOptions Debug() => new() { PreserveAllSymbols = true };

    [Test]
    public void OneOrMore_matches_a_single_occurrence()
    {
        var rule = OneOrMore(Token('a'));
        var result = rule.Parse("a", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("a"));
    }

    [Test]
    public void OneOrMore_matches_multiple_occurrences_greedily()
    {
        var rule = OneOrMore(Token('a'));
        var result = rule.Parse("aaaa", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aaaa"));
    }

    [Test]
    public void OneOrMore_stops_at_first_inner_mismatch_and_surrounding_rule_continues()
    {
        // OneOrMore is greedy but stops as soon as its inner fails. Here it
        // matches "aa", then the inner Token('a') sees 'b' on the third try
        // and fails. OneOrMore commits the two successful iterations and
        // hands control to the next rule in the And, which consumes "bb".
        // (The top-level Parse requires consuming all input, so a follow-up
        // rule is needed to pick up the remainder.)
        var rule = And(OneOrMore(Token('a')), OneOrMore(Token('b')));
        var result = rule.Parse("aabb", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aabb"));
    }

    [Test]
    public void OneOrMore_failure_without_WithError_falls_back_to_positional_message()
    {
        var rule = OneOrMore(Token('a'));
        var result = rule.Parse("bbb");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Does.StartWith("Parse failed at offset 0"));
    }

    [Test]
    public void OneOrMore_no_matches_reports_inner_rules_message()
    {
        // OneOrMore requires at least one match. The inner Token('a') tries
        // at offset 0, reads 'b', fails and records its WithError message.
        // OneOrMore then records at the same offset with its own WithError,
        // but the slot is already filled by the inner's more-specific
        // message, so the inner wins (first-writer at equal depth).
        var rule = OneOrMore(Token('a').WithError("want 'a'"))
                       .WithError("want at least one 'a'");

        var result = rule.Parse("bbb");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("want 'a'"));
    }

    [Test]
    public void OneOrMore_outer_message_wins_when_inner_has_none()
    {
        // Without a WithError on the inner, Token('a') records at offset 0
        // with a null message. OneOrMore then records at offset 0 with its
        // own WithError, which claims the empty slot via the equal-depth
        // rule.
        var rule = OneOrMore(Token('a')).WithError("want at least one 'a'");

        var result = rule.Parse("bbb");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("want at least one 'a'"));
    }

    [Test]
    public void OneOrMore_trace_success_produces_expected_output()
    {
        // Loop runs four inner attempts: three succeed on 'a','b','c',
        // the fourth hits EOF and fails. The failing iteration's
        // RecordFailure at position 3 is strictly deeper than the
        // initial 0, so the deepest-failure trace fires.
        var sink = NewSink();
        OneOrMore(OneOf(RuneSet.Ascii.Letters)).Parse("abc",
            new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'a', Consumed: 1",
            "      SUCC | OneOf: found 'a', wanted one of '[A-Z,a-z]'",
            "      Lexer.Read: 'b', Consumed: 2",
            "      SUCC | OneOf: found 'b', wanted one of '[A-Z,a-z]'",
            "      Lexer.Read: 'c', Consumed: 3",
            "      SUCC | OneOf: found 'c', wanted one of '[A-Z,a-z]'",
            "      Lexer.Read: '<EOF>', Consumed: 3",
            "      FAIL | OneOf: found '<EOF>', wanted one of '[A-Z,a-z]'",
            "      Lexer.RecordFailure: new deepest failure at char 3",
            "   SUCC | OneOrMore: count= 3"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void OneOrMore_trace_failure_produces_expected_output()
    {
        // OneOrMore's first-rune lookahead skip proves Token('a') can't match
        // on input "z" without reading (peek 'z' not in {'a'}), so OneOrMore
        // emits its own FAIL line with count= 0 and no inner Read/FAIL
        // trace appears. See BetweenInclusiveRule's skip branch.
        var sink = NewSink();
        OneOrMore(Token('a')).Parse("z", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   FAIL | OneOrMore: count= 0"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }
}
