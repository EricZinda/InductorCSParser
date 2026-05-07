using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;

namespace InductorParser.Tests;

[TestFixture]
public class WithErrorTests
{
    [Test]
    public void WithError_message_appears_when_that_rule_is_deepest_failure()
    {
        // Name must be letters only. WithError gives the user-friendly message.
        var settingName = OneOrMore(OneOf(TokenSet.Letters))
            .WithError("Expected a setting name");

        var document = And(settingName, Token('='), Token(';'));

        // "1 = ;" fails at offset 0 because a digit isn't a letter.
        var result = document.Parse("1 = ;");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("Expected a setting name"));
        // OneOf records at pre-read offset 0 with null message. OneOrMore
        // then claims the slot with "Expected a setting name" via the
        // equal-depth rule.
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
    }

    [Test]
    public void Generic_error_when_no_rule_set_WithError()
    {
        var document = And(OneOrMore(OneOf(TokenSet.Letters)), Token('='), Token(';'));

        var result = document.Parse("ab");

        Assert.That(result.Success, Is.False);
        // OneOrMore consumes "ab", advancing to offset 2. Token('=') tries
        // at offset 2 and finds EOF. It records at offset 2. Since no rule
        // set WithError, the message falls back to the positional version.
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
        Assert.That(result.ErrorMessage, Does.StartWith("Unexpected end of input"));
    }

    [Test]
    public void Deepest_failure_wins_across_multiple_WithError_rules()
    {
        // Two rules with different WithError messages. The one whose failure
        // is deepest in the input should be the one the user sees.
        var name = OneOrMore(OneOf(TokenSet.Letters)).WithError("need letters");
        var digits = OneOrMore(OneOf(TokenSet.Digits)).WithError("need digits");
        var doc = And(name, Token('='), digits);

        // "ab=x" reaches the digits rule before failing (x isn't a digit).
        // "need digits" should win over "need letters" because the digit
        // failure is at a deeper position.
        var result = doc.Parse("ab=x");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("need digits"));
        // digits OneOf records at pre-read offset 3 (start of 'x').
        // OneOrMore claims the message slot there with "need digits".
        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
    }
}
