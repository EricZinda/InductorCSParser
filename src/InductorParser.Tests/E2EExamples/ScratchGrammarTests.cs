using NUnit.Framework;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// A throwaway place to try out a grammar under the debugger. Safe to
// delete. The real coverage conventions live in docs/TestArchitecture.md;
// this file is only meant as a starting point for experimenting.
[TestFixture]
public class ScratchGrammarTests
{
    // A tiny grammar:  NAME = VALUE
    //   name  -> an identifier (letters / digits / underscore)
    //   value -> an integer, or another identifier (a bare word)
    // Returns the rules we want to inspect after parsing so the tests can
    // pull their matched text back out of the tree by rule reference.
    private static (Rule pair, Rule name, Rule value) BuildGrammar()
    {
        var name = Identifier();
        var value = Or(Integer(), Identifier()).Flatten(FlattenType.Preserve);

        var pair = And(
            name,
            Optional(AnyWhitespace()),
            Token('='),
            Optional(AnyWhitespace()),
            value
        ).Flatten(FlattenType.Preserve);

        return (pair, name, value);
    }

    [Test]
    public void Parses_a_name_equals_integer()
    {
        var (pair, name, value) = BuildGrammar();

        // Set a breakpoint on this line, then click "Debug Test" above the
        // method name. Step over (F10) and inspect `result` in the Variables
        // panel: result.Success, result.Tree, result.ErrorMessage. Press
        // F11 on Parse to step into the parser itself.
        var result = pair.Parse("count = 42");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.Find(name)!.ToString(), Is.EqualTo("count"));
        Assert.That(result.Tree!.Find(value)!.ToString(), Is.EqualTo("42"));
    }

    [Test]
    public void Reports_where_it_failed_when_the_value_is_missing()
    {
        var (pair, _, _) = BuildGrammar();

        var result = pair.Parse("count = ");

        Assert.That(result.Success, Is.False);
        // ErrorCharIndex is the position the failing read started at, and
        // ErrorMessage explains it. Watch these two in the debugger to see
        // why a grammar rejected an input.
        TestContext.Out.WriteLine($"failed at char {result.ErrorCharIndex}: {result.ErrorMessage}");
    }
}
