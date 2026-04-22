using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;

namespace InductorParser.Tests;

[TestFixture]
public class RuneInRuleTests
{
    [Test]
    public void RuneIn_matches_a_letter_and_returns_a_single_rune_symbol()
    {
        var rule = RuneIn(RuneSet.Letters);
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("a"));
    }

    [Test]
    public void RuneIn_mismatch_without_WithError_falls_back_to_positional_message()
    {
        // No WithError anywhere, so RuneIn records a null message at offset
        // 0 and BuildErrorMessage's positional fallback decides what to say.
        var rule = OneOrMore(RuneIn(RuneSet.Letters));
        var result = rule.Parse("1abc");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Does.StartWith("Parse failed at offset 0"));
    }

    [Test]
    public void RuneIn_EOF_on_empty_input_points_at_zero()
    {
        // OneOrMore requires at least one letter; empty input can't satisfy
        // that. RuneIn sees EOF on its first read and records at its pre-
        // read position 0 with its WithError message.
        var rule = OneOrMore(RuneIn(RuneSet.Letters).WithError("need a letter"));

        var result = rule.Parse("");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("need a letter"));
    }

    [Test]
    public void RuneIn_mismatch_at_start_points_at_offender()
    {
        // '1' is at offset 0; not a letter. RuneIn records its WithError
        // message at pre-read position 0.
        var rule = OneOrMore(RuneIn(RuneSet.Letters).WithError("need a letter"));

        var result = rule.Parse("1abc");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("need a letter"));
    }

    [Test]
    public void RuneIn_mismatch_after_successful_matches_points_at_first_bad_char()
    {
        // OneOrMore(Letters) commits "abc" up to offset 3. Then Token(';')
        // runs at offset 3, reads '1', and records its own WithError at
        // pre-read offset 3. That's deeper than the letter's WithError
        // (which is at offset 3 too, from the OneOrMore's terminating
        // attempt, but recorded first). First-writer at equal depth wins.
        //
        // To make the test unambiguous we only put a WithError on Token(';')
        // so there's no contention.
        var rule = And(OneOrMore(RuneIn(RuneSet.Letters)),
                       Token(';').WithError("expected ';'"));

        var result = rule.Parse("abc1");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected ';'"));
    }

    [Test]
    public void RuneIn_trace_success_produces_expected_output()
    {
        var sink = NewSink();
        RuneIn(RuneSet.Ascii.Letters).Parse("x", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: 'x', Consumed: 1",
            "   SUCC | RuneIn: found 'x', wanted one of '[A-Z,a-z]'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void RuneIn_trace_failure_produces_expected_output()
    {
        var sink = NewSink();
        RuneIn(RuneSet.Ascii.Letters).Parse("1", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: '1', Consumed: 1",
            "   FAIL | RuneIn: found '1', wanted one of '[A-Z,a-z]'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }
}
