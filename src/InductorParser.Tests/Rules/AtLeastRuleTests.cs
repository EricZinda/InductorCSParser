using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;

namespace InductorParser.Tests;

// AtLeast(N, inner) is a thin factory over BetweenInclusiveRule with
// atLeast=N, atMost=int.MaxValue, traceName="AtLeast[N]". The shared
// functional behavior (greedy match, failure when below the lower bound,
// sealed-rule rejection, trace format) is covered by
// BetweenInclusiveRuleTests. This fixture only verifies that the AtLeast
// factory wires those three values into the base correctly.
[TestFixture]
public class AtLeastRuleTests
{
    [Test]
    public void AtLeast_factory_wires_atLeast_N_and_atMost_int_max_with_AtLeast_trace_name()
    {
        // Trace label "AtLeast[2]" proves the named factory was used and
        // the lower bound shows up in the trace label. The probe
        // past two matches (Lexer.Read at EOF, FAIL on Token,
        // RecordFailure at char 2) followed by SUCC at count= 2 proves
        // atMost = int.MaxValue: the loop ran past the lower bound and
        // only stopped when the inner rule failed.
        var sink = NewSink();
        AtLeast(2, Token('a')).Parse("aa", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'a', Consumed: 1",
            "      SUCC | Token: found 'a'",
            "      Lexer.Read: 'a', Consumed: 2",
            "      SUCC | Token: found 'a'",
            "      Lexer.Read: '<EOF>', Consumed: 2",
            "      FAIL | Token: found '<EOF>', wanted 'a'",
            "      Lexer.RecordFailure: new deepest failure at char 2",
            "   SUCC | AtLeast[2]: count= 2"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void AtLeast_fails_when_count_is_below_lower_bound()
    {
        // atLeast = 2: only one match isn't enough. This is what
        // distinguishes AtLeast(2, ...) from OneOrMore.
        var result = AtLeast(2, Token('a')).Parse("a");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
    }
}
