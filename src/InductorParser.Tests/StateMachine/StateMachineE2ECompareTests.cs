using NUnit.Framework;
using InductorParser;
using InductorParser.StateMachine;

namespace InductorParser.Tests.StateMachine;

// Cross-validates the state-machine evaluator against the recursive
// evaluator on every E2E example grammar in the test suite. Each
// grammar exercises a different mix of rules, so this catches cases
// where the state machine's lowering produces different output
// shapes from the recursive path. Inputs cover both success and
// failure cases and exercise WithError, LiteralIgnoreAsciiCase,
// and the bridge fallback paths.
[TestFixture]
public class StateMachineE2ECompareTests
{
    [TestCase("C", true)]
    [TestCase("Am", true)]
    [TestCase("F#m7", true)]
    [TestCase("Cmaj7", true)]
    [TestCase("Bb", true)]
    [TestCase("Ebm9", true)]
    [TestCase("Csus2", true)]
    [TestCase("Csus4", true)]
    [TestCase("Cadd9", true)]
    [TestCase("C#m7b5", true)]
    [TestCase("C/G", true)]
    [TestCase("F#m7/A", true)]
    [TestCase("Caug7", true)]
    [TestCase("Cdim", true)]
    [TestCase("X", false)]
    [TestCase("Cnonsense", false)]
    [TestCase("", false)]
    public void Chord_grammar_agrees_with_recursive_evaluator(string input, bool expectSuccess)
    {
        var rule = ChordGrammar.Chord;
        AssertEvaluatorsAgree(rule, input, expectSuccess);
    }

    [TestCase("\"hello\"", true)]
    [TestCase("\"\"", true)]
    [TestCase("\"a\\nb\"", true)]
    [TestCase("\"\\u00E9\"", true)]
    [TestCase("{}", true)]
    [TestCase("{\"a\":\"b\"}", true)]
    [TestCase("[\"a\",\"b\",\"c\"]", true)]
    [TestCase("[", false)]
    [TestCase("notjson", false)]
    public void Json_grammar_agrees_with_recursive_evaluator(string input, bool expectSuccess)
    {
        var rule = JsonGrammar.Json;
        AssertEvaluatorsAgree(rule, input, expectSuccess);
    }

    private static void AssertEvaluatorsAgree(Rule rule, string input, bool expectSuccess)
    {
        // Default ParseOptions runs both engines through FormC
        // normalization. Failure positions stay comparable because both
        // engines translate the lexer's normalized-space position back
        // to caller-original coordinates the same way.
        var options = new ParseOptions();
        var legacy = rule.ParseRecursive(input, options);
        var stateMachine = StateMachineParser.Parse(rule, input, options);

        Assert.That(legacy.Success, Is.EqualTo(expectSuccess), $"recursive outcome: {legacy.ErrorMessage}");
        Assert.That(stateMachine.Success, Is.EqualTo(expectSuccess), $"state-machine outcome: {stateMachine.ErrorMessage}");

        if (expectSuccess)
        {
            // ToString reconstructs the matched leaf text. After tree
            // flattening both evaluators should reproduce the same
            // string for the same input.
            Assert.That(stateMachine.ToString(), Is.EqualTo(legacy.ToString()),
                $"tree text differs on '{input}'");
        }
    }
}
