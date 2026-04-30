using System;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests;

[TestFixture]
public class LateBoundRuleTests
{
    // Mini expression grammar: integer, '(' expression ')', sums with '+'.
    // Expression refers to Term, Term refers back to Expression via the
    // LateBoundRule. Without late binding, this grammar can't be expressed
    // in C# (whichever rule is declared first would see the other as null).
    private static Rule BuildExpressionGrammar()
    {
        var expression = new LateBoundRule("expression");

        var term = FirstOf(Integer(), AllOf(Grapheme('('), expression, Grapheme(')')));
        var sum = AllOf(term, ZeroOrMore(AllOf(Grapheme('+'), term)));

        expression.Bind(sum);
        return sum;
    }

    // Tree.ToString() assertions use PreserveAllSymbols so the
    // Grapheme('+') / Grapheme('(') / Grapheme(')') leaves (default FlattenType.Delete)
    // stay in the tree and their text appears in the concatenated view.
    private static ParseOptions Debug() => new() { PreserveAllSymbols = true };

    [Test]
    public void Parses_linear_sum()
    {
        var root = BuildExpressionGrammar();

        var result = root.Parse("1+2+3", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("1+2+3"));
    }

    [Test]
    public void Parses_parenthesized_subexpression()
    {
        var root = BuildExpressionGrammar();

        var result = root.Parse("(1+2)+3", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("(1+2)+3"));
    }

    [Test]
    public void Parses_nested_parens()
    {
        var root = BuildExpressionGrammar();

        var result = root.Parse("((1+2)+(3+4))+5", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("((1+2)+(3+4))+5"));
    }

    [Test]
    public void Compile_throws_when_LateBoundRule_is_unbound()
    {
        var expression = new LateBoundRule("expression");

        var term = FirstOf(Integer(), AllOf(Grapheme('('), expression, Grapheme(')')));
        // Forgot the .Bind(...) call.

        var ex = Assert.Throws<InvalidOperationException>(() => term.Compile());
        Assert.That(ex!.Message, Does.Contain("expression"));
        Assert.That(ex.Message, Does.Contain("never bound"));
    }

    [Test]
    public void Parse_auto_compiles_and_throws_when_unbound()
    {
        // Same as above but via Parse instead of explicit Compile. Parse
        // is supposed to auto-compile on first call, which should surface
        // the unbound-rule error before any parsing starts.
        var expression = new LateBoundRule("expression");
        var term = FirstOf(Integer(), AllOf(Grapheme('('), expression, Grapheme(')')));

        Assert.Throws<InvalidOperationException>(() => term.Parse("1"));
    }

    [Test]
    public void As_Flatten_WithError_all_throw_on_LateBoundRule()
    {
        // All the modifier methods would silently do nothing on a
        // LateBoundRule because the rule is transparent at parse time.
        // Fail loudly instead.
        var expression = new LateBoundRule("expression");

        Assert.Throws<InvalidOperationException>(() => expression.As("someOtherName"));
        Assert.Throws<InvalidOperationException>(() => expression.As(new SymbolId(SymbolRanges.CustomRangeStart + 1)));
        Assert.Throws<InvalidOperationException>(() => expression.Flatten(FlattenType.Preserve));
        Assert.Throws<InvalidOperationException>(() => expression.WithError("Expected an expression"));
    }

    [Test]
    public void Bind_after_compile_throws()
    {
        // Once the graph is sealed, Bind should refuse to rewire.
        var expression = new LateBoundRule();
        expression.Bind(Integer());
        expression.Compile();

        Assert.Throws<InvalidOperationException>(() => expression.Bind(Integer()));
    }

    [Test]
    public void LateBoundRule_keeps_Preserve_target_symbol_in_parent_children()
    {
        // Regression: LateBoundRule's own FlattenType is Flatten, but when
        // it forwards to a target that's FlattenType.Preserve the target's
        // wrapper Symbol has to reach the enclosing composite's children
        // list. If the proxy drops it, a grammar like
        //   AllOf(X, lateBound, Y)
        // silently loses the Preserve wrapper between X and Y. Every
        // in-tree grammar happens to bind LateBoundRule to a Flatten
        // target (FirstOf/AllOf defaults), so this case was uncovered until
        // ArithmeticGrammar hit it.
        var named = AllOf(Grapheme('1'), Grapheme('2')).As("named").Flatten(FlattenType.Preserve);
        var late = new LateBoundRule("late");
        late.Bind(named);
        var outer = AllOf(Grapheme('a'), late, Grapheme('b'));

        var result = outer.Parse("a12b");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Symbols.Count, Is.EqualTo(1));
        Assert.That(result.Symbols[0].Id, Is.EqualTo(named.Id));
    }

    [Test]
    public void LateBoundRule_forwards_Preserve_target_through_recursive_grammar()
    {
        // Regression: an arithmetic-style grammar where `factor` references
        // `expression` via a LateBoundRule and `expression` is Preserve.
        // Without the LateBoundRule fix, the nested expression wrapper
        // disappears from `factor`'s children, so a parenthesized
        // sub-expression leaves no Symbol behind and an evaluator that
        // dispatches on expression.Id can't see the inner expression at
        // all.
        var expression = new LateBoundRule("expression");
        var factor = FirstOf(Integer(), AllOf(Grapheme('('), expression, Grapheme(')')));
        var expressionDef = AllOf(factor, ZeroOrMore(AllOf(Grapheme('+'), factor)))
            .As("expression").Flatten(FlattenType.Preserve);
        expression.Bind(expressionDef);

        var result = expressionDef.Parse("(1+2)+3");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree, Is.Not.Null);

        // Expect two expression-id Symbols: the outer root wrapper and
        // the nested wrapper for "1+2" inside the parens.
        int expressionSymbols = 0;
        foreach (var symbol in result.Tree!.Walk())
            if (symbol.Id == expressionDef.Id) expressionSymbols++;

        Assert.That(expressionSymbols, Is.EqualTo(2),
            "Expected outer and inner expression wrappers to both survive flattening.");
    }
}
