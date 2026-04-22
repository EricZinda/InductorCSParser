using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;

namespace InductorParser.Tests;

[TestFixture]
public class EofRuleTests
{
    [Test]
    public void Eof_at_end_of_input_succeeds()
    {
        // Control case: Eof() on empty input succeeds with no error.
        var rule = Eof();
        var result = rule.Parse("");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Eof_fails_at_current_position_when_input_has_content()
    {
        // Token('a') matches. Eof() runs at offset 1 and finds 'b' there,
        // records its WithError at the current position.
        var rule = And(Token('a').WithError("need an 'a'"),
                       Eof().WithError("expected end of input"));

        var result = rule.Parse("ab");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected end of input"));
    }

    [Test]
    public void Eof_failure_without_WithError_falls_back_to_positional_message()
    {
        // No WithError anywhere. Eof records a null message at offset 1 and
        // BuildErrorMessage's positional fallback renders the message.
        var rule = And(Token('a'), Eof());

        var result = rule.Parse("ab");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
        Assert.That(result.ErrorMessage, Does.StartWith("Parse failed at offset 1"));
    }

    [Test]
    public void Eof_trace_success_produces_expected_output()
    {
        // EofRule doesn't open a transaction and top-level Parse doesn't
        // either, so the success line sits at depth 0 with no leading
        // indentation. The message is empty, so there's no ": {detail}"
        // tail either — the line reads simply "SUCC | Eof".
        var sink = NewSink();
        Eof().Parse("", new ParseOptions { TraceSink = sink });

        string expected = Lines("SUCC | Eof");
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Eof_trace_failure_produces_expected_output()
    {
        // Wrapped in And so there's a transaction open when Eof fails,
        // giving us a non-trivial indentation to pin.
        var sink = NewSink();
        And(Eof()).Parse("x", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   FAIL | Eof: found x",
            "   FAIL | And: symbol #0"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }
}
