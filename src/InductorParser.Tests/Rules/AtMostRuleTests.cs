using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;

namespace InductorParser.Tests;

// AtMost(N, inner) is a thin factory over BetweenInclusiveRule with
// atLeast=0, atMost=N, traceName="AtMost[N]". The shared functional
// behavior (greedy match up to the upper bound, always-succeeds
// suppression of WithError, sealed-rule rejection, trace format) is
// covered by BetweenInclusiveRuleTests. This fixture only verifies that
// the AtMost factory wires those three values into the base correctly.
[TestFixture]
public class AtMostRuleTests
{
    [Test]
    public void AtMost_factory_wires_atLeast_0_and_atMost_N_with_AtMost_trace_name()
    {
        // Trace label "AtMost[3]" proves the named factory was used and
        // the upper bound was carried into the trace label. count= 3
        // with no fourth probe inside the AtMost segment proves
        // atMost = 3 (the loop stopped because count == atMost, not
        // because the inner rule failed). The follow-up Token('a')
        // consuming the fourth 'a' confirms AtMost released control
        // rather than greedily eating all four.
        var sink = NewSink();
        AllOf(AtMost(3, Token('a')), Token('a'))
            .Parse("aaaa", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "         Lexer.Read: 'a', Consumed: 1",
            "         SUCC | Token: found 'a'",
            "         Lexer.Read: 'a', Consumed: 2",
            "         SUCC | Token: found 'a'",
            "         Lexer.Read: 'a', Consumed: 3",
            "         SUCC | Token: found 'a'",
            "      SUCC | AtMost[3]: count= 3",
            "      Lexer.Read: 'a', Consumed: 4",
            "      SUCC | Token: found 'a'",
            "   SUCC | AllOf: found 2"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void AtMost_succeeds_with_zero_matches_proving_atLeast_is_0()
    {
        // atLeast = 0: when the inner rule can't match, AtMost still
        // succeeds with no consumption. The follow-up Token('b')
        // picks up the input from the same position AtMost started at.
        var result = AllOf(AtMost(3, Token('a')), Token('b')).Parse("b");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }
}
