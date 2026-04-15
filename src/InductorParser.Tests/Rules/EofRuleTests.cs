using NUnit.Framework;
using static InductorParser.Rules;

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
        // Char('a') matches. Eof() runs at offset 1 and finds 'b' there,
        // records its WithError at the current position.
        var rule = And(Char('a').WithError("need an 'a'"),
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
        var rule = And(Char('a'), Eof());

        var result = rule.Parse("ab");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
        Assert.That(result.ErrorMessage, Does.StartWith("Parse failed at offset 1"));
    }
}
