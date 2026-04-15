using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;

namespace InductorParser.Tests;

[TestFixture]
public class ZeroOrMoreRuleTests
{
    // ZeroOrMore has no failure path at all, so this fixture only carries
    // success tests. The TestArchitecture doc calls this out explicitly.

    [Test]
    public void ZeroOrMore_with_zero_matches_succeeds_with_empty_consumption()
    {
        // Input doesn't start with 'a', so the inner rule fails on its very
        // first attempt. ZeroOrMore catches that and succeeds with zero
        // children, leaving the lexer position unchanged.
        var rule = And(ZeroOrMore(Char('a')), Char('b'));
        var result = rule.Parse("b");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("b"));
    }

    [Test]
    public void ZeroOrMore_matches_multiple_occurrences_greedily()
    {
        var rule = And(ZeroOrMore(Char('a')), Char('b'));
        var result = rule.Parse("aaab");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aaab"));
    }

    [Test]
    public void ZeroOrMore_stops_at_first_inner_mismatch_and_surrounding_rule_continues()
    {
        // Inner matches 'a' twice, then on the third try sees 'b' and the
        // inner rule fails. ZeroOrMore commits the two successful iterations
        // and hands 'b' off to the next rule in the And.
        var rule = And(ZeroOrMore(Char('a')), Char('b'), Char('c'));
        var result = rule.Parse("aabc");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aabc"));
    }

    [Test]
    public void ZeroOrMore_trace_with_matches_produces_expected_output()
    {
        // ZeroOrMore itself does NOT open a transaction, so its success
        // line sits at the same depth as its containing And (depth 1);
        // inner iterations open their own transaction at depth 2.
        var sink = NewSink();
        And(ZeroOrMore(RuneIn(RuneSet.Ascii.Letters)), Eof())
            .Parse("ab", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'a', Consumed: 1",
            "      SUCC | RuneIn: found 'a', wanted one of '[A-Z,a-z]'",
            "      Lexer.Read: 'b', Consumed: 2",
            "      SUCC | RuneIn: found 'b', wanted one of '[A-Z,a-z]'",
            "      Lexer.Read: '<EOF>', Consumed: 2",
            "      FAIL | RuneIn: found '<EOF>', wanted one of '[A-Z,a-z]'",
            "      Lexer.RecordFailure: new deepest failure at char 2",
            "   SUCC | ZeroOrMore: count= 2",
            "   SUCC | Eof",
            "   SUCC | And: found 2"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void ZeroOrMore_trace_with_zero_matches_produces_expected_output()
    {
        // ZeroOrMore has no failure path, so even "no matches" is a
        // success — with count= 0. Wrapped in And so there's a
        // transaction open and the indentation is non-trivial.
        var sink = NewSink();
        And(ZeroOrMore(Char('a')), Eof())
            .Parse("", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: '<EOF>', Consumed: 0",
            "      FAIL | Char: found '<EOF>', wanted 'a'",
            "   SUCC | ZeroOrMore: count= 0",
            "   SUCC | Eof",
            "   SUCC | And: found 2"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }
}
