using System;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;

namespace InductorParser.Tests;

[TestFixture]
public class BetweenInclusiveRuleTests
{
    // Tests that assert on Tree.ToString() use PreserveAllSymbols so
    // Grapheme rules (default FlattenType.Delete) stay in the tree and their
    // text is visible in the concatenated output. Without the flag the
    // tree would contain only non-Delete nodes, which is the correct
    // parse-time semantic, just not what these tests are looking at.
    private static ParseOptions Debug() => new() { PreserveAllSymbols = true };


    [Test]
    public void BetweenInclusive_exact_count_matches_exactly_N()
    {
        var rule = BetweenInclusive(3, 3, Grapheme('a'));
        var result = rule.Parse("aaa", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aaa"));
    }

    [Test]
    public void BetweenInclusive_exact_count_fails_when_too_few()
    {
        var rule = BetweenInclusive(3, 3, Grapheme('a'));
        var result = rule.Parse("aa");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
    }

    [Test]
    public void BetweenInclusive_exact_count_fails_when_too_many()
    {
        var rule = BetweenInclusive(3, 3, Grapheme('a'));
        var result = rule.Parse("aaaa");

        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void BetweenInclusive_at_lower_bound_succeeds()
    {
        var rule = BetweenInclusive(2, 5, Grapheme('a'));
        var result = AllOf(rule, OneOrMore(Grapheme('b'))).Parse("aabbb", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aabbb"));
    }

    [Test]
    public void BetweenInclusive_at_upper_bound_succeeds()
    {
        var rule = BetweenInclusive(2, 5, Grapheme('a'));
        var result = AllOf(rule, Grapheme('b')).Parse("aaaaab", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aaaaab"));
    }

    [Test]
    public void BetweenInclusive_stops_at_upper_bound_even_with_more_input()
    {
        var rule = AllOf(BetweenInclusive(1, 3, Grapheme('a')), OneOrMore(Grapheme('a')));
        var result = rule.Parse("aaaaa", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aaaaa"));
    }

    [Test]
    public void BetweenInclusive_one_below_lower_bound_fails()
    {
        var rule = BetweenInclusive(3, 5, Grapheme('a'));
        var result = rule.Parse("aa");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
    }

    [Test]
    public void BetweenInclusive_zero_zero_succeeds_with_no_matches()
    {
        var rule = AllOf(BetweenInclusive(0, 0, Grapheme('a')), Grapheme('b'));
        var result = rule.Parse("b", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("b"));
    }

    [Test]
    public void BetweenInclusive_zero_zero_does_not_consume_matching_input()
    {
        var rule = AllOf(BetweenInclusive(0, 0, Grapheme('a')), OneOrMore(Grapheme('a')));
        var result = rule.Parse("aaa", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aaa"));
    }

    [Test]
    public void BetweenInclusive_failure_without_WithError_falls_back_to_positional_message()
    {
        var rule = BetweenInclusive(2, 4, Grapheme('a'));
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
        Assert.That(result.ErrorMessage, Does.StartWith("Unexpected end of input"));
    }

    [Test]
    public void BetweenInclusive_WithError_message_surfaces_on_failure()
    {
        var rule = BetweenInclusive(2, 4, Grapheme('a'))
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
            () => BetweenInclusive(-1, 5, Grapheme('a')));
    }

    [Test]
    public void BetweenInclusive_factory_rejects_atMost_less_than_atLeast()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BetweenInclusive(3, 2, Grapheme('a')));
    }

    [Test]
    public void BetweenInclusive_factory_rejects_null_inner()
    {
        Assert.Throws<ArgumentNullException>(
            () => BetweenInclusive(0, 1, null!));
    }

    [Test]
    public void BetweenInclusive_trace_success_produces_expected_output()
    {
        var sink = NewSink();
        BetweenInclusive(2, 4, OneOf(RuneSet.Ascii.Letters))
            .Parse("abc", new ParseOptions { TraceSink = sink });

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
            "   SUCC | BetweenInclusive[2..4]: count= 3"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void BetweenInclusive_trace_failure_produces_expected_output()
    {
        var sink = NewSink();
        BetweenInclusive(2, 4, Grapheme('a'))
            .Parse("a", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'a', Consumed: 1",
            "      SUCC | Grapheme: found 'a'",
            "      Lexer.Read: '<EOF>', Consumed: 1",
            "      FAIL | Grapheme: found '<EOF>', wanted 'a'",
            "      Lexer.RecordFailure: new deepest failure at char 1",
            "   FAIL | BetweenInclusive[2..4]: count= 1"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Sealed_BetweenInclusive_rejects_Flatten()
    {
        var rule = BetweenInclusive(1, 5, Grapheme('a'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.Flatten(FlattenType.Preserve));
    }

    [Test]
    public void Sealed_BetweenInclusive_rejects_WithError()
    {
        var rule = BetweenInclusive(1, 5, Grapheme('a'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.WithError("late"));
    }

    [Test]
    public void Sealed_BetweenInclusive_rejects_As()
    {
        var rule = BetweenInclusive(1, 5, Grapheme('a'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.As("late"));
    }
}
