using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;

namespace InductorParser.Tests;

// Optional(inner) is a thin factory over BetweenInclusiveRule with
// atLeast=0, atMost=1, traceName="Optional". The shared functional
// behavior (zero-match success, deepest-failure-wins quirk, sealed-rule
// rejection, trace format) is covered by BetweenInclusiveRuleTests. This
// fixture only verifies that the Optional factory wires those three
// values into the base correctly.
[TestFixture]
public class OptionalRuleTests
{
    [Test]
    public void Optional_factory_wires_atLeast_0_and_atMost_1_with_Optional_trace_name()
    {
        // Trace label "Optional" proves the named factory was used. The
        // SUCC at count= 1 inside the Optional segment with no probe-past
        // proves atMost = 1: the loop stopped at one match even though
        // the surrounding input had a second matchable 'a' available.
        // The follow-up Grapheme('a') consuming the second 'a' proves
        // Optional released control after one match rather than running
        // off the end.
        var sink = NewSink();
        AllOf(Optional(Grapheme('a')), Grapheme('a'))
            .Parse("aa", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "         Lexer.Read: 'a', Consumed: 1",
            "         SUCC | Grapheme: found 'a'",
            "      SUCC | Optional: count= 1",
            "      Lexer.Read: 'a', Consumed: 2",
            "      SUCC | Grapheme: found 'a'",
            "   SUCC | AllOf: found 2"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Optional_succeeds_with_zero_matches_proving_atLeast_is_0()
    {
        // atLeast = 0: when the inner rule can't match, Optional still
        // succeeds with no consumption. This is what distinguishes
        // Optional from a Grapheme('-') used directly.
        var result = AllOf(Optional(Grapheme('-')), Grapheme('a')).Parse("a");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }
}
