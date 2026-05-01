using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;

namespace InductorParser.Tests;

// ZeroOrMore(inner) is a thin factory over BetweenInclusiveRule with
// atLeast=0, atMost=int.MaxValue, traceName="ZeroOrMore". The shared
// functional behavior (greedy match, scanner-skip optimization, sealed-
// rule rejection, trace format) is covered by BetweenInclusiveRuleTests.
// This fixture only verifies that the ZeroOrMore factory wires those
// three values into the base correctly.
[TestFixture]
public class ZeroOrMoreRuleTests
{
    [Test]
    public void ZeroOrMore_factory_wires_atLeast_0_and_atMost_int_max_with_ZeroOrMore_trace_name()
    {
        // Trace label "ZeroOrMore" proves the named factory was used.
        // SUCC at count= 0 on input that doesn't match proves atLeast = 0
        // (zero matches still succeed). The first-rune lookahead skip
        // proves the inner rule never matched, so the count= 0 outcome
        // is a real zero-match success, not a one-match success after
        // an inner SUCC line.
        var sink = NewSink();
        ZeroOrMore(Token('a')).Parse("z", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   SUCC | ZeroOrMore: count= 0"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void ZeroOrMore_loops_past_two_matches_proving_atMost_is_int_max()
    {
        // The probe past two matches (Lexer.Read at EOF, FAIL on Token,
        // RecordFailure at char 2) proves the loop's upper bound is
        // unbounded: it kept asking the inner rule for another match
        // after the second succeeded.
        var sink = NewSink();
        ZeroOrMore(Token('a')).Parse("aa", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'a', Consumed: 1",
            "      SUCC | Token: found 'a'",
            "      Lexer.Read: 'a', Consumed: 2",
            "      SUCC | Token: found 'a'",
            "      Lexer.Read: '<EOF>', Consumed: 2",
            "      FAIL | Token: found '<EOF>', wanted 'a'",
            "      Lexer.RecordFailure: new deepest failure at char 2",
            "   SUCC | ZeroOrMore: count= 2"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }
}
