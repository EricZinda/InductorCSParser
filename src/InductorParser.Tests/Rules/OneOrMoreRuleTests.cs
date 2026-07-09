using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;

namespace InductorParser.Tests;

// OneOrMore(inner) is a thin factory over BetweenInclusiveRule with
// atLeast=1, atMost=int.MaxValue, traceName="OneOrMore". The shared
// functional behavior (greedy match, deepest-failure-wins, WithError
// surfacing, sealed-rule rejection, trace format) is covered by
// BetweenInclusiveRuleTests. This fixture only verifies that the
// OneOrMore factory wires those three values into the base correctly.
[TestFixture]
public class OneOrMoreRuleTests
{
    [Test]
    public void OneOrMore_factory_wires_atLeast_1_and_atMost_int_max_with_OneOrMore_trace_name()
    {
        // Trace label "OneOrMore" (instead of "BetweenInclusive[1..]")
        // proves the named factory was used. Two SUCC iterations followed
        // by a probe that fails on EOF and a final SUCC at count= 2 prove
        // atMost = int.MaxValue, since the loop ran past the lower bound
        // and only stopped when the inner rule failed.
        var sink = NewSink();
        OneOrMore(Token('a')).Parse("aa", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'a', Consumed: 1",
            "      SUCC | Token: found 'a'",
            "      Lexer.Read: 'a', Consumed: 2",
            "      SUCC | Token: found 'a'",
            "      Lexer.Read: '<EOF>', Consumed: 2",
            "      FAIL | Token: found '<EOF>', wanted 'a'",
            "      Lexer.RecordFailure: new deepest failure at char 2",
            "   SUCC | OneOrMore: count= 2"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void OneOrMore_fails_with_zero_matches_proving_atLeast_is_1()
    {
        // The "OneOrMore"-specific bound is atLeast=1: zero matches must
        // fail. This is what distinguishes OneOrMore from ZeroOrMore.
        var result = OneOrMore(Token('a')).Parse("z");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
    }

    [Test]
    public void OneOrMore_of_a_nullable_inner_succeeds_via_a_zero_width_match()
    {
        // Optional always matches. Only one empty success is counted,
        // satisfying OneOrMore's AtLeast of 1.
        var result = OneOrMore(Optional(OneOf("a"))).Parse("");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.ToString(), Is.EqualTo(""));
    }

    [Test]
    public void OneOrMore_named_WithError_anchors_at_deepest_descendant_failure()
    {
        // Inner And(Letter, Letter) consumes the first letter then fails
        // on the second, recording a mechanical failure at position 1.
        // OneOrMore has a named WithError, and composite anchoring records
        // it at the deepest position its subtree reached (1), where it
        // ties the mechanical failure on depth and wins the named-beats-
        // mechanical tie-break. See docs/ErrorArchitecture.md.
        var letter = OneOf(TokenSet.Letters);
        var rule = OneOrMore(And(letter, letter)).WithError("expected letter pairs");

        var result = rule.Parse("a1");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("expected letter pairs at line 1, column 2."));
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
    }
}
