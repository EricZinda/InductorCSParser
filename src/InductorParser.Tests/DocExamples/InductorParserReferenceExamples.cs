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
// elsewhere by reflection or by being used implicitly here. This file
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

    // "Naming Rules": the doc says a named rule "is one the caller wants
    // to locate later with Tree.Find or result.Find", and that naming a
    // rule flips its FlattenType to Preserve so its Symbol reaches the
    // tree. Exercise the ParseResult-level lookup (which walks every
    // top-level Symbol) so it works even when the root keeps its Flatten
    // default and result.Tree is null.
    [Test]
    public void Naming_a_rule_makes_it_findable_via_result_find()
    {
        var word = Identifier().As("word");
        var breaking = Token('!').As("breaking");
        var document = And(word, breaking);

        var result = document.Parse("change!");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        // The root And keeps its Flatten default, so result.Tree is null
        // (two top-level Symbols bubbled up). result.Find walks every
        // top-level Symbol, so it locates the named rules regardless.
        Assert.That(result.Tree, Is.Null);
        Assert.That(result.Find(word)!.ToString(), Is.EqualTo("change"));
        Assert.That(result.Find(breaking)!.ToString(), Is.EqualTo("!"));
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

    // "Naming Rules" / ".As(SymbolId)" explicit numeric ids. Doc claim: an
    // explicit SymbolId stays put across compiles, and a separate .As
    // attaches a debug name. The two overloads write different fields
    // (Id and Name) so they compose cleanly on a single instance.
    [Test]
    public void Explicit_SymbolId_stays_put()
    {
        var explicitId = new SymbolId(SymbolRanges.CustomRangeStart + 42);
        var thing = Identifier().As(explicitId).As("Thing");
        var document = And(thing, Eof()).Compile();

        Assert.That(thing.Id, Is.EqualTo(explicitId));
        Assert.That(document.NameOf(explicitId), Is.EqualTo("Thing"));
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
            return (null, parsed.ErrorMessage);

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
        ).As("settingValue");

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
        ).As("document").Compile();

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

        // Missing semicolon: the doc claims an error result whose message
        // already includes a 1-based position from the default template.
        var (setting, error) = CompileSetting(document, settingName, settingValue, "x = 5");

        Assert.That(setting, Is.Null);
        Assert.That(error, Does.StartWith("Unexpected end of input"));
        Assert.That(error, Does.Contain("line 1"));
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

        // Named so each matched value survives as its own node under
        // `values` (the name flips the Or's default Flatten to Preserve).
        // The identifier-shaped alternative is built fresh rather than
        // reusing `key`: this spot needs a flattened, unnamed identifier,
        // and `key` is named (so Preserve). Calling
        // .Flatten(FlattenType.Flatten) on it would throw rather than
        // silently override the .As choice.
        var valueAtom = Or(
            Float().Flatten(FlattenType.Flatten),
            Integer().Flatten(FlattenType.Flatten),
            Identifier(extraStartRunes: TokenSet.Runes("_"))
                .Flatten(FlattenType.Flatten)
        ).As("value");

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
        ).As("values");

        var pair = And(
            key,
            Optional(AnyWhitespace()),
            Token('='),
            Optional(AnyWhitespace()),
            values,
            Optional(AnyWhitespace()),
            Token(';')
        ).As("pair");

        var document = And(
            Optional(AnyWhitespace()),
            ZeroOrMore(
                And(pair, Optional(AnyWhitespace()))
            ),
            Eof()
        ).As("document").Compile();

        const string input =
            "colors = red, green, blue;\n" +
            "difficulty = hard;\n" +
            "retries = 3;\n";

        var result = document.Parse(input);
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        var pairs = result.Tree!.FindAll(pair).ToList();
        Assert.That(pairs.Count, Is.EqualTo(3));

        // pair[0]: colors = red, green, blue. Each value is its own
        // [value] node because valueAtom is named, which is the doc's
        // tree-diagram shape.
        Assert.That(pairs[0].Find(key)!.ToString(), Is.EqualTo("colors"));
        var colorsValues = pairs[0].Find(values)!;
        Assert.That(colorsValues.Children.Select(c => c.ToString()).ToArray(),
            Is.EqualTo(new[] { "red", "green", "blue" }),
            "each value survives as its own node because valueAtom is named");
        Assert.That(colorsValues.Children.All(c => c.Is(valueAtom)), Is.True,
            "every child of values comes from the valueAtom rule");
        Assert.That(colorsValues.ToString(), Is.EqualTo("redgreenblue"),
            "values' ToString still concatenates all descendant leaves (the commas are Delete'd)");

        // pair[1]: difficulty = hard
        Assert.That(pairs[1].Find(key)!.ToString(), Is.EqualTo("difficulty"));
        Assert.That(pairs[1].Find(values)!.ToString(), Is.EqualTo("hard"));

        // pair[2]: retries = 3
        Assert.That(pairs[2].Find(key)!.ToString(), Is.EqualTo("retries"));
        Assert.That(pairs[2].Find(values)!.ToString(), Is.EqualTo("3"));

        // The value nodes all have valueAtom's .As name. No rule is named
        // "integerExpression", so no node can have that label, and the
        // integer value sits under a [value] node exactly like the other
        // atoms.
        var allLabels = result.Tree!.Walk().Select(s => result.DisplayName(s)).ToList();
        Assert.That(allLabels, Has.None.EqualTo("integerExpression"),
            "No rule is named 'integerExpression', so the tree can't contain that label.");
        var retriesValues = pairs[2].Find(values)!;
        Assert.That(retriesValues.Children.Select(c => c.ToString()).ToArray(),
            Is.EqualTo(new[] { "3" }),
            "the retries values list holds one [value] node whose text is 3");
        Assert.That(retriesValues.Children.Single().Is(valueAtom), Is.True);
    }

    // "LINQ on the Symbol Tree": the doc lists four LINQ entry points
    // (Children, Walk, FindAll, and "Flattened tree as a list"). The last
    // one read `symbol.FlattenInto().OfType<Symbol>()`, which doesn't
    // compile: Symbol.FlattenInto takes a List<Symbol> and returns void.
    // The list-returning method is Flatten(), which returns
    // IReadOnlyList<Symbol>, exactly what the prose ("each one is typed as
    // IReadOnlyList<Symbol> or IEnumerable<Symbol>") and the comment
    // ("as a list") promise. Running all four keeps the snippet from
    // drifting back to a non-compiling form.
    [Test]
    public void Linq_on_the_symbol_tree_entry_points()
    {
        var settingName = Identifier().As("settingName");
        var number = Integer().As("number");
        var entry = Or(settingName, number);
        var document = And(entry, ZeroOrMore(And(Token(','), entry)), Eof())
            .As("document").Compile();

        var result = document.Parse("alpha,42,beta");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        var symbol = result.Tree!;

        // Direct children (no recursion)
        var directNames = symbol.Children.Where(c => c.Id == settingName.Id).ToList();
        Assert.That(directNames.Count, Is.EqualTo(2));

        // Entire subtree, pre-order walk
        var integers = symbol.Walk().Where(s => s.Id == number.Id).ToList();
        Assert.That(integers.Count, Is.EqualTo(1));
        Assert.That(integers[0].ToString(), Is.EqualTo("42"));

        // All descendants matching a specific rule
        var nameTexts = symbol.FindAll(settingName).Select(s => s.ToString()).ToList();
        Assert.That(nameTexts, Is.EqualTo(new[] { "alpha", "beta" }));

        // Flattened tree as a list. Flatten() returns IReadOnlyList<Symbol>,
        // a direct LINQ target exactly as the surrounding prose claims.
        var flattened = symbol.Flatten().OfType<Symbol>().ToList();
        Assert.That(flattened.Count, Is.GreaterThan(0));
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
