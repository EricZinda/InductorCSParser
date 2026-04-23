using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;

namespace InductorParser.Tests;

[TestFixture]
public class AtMostRuleTests
{
    // Tree.ToString() assertions use PreserveFlattenWrappers so the
    // Token leaves (default FlattenType.Delete) stay in the tree and
    // their text contributes to the concatenated view.
    private static ParseOptions Debug() => new() { PreserveFlattenWrappers = true };

    [Test]
    public void AtMost_matches_zero_occurrences()
    {
        // AtMost always succeeds (lower bound is 0), so a grammar that
        // sees no matches still produces a successful parse. The
        // follow-up rule in the And has to supply whatever content
        // actually shows up at this position.
        var rule = And(AtMost(3, Token('a')), Token('b'));
        var result = rule.Parse("b", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("b"));
    }

    [Test]
    public void AtMost_matches_one_occurrence()
    {
        var rule = And(AtMost(3, Token('a')), Token('b'));
        var result = rule.Parse("ab", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("ab"));
    }

    [Test]
    public void AtMost_matches_up_to_N_occurrences()
    {
        var rule = And(AtMost(3, Token('a')), Token('b'));
        var result = rule.Parse("aaab", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aaab"));
    }

    [Test]
    public void AtMost_stops_at_N_and_surrounding_rule_consumes_remainder()
    {
        // AtMost commits after the Nth match even when more would
        // match. Here the And requires the follow-up Token('a') to
        // pick up the fourth 'a'. Without the upper-bound stop the
        // outer OneOrMore would swallow everything and the trailing
        // 'b' would have nowhere to go.
        var rule = And(AtMost(3, Token('a')), Token('a'), Token('b'));
        var result = rule.Parse("aaaab", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aaaab"));
    }

    [Test]
    public void AtMost_top_level_fails_when_more_than_N_and_input_not_fully_consumed()
    {
        // Top-level Parse requires the whole input be consumed. AtMost
        // caps at N matches, so the tail ("aa") has no rule to match
        // it and the overall parse fails.
        var rule = AtMost(3, Token('a'));
        var result = rule.Parse("aaaaa");

        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void AtMost_does_not_consume_non_matching_input()
    {
        // AtMost(3, a) on input "bbb" matches zero times and leaves
        // the lexer where it started. The OneOrMore(b) then runs on
        // the full "bbb".
        var rule = And(AtMost(3, Token('a')), OneOrMore(Token('b')));
        var result = rule.Parse("bbb", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("bbb"));
    }

    [Test]
    public void AtMost_zero_succeeds_with_no_matches()
    {
        // AtMost(0, ...) is technically legal: upper and lower bound
        // are both zero, so the rule always matches zero times and
        // never consumes input. Weird but consistent.
        var rule = And(AtMost(0, Token('a')), Token('b'));
        var result = rule.Parse("b", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("b"));
    }

    [Test]
    public void AtMost_zero_does_not_consume_matching_input()
    {
        var rule = And(AtMost(0, Token('a')), OneOrMore(Token('a')));
        var result = rule.Parse("aaa", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aaa"));
    }

    [Test]
    public void AtMost_WithError_does_not_surface_because_rule_always_succeeds()
    {
        // AtMost always succeeds, so a WithError message on it never
        // reaches the deepest-failure slot. Document the behavior by
        // verifying it. A failing parse here fails on the outer And,
        // not on AtMost.
        var rule = And(
            AtMost(3, Token('a')).WithError("unreachable"),
            Token('z'));
        var result = rule.Parse("aaab");

        Assert.That(result.Success, Is.False);
        // AtMost consumed three 'a's. The outer And failed on Token('z')
        // against 'b' at offset 3.
        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(result.ErrorMessage, Does.Not.Contain("unreachable"));
    }

    [Test]
    public void AtMost_factory_rejects_negative_count()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => AtMost(-1, Token('a')));
    }

    [Test]
    public void AtMost_factory_rejects_null_inner()
    {
        Assert.Throws<System.ArgumentNullException>(
            () => AtMost(3, null!));
    }

    [Test]
    public void AtMost_trace_success_produces_expected_output()
    {
        // Two matches, then the inner fails on 'c' (not a digit) and
        // the AtMost loop stops. Loop upper bound is 3, so the probe
        // that fails on 'c' happens before the third iteration gets a
        // chance to run. AtMost reports count=2 on the success line.
        var sink = NewSink();
        AtMost(3, RuneIn(RuneSet.Ascii.Digits)).Parse("12c",
            new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: '1', Consumed: 1",
            "      SUCC | RuneIn: found '1', wanted one of '[0-9]'",
            "      Lexer.Read: '2', Consumed: 2",
            "      SUCC | RuneIn: found '2', wanted one of '[0-9]'",
            "      Lexer.Read: 'c', Consumed: 3",
            "      FAIL | RuneIn: found 'c', wanted one of '[0-9]'",
            "      Lexer.RecordFailure: new deepest failure at char 2",
            "   SUCC | AtMost[3]: count= 2"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void AtMost_trace_zero_match_still_succeeds()
    {
        // First-rune lookahead skip proves Token('a') can't match on
        // 'z', so the AtMost loop exits at count= 0. Since the lower
        // bound is 0, AtMost still succeeds.
        var sink = NewSink();
        AtMost(3, Token('a')).Parse("z", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   SUCC | AtMost[3]: count= 0"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }
}
