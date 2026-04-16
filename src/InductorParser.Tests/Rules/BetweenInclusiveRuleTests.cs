using System;
using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;

namespace InductorParser.Tests;

[TestFixture]
public class BetweenInclusiveRuleTests
{
    // Tests that assert on Tree.ToString() use PreserveFlattenWrappers so
    // Char rules (default FlattenType.Delete) stay in the tree and their
    // text is visible in the concatenated output. Without the flag the
    // tree would contain only non-Delete nodes, which is the correct
    // parse-time semantic — just not what these tests are looking at.
    private static ParseOptions Debug() => new() { PreserveFlattenWrappers = true };


    [Test]
    public void BetweenInclusive_exact_count_matches_exactly_N()
    {
        var rule = BetweenInclusive(Char('a'), 3, 3);
        var result = rule.Parse("aaa", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aaa"));
    }

    [Test]
    public void BetweenInclusive_exact_count_fails_when_too_few()
    {
        var rule = BetweenInclusive(Char('a'), 3, 3);
        var result = rule.Parse("aa");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
    }

    [Test]
    public void BetweenInclusive_exact_count_fails_when_too_many()
    {
        var rule = BetweenInclusive(Char('a'), 3, 3);
        var result = rule.Parse("aaaa");

        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void BetweenInclusive_at_lower_bound_succeeds()
    {
        var rule = BetweenInclusive(Char('a'), 2, 5);
        var result = And(rule, OneOrMore(Char('b'))).Parse("aabbb", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aabbb"));
    }

    [Test]
    public void BetweenInclusive_at_upper_bound_succeeds()
    {
        var rule = BetweenInclusive(Char('a'), 2, 5);
        var result = And(rule, Char('b')).Parse("aaaaab", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aaaaab"));
    }

    [Test]
    public void BetweenInclusive_stops_at_upper_bound_even_with_more_input()
    {
        var rule = And(BetweenInclusive(Char('a'), 1, 3), OneOrMore(Char('a')));
        var result = rule.Parse("aaaaa", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aaaaa"));
    }

    [Test]
    public void BetweenInclusive_one_below_lower_bound_fails()
    {
        var rule = BetweenInclusive(Char('a'), 3, 5);
        var result = rule.Parse("aa");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
    }

    [Test]
    public void BetweenInclusive_zero_zero_succeeds_with_no_matches()
    {
        var rule = And(BetweenInclusive(Char('a'), 0, 0), Char('b'));
        var result = rule.Parse("b", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("b"));
    }

    [Test]
    public void BetweenInclusive_zero_zero_does_not_consume_matching_input()
    {
        var rule = And(BetweenInclusive(Char('a'), 0, 0), OneOrMore(Char('a')));
        var result = rule.Parse("aaa", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aaa"));
    }

    [Test]
    public void BetweenInclusive_failure_without_WithError_falls_back_to_positional_message()
    {
        var rule = BetweenInclusive(Char('a'), 2, 4);
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
        Assert.That(result.ErrorMessage, Does.StartWith("Unexpected end of input"));
    }

    [Test]
    public void BetweenInclusive_WithError_message_surfaces_on_failure()
    {
        var rule = BetweenInclusive(Char('a'), 2, 4)
            .WithError("need 2 to 4 a's");
        var result = rule.Parse("ab");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
        Assert.That(result.ErrorMessage, Is.EqualTo("need 2 to 4 a's"));
    }

    [Test]
    public void BetweenInclusive_factory_rejects_negative_atLeast()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BetweenInclusive(Char('a'), -1, 5));
    }

    [Test]
    public void BetweenInclusive_factory_rejects_atMost_less_than_atLeast()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BetweenInclusive(Char('a'), 3, 2));
    }

    [Test]
    public void BetweenInclusive_factory_rejects_null_inner()
    {
        Assert.Throws<ArgumentNullException>(
            () => BetweenInclusive(null!, 0, 1));
    }

    [Test]
    public void BetweenInclusive_trace_success_produces_expected_output()
    {
        var sink = NewSink();
        BetweenInclusive(RuneIn(RuneSet.Ascii.Letters), 2, 4)
            .Parse("abc", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'a', Consumed: 1",
            "      SUCC | RuneIn: found 'a', wanted one of '[A-Z,a-z]'",
            "      Lexer.Read: 'b', Consumed: 2",
            "      SUCC | RuneIn: found 'b', wanted one of '[A-Z,a-z]'",
            "      Lexer.Read: 'c', Consumed: 3",
            "      SUCC | RuneIn: found 'c', wanted one of '[A-Z,a-z]'",
            "      Lexer.Read: '<EOF>', Consumed: 3",
            "      FAIL | RuneIn: found '<EOF>', wanted one of '[A-Z,a-z]'",
            "      Lexer.RecordFailure: new deepest failure at char 3",
            "   SUCC | BetweenInclusive[2..4]: count= 3"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void BetweenInclusive_trace_failure_produces_expected_output()
    {
        var sink = NewSink();
        BetweenInclusive(Char('a'), 2, 4)
            .Parse("a", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'a', Consumed: 1",
            "      SUCC | Char: found 'a'",
            "      Lexer.Read: '<EOF>', Consumed: 1",
            "      FAIL | Char: found '<EOF>', wanted 'a'",
            "      Lexer.RecordFailure: new deepest failure at char 1",
            "   FAIL | BetweenInclusive[2..4]: count= 1"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }
}
