using NUnit.Framework;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests;

[TestFixture]
public class SettingExampleTests
{
    private static (Rule document, Rule settingName, Rule settingValue) BuildGrammar()
    {
        var settingName = Identifier();

        var settingValue = Or(
            Float(),
            Integer(),
            Identifier()
        ).Flatten(FlattenType.Preserve);

        var document = And(
            settingName,
            OptionalWhitespace(),
            Token('='),
            OptionalWhitespace(),
            settingValue,
            OptionalWhitespace(),
            Token(';')
        ).Flatten(FlattenType.Preserve);

        return (document, settingName, settingValue);
    }

    [Test]
    public void Parses_integer_setting()
    {
        var (document, settingName, settingValue) = BuildGrammar();

        var result = document.Parse("setting = 5;");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.Find(settingName)!.ToString(), Is.EqualTo("setting"));
        Assert.That(result.Tree!.Find(settingValue)!.ToString(), Is.EqualTo("5"));
    }

    [Test]
    public void Parses_string_value()
    {
        var (document, settingName, settingValue) = BuildGrammar();

        var result = document.Parse("difficulty=hard;");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.Find(settingName)!.ToString(), Is.EqualTo("difficulty"));
        Assert.That(result.Tree!.Find(settingValue)!.ToString(), Is.EqualTo("hard"));
    }

    [Test]
    public void Parses_float_value()
    {
        var (document, settingName, settingValue) = BuildGrammar();

        var result = document.Parse("ratio = 3.14;");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.Find(settingName)!.ToString(), Is.EqualTo("ratio"));
        Assert.That(result.Tree!.Find(settingValue)!.ToString(), Is.EqualTo("3.14"));
    }

    [Test]
    public void Fails_when_semicolon_missing()
    {
        var (document, _, _) = BuildGrammar();

        var result = document.Parse("setting = 5");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Outcome, Is.EqualTo(ParseOutcome.GrammarMismatch));
        // Token(';') tries at offset 11 (end of input) and finds EOF.
        // Under the error-position principle the failure is recorded at
        // the pre-read position 11, which equals input.Length, so the
        // error message renders "Unexpected end of input".
        Assert.That(result.ErrorCharIndex, Is.EqualTo(11));
        Assert.That(result.ErrorMessage, Does.StartWith("Unexpected end of input"));
    }

    [Test]
    public void Find_locates_named_subtree_by_rule_reference()
    {
        var (document, settingName, settingValue) = BuildGrammar();
        document.Compile();

        var result = document.Parse("alpha = beta;");

        Assert.That(result.Success, Is.True);
        var nameSym = result.Tree!.Find(settingName);
        var valueSym = result.Tree!.Find(settingValue);
        Assert.That(nameSym, Is.Not.Null);
        Assert.That(valueSym, Is.Not.Null);
        Assert.That(nameSym!.Id, Is.EqualTo(settingName.Id));
        Assert.That(valueSym!.Id, Is.EqualTo(settingValue.Id));
    }

}
