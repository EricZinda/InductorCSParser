using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;

namespace InductorParser.Tests;

// Exactly(N, inner) is a thin factory over BetweenInclusiveRule with
// atLeast=N, atMost=N, traceName="Exactly[N]". The shared functional
// behavior (greedy match capped at the upper bound, failure when below
// the lower bound, WithError surfacing, sealed-rule rejection, trace
// format) is covered by BetweenInclusiveRuleTests. This fixture only
// verifies that the Exactly factory wires those three values into the
// base correctly.
[TestFixture]
public class ExactlyRuleTests
{
    [Test]
    public void Exactly_factory_wires_both_bounds_to_N_with_Exactly_trace_name()
    {
        // Trace label "Exactly[3]" proves the named factory was used and
        // the count was carried into the trace label. SUCC at count= 3
        // with no fourth probe proves atMost = 3 (the loop stopped
        // because count == atMost). The sibling test below verifies
        // atLeast = 3 by failing when the count is below 3.
        var sink = NewSink();
        Exactly(3, Token('a')).Parse("aaa", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'a', Consumed: 1",
            "      SUCC | Token: found 'a'",
            "      Lexer.Read: 'a', Consumed: 2",
            "      SUCC | Token: found 'a'",
            "      Lexer.Read: 'a', Consumed: 3",
            "      SUCC | Token: found 'a'",
            "   SUCC | Exactly[3]: count= 3"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Exactly_fails_when_count_is_below_required_count()
    {
        // atLeast = 3: matching only twice is not enough. This is what
        // distinguishes Exactly from AtMost.
        var result = Exactly(3, Token('a')).Parse("aa");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
    }
}
