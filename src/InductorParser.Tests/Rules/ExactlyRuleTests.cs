using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;

namespace InductorParser.Tests;

[TestFixture]
public class ExactlyRuleTests
{
    // Tree.ToString() assertions use PreserveFlattenWrappers so the
    // Token leaves (default FlattenType.Delete) stay in the tree and
    // their text contributes to the concatenated view.
    private static ParseOptions Debug() => new() { PreserveFlattenWrappers = true };

    [Test]
    public void Exactly_matches_when_input_has_exactly_N()
    {
        var rule = Exactly(Token('a'), 3);
        var result = rule.Parse("aaa", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aaa"));
    }

    [Test]
    public void Exactly_fails_when_input_has_fewer_than_N()
    {
        var rule = Exactly(Token('a'), 3);
        var result = rule.Parse("aa");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
    }

    [Test]
    public void Exactly_stops_at_N_and_surrounding_rule_consumes_remainder()
    {
        // Exactly commits after the Nth match even when more would match.
        // Here the And requires the follow-up Token('a') to pick up the
        // fourth 'a'. If Exactly greedily consumed it, the And would fail.
        var rule = And(Exactly(Token('a'), 3), Token('a'));
        var result = rule.Parse("aaaa", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aaaa"));
    }

    [Test]
    public void Exactly_top_level_fails_when_input_has_more_than_N()
    {
        // Top-level Parse requires consuming all input, so the trailing
        // 'a' past the exact count causes the parse to fail even though
        // the Exactly rule itself matched three times.
        var rule = Exactly(Token('a'), 3);
        var result = rule.Parse("aaaa");

        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void Exactly_zero_succeeds_with_no_matches()
    {
        var rule = And(Exactly(Token('a'), 0), Token('b'));
        var result = rule.Parse("b", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("b"));
    }

    [Test]
    public void Exactly_zero_does_not_consume_matching_input()
    {
        var rule = And(Exactly(Token('a'), 0), OneOrMore(Token('a')));
        var result = rule.Parse("aaa", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aaa"));
    }

    [Test]
    public void Exactly_WithError_message_surfaces_on_failure()
    {
        var rule = Exactly(Token('a'), 3)
            .WithError("need exactly 3 a's");
        var result = rule.Parse("ab");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
        Assert.That(result.ErrorMessage, Is.EqualTo("need exactly 3 a's"));
    }

    [Test]
    public void Exactly_factory_rejects_negative_count()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => Exactly(Token('a'), -1));
    }

    [Test]
    public void Exactly_factory_rejects_null_inner()
    {
        Assert.Throws<System.ArgumentNullException>(
            () => Exactly(null!, 3));
    }

    [Test]
    public void Exactly_trace_success_produces_expected_output()
    {
        // Three iterations succeed, the fourth inner attempt never runs
        // because the loop's upper bound is also 3. So no EOF Read/FAIL
        // trace appears, unlike OneOrMore/BetweenInclusive which probe
        // past their successful count.
        var sink = NewSink();
        Exactly(RuneIn(RuneSet.Ascii.Letters), 3).Parse("abc",
            new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'a', Consumed: 1",
            "      SUCC | RuneIn: found 'a', wanted one of '[A-Z,a-z]'",
            "      Lexer.Read: 'b', Consumed: 2",
            "      SUCC | RuneIn: found 'b', wanted one of '[A-Z,a-z]'",
            "      Lexer.Read: 'c', Consumed: 3",
            "      SUCC | RuneIn: found 'c', wanted one of '[A-Z,a-z]'",
            "   SUCC | Exactly[3]: count= 3"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Exactly_trace_failure_produces_expected_output()
    {
        // Lookahead skip proves Token('a') can't match on 'z', so Exactly
        // emits its FAIL line with count= 0 and no inner Read/FAIL appears.
        var sink = NewSink();
        Exactly(Token('a'), 3).Parse("z", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   FAIL | Exactly[3]: count= 0"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }
}
