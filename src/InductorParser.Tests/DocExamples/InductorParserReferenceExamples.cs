using System.Linq;
using NUnit.Framework;
using InductorParser.SyntaxTree;
using InductorParser.Tracing;
using static InductorParser.Rules;

namespace InductorParser.Tests.DocExamples;

// Verifies the runnable code examples in docs/InductorParserReference.md.
// Each test mirrors a code block from the doc and asserts the documented
// behavior. Snippets that just describe API shape (the Rule class
// surface, the SymbolId struct, the ParseResult struct) are spot-checked
// elsewhere by reflection or by being used implicitly here; this file
// covers the runnable user-code examples.
[TestFixture]
public class InductorParserReferenceExamples
{
    // "Hello World Example": parse "setting = 5;" and recover name/value
    // via Tree.Find on the rule references.
    [Test]
    public void Hello_world_parses_setting_name_and_value()
    {
        var settingName = Identifier();

        var settingValue = Or(
            Float().Flatten(FlattenType.Flatten),
            Integer().Flatten(FlattenType.Flatten),
            Identifier()
        ).Preserve();

        var document = And(
            settingName,
            Optional(AnyWhitespace()),
            Token('='),
            Optional(AnyWhitespace()),
            settingValue,
            Optional(AnyWhitespace()),
            Token(';')
        ).Preserve();

        var result = document.Parse("setting = 5;");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree, Is.Not.Null,
            $"Tree should be a single wrapper Symbol; Symbols.Count={result.Symbols.Count}");
        var nameSym = result.Tree!.Find(settingName);
        Assert.That(nameSym, Is.Not.Null, "Find(settingName) should locate a Preserve wrapper");
        var valueSym = result.Tree!.Find(settingValue);
        Assert.That(valueSym, Is.Not.Null, "Find(settingValue) should locate a Preserve wrapper");
        Assert.That($"{nameSym} = {valueSym}", Is.EqualTo("setting = 5"));
    }

    // "Naming Rules": the doc example uses a class field with
    // `.As(nameof(SettingName))`. The compile-time check on nameof works
    // for fields because the field name is in scope inside its own
    // initializer.
    private static class NameOfFieldExample
    {
        public static readonly Rule SettingName =
            Identifier().As(nameof(SettingName)).Compile();
    }

    [Test]
    public void As_nameof_attaches_name_for_lookup()
    {
        Assert.That(NameOfFieldExample.SettingName.NameOf(NameOfFieldExample.SettingName.Id),
                    Is.EqualTo("SettingName"));
    }

    // "Naming Rules" / ".As(SymbolId)" pinned numeric ids. Doc claim: a
    // pinned SymbolId stays put across compiles, and a separate .As
    // attaches a debug name.
    [Test]
    public void Pinned_SymbolId_stays_put()
    {
        var pinned = new SymbolId(SymbolRanges.CustomRangeStart + 42);
        var thing = Identifier().As(pinned).As("Thing");
        var document = And(thing, Eof()).Compile();

        Assert.That(thing.Id, Is.EqualTo(pinned));
        Assert.That(document.NameOf(pinned), Is.EqualTo("Thing"));
    }

    // "What Compile Actually Does" / "Freeze the rule graph": after
    // Compile, .As / .Flatten / .WithError throw InvalidOperationException.
    [Test]
    public void Compile_seals_the_rule_graph()
    {
        var settingName = Identifier();
        var document = And(settingName, Eof()).Compile();

        Assert.Throws<System.InvalidOperationException>(() => document.As("doc"));
        Assert.Throws<System.InvalidOperationException>(() => document.Flatten(FlattenType.Preserve));
        Assert.Throws<System.InvalidOperationException>(() => document.WithError("oops"));
    }

    // "Rule Construction Is Fluent" example: chain .As, .Flatten,
    // .WithError on a single rule. Verify chaining returns a usable rule.
    [Test]
    public void Fluent_chain_works()
    {
        var settingName = Identifier()
            .As("settingName")
            .Flatten(FlattenType.Preserve)
            .WithError("Expected a setting name");

        var result = settingName.Parse("hello");
        Assert.That(result.Success, Is.True);
    }

    // "A Walkthrough With a Compiler Function": the doc shows a
    // CompileSetting function that converts a parse tree to a Setting
    // record. Re-create it and verify the doc's claim:
    //   "difficulty = hard;" => Setting("difficulty", "hard")
    private sealed record Setting(string Name, string Value);

    private static (Setting? result, string? error) CompileSetting(
        Rule root, Rule name, Rule valueRule, string input)
    {
        var parsed = root.Parse(input);
        if (!parsed.Success)
            return (null, $"Line {parsed.ErrorLine}: {parsed.ErrorMessage}");

        var nameText = parsed.Tree!.Find(name)!.ToString();
        var valueText = parsed.Tree!.Find(valueRule)!.ToString();
        return (new Setting(nameText, valueText), null);
    }

    private static (Rule document, Rule settingName, Rule settingValue) BuildWalkthroughGrammar()
    {
        var settingName = Identifier().As("settingName");
        var settingValue = Or(
            Float().Flatten(FlattenType.Flatten),
            Integer().Flatten(FlattenType.Flatten),
            Identifier()
        ).As("settingValue").Preserve();

        var document = And(
            Optional(AnyWhitespace()),
            settingName,
            Optional(AnyWhitespace()),
            Token('='),
            Optional(AnyWhitespace()),
            settingValue,
            Optional(AnyWhitespace()),
            Token(';'),
            Optional(AnyWhitespace()),
            Eof()
        ).As("document").Preserve().Compile();

        return (document, settingName, settingValue);
    }

    [Test]
    public void Compiler_function_walkthrough_returns_setting()
    {
        var (document, settingName, settingValue) = BuildWalkthroughGrammar();

        var (setting, error) = CompileSetting(document, settingName, settingValue, "difficulty = hard;");

        Assert.That(error, Is.Null);
        Assert.That(setting, Is.EqualTo(new Setting("difficulty", "hard")));
    }

    [Test]
    public void Compiler_function_walkthrough_reports_error()
    {
        var (document, settingName, settingValue) = BuildWalkthroughGrammar();

        // Missing semicolon: the doc claims an error result with line/message.
        var (setting, error) = CompileSetting(document, settingName, settingValue, "x = 5");

        Assert.That(setting, Is.Null);
        Assert.That(error, Does.StartWith("Line "));
    }

    // "A Bigger Example: Nested Rules": the doc shows a multi-setting
    // grammar where pair contains key + values, and values can be a
    // comma-separated list. Doc claim: parsing
    //   colors = red, green, blue;
    //   difficulty = hard;
    //   retries = 3;
    // yields three pairs whose keys and values match the documented shape.
    [Test]
    public void Bigger_example_parses_multiple_pairs()
    {
        var key = Identifier(extraStartRunes: TokenSet.Runes("_")).As("key");

        // Build a separate identifier-shaped alternative for valueAtom
        // because .Flatten(...) mutates the rule it's called on, and
        // reusing `key` here would flatten its position inside `pair` too.
        var valueAtom = Or(
            Float().Flatten(FlattenType.Flatten),
            Integer().Flatten(FlattenType.Flatten),
            Identifier(extraStartRunes: TokenSet.Runes("_"))
                .Flatten(FlattenType.Flatten)
        );

        var values = And(
            valueAtom,
            ZeroOrMore(
                And(
                    Optional(AnyWhitespace()),
                    Token(','),
                    Optional(AnyWhitespace()),
                    valueAtom
                )
            )
        ).As("values").Preserve();

        var pair = And(
            key,
            Optional(AnyWhitespace()),
            Token('='),
            Optional(AnyWhitespace()),
            values,
            Optional(AnyWhitespace()),
            Token(';')
        ).As("pair").Preserve();

        var document = And(
            Optional(AnyWhitespace()),
            ZeroOrMore(
                And(pair, Optional(AnyWhitespace()))
            ),
            Eof()
        ).As("document").Preserve().Compile();

        const string input =
            "colors = red, green, blue;\n" +
            "difficulty = hard;\n" +
            "retries = 3;\n";

        var result = document.Parse(input);
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        var pairs = result.Tree!.FindAll(pair).ToList();
        Assert.That(pairs.Count, Is.EqualTo(3));

        // pair[0]: colors = red, green, blue
        Assert.That(pairs[0].Find(key)!.ToString(), Is.EqualTo("colors"));
        Assert.That(pairs[0].Find(values)!.ToString(), Is.EqualTo("redgreenblue"),
            "values' ToString concatenates leaves; the comma delimiters Delete-flatten away");

        // pair[1]: difficulty = hard
        Assert.That(pairs[1].Find(key)!.ToString(), Is.EqualTo("difficulty"));
        Assert.That(pairs[1].Find(values)!.ToString(), Is.EqualTo("hard"));

        // pair[2]: retries = 3
        Assert.That(pairs[2].Find(key)!.ToString(), Is.EqualTo("retries"));
        Assert.That(pairs[2].Find(values)!.ToString(), Is.EqualTo("3"));
    }

    // "Tracing": setting ParseOptions.TraceSink + TraceLevel routes trace
    // output to a TextWriter.
    [Test]
    public void TraceSink_receives_output()
    {
        var grammar = And(Identifier(), Eof()).Compile();
        var sink = new System.IO.StringWriter();

        var options = new ParseOptions
        {
            TraceSink = sink,
            TraceLevel = TraceLevel.Diagnostic,
        };

        var result = grammar.Parse("hello", options);
        Assert.That(result.Success, Is.True);
        Assert.That(sink.ToString(), Is.Not.Empty,
            "TraceSink wired up should receive at least some output");
    }

    // "Catastrophic Backtracking and Timeouts": the doc claims the
    // default RuleCountLimit is 10_000_000 and that exceeding it returns
    // an Outcome distinct from GrammarMismatch. The budget is checked
    // every 1024 rule invocations (BudgetCheckInterval), so the input
    // has to be long enough to push past that boundary.
    [Test]
    public void RuleCountLimit_distinguishes_from_GrammarMismatch()
    {
        var grammar = ZeroOrMore(AnyToken()).Compile();
        var options = new ParseOptions { RuleCountLimit = 100 };
        var input = new string('a', 5000);

        var result = grammar.Parse(input, options);
        Assert.That(result.Outcome, Is.EqualTo(ParseOutcome.RuleCountLimitExceeded));
        Assert.That(result.Outcome, Is.Not.EqualTo(ParseOutcome.GrammarMismatch));
    }
}
