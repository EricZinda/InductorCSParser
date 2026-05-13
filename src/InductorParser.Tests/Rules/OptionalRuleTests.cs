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
        // The follow-up Token('a') consuming the second 'a' proves
        // Optional released control after one match rather than running
        // off the end.
        var sink = NewSink();
        And(Optional(Token('a')), Token('a'))
            .Parse("aa", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "         Lexer.Read: 'a', Consumed: 1",
            "         SUCC | Token: found 'a'",
            "      SUCC | Optional: count= 1",
            "      Lexer.Read: 'a', Consumed: 2",
            "      SUCC | Token: found 'a'",
            "   SUCC | And: found 2"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Optional_succeeds_with_zero_matches_proving_atLeast_is_0()
    {
        // atLeast = 0: when the inner rule can't match, Optional still
        // succeeds with no consumption. This is what distinguishes
        // Optional from a Token('-') used directly.
        var result = And(Optional(Token('-')), Token('a')).Parse("a");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Optional_that_matched_zero_times_has_empty_SourceText()
    {
        // Optional matched zero times — the inner rule wasn't entered.
        // The preserved Optional Symbol records a zero-width consumed
        // span at its anchor offset, so SourceText is the empty string
        // and SourceRange is a zero-width range. Consumers that
        // highlight the position of an absent optional still get a
        // usable position from SourceRange.
        var optional = Optional(Literal("X").Preserve()).As("opt").Preserve();
        var rule = And(optional, Literal("Y").Preserve()).Preserve();
        var result = rule.Parse("Y");

        var optionalSymbol = result.Tree!.Find(optional)!;
        Assert.That(optionalSymbol.SourceText, Is.EqualTo(string.Empty));
        var range = optionalSymbol.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(0));
    }

    [Test]
    public void Optional_that_matched_once_returns_the_matched_SourceText()
    {
        // Optional matched once — the inner rule produced a leaf. The
        // Optional composite's consumed span covers what the inner
        // consumed ("X"), so SourceText returns "X" and SourceRange
        // spans [0, 1). Inner doesn't need .Preserve() — even with
        // Literal's Delete-by-factory default the wrapper still
        // recovers the matched text.
        var optional = Optional(Literal("X")).As("opt").Preserve();
        var rule = And(optional, Literal("Y").Preserve()).Preserve();
        var result = rule.Parse("XY");

        var optionalSymbol = result.Tree!.Find(optional)!;
        Assert.That(optionalSymbol.SourceText, Is.EqualTo("X"));
        var range = optionalSymbol.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(1));
    }
}
