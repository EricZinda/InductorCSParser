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

        var term = Or(Integer(), And(Char('('), expression, Char(')')));
        var sum = And(term, ZeroOrMore(And(Char('+'), term)));

        expression.Bind(sum);
        return sum;
    }

    [Test]
    public void Parses_linear_sum()
    {
        var root = BuildExpressionGrammar();

        var result = root.Parse("1+2+3");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("1+2+3"));
    }

    [Test]
    public void Parses_parenthesized_subexpression()
    {
        var root = BuildExpressionGrammar();

        var result = root.Parse("(1+2)+3");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("(1+2)+3"));
    }

    [Test]
    public void Parses_nested_parens()
    {
        var root = BuildExpressionGrammar();

        var result = root.Parse("((1+2)+(3+4))+5");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("((1+2)+(3+4))+5"));
    }

    [Test]
    public void Compile_throws_when_LateBoundRule_is_unbound()
    {
        var expression = new LateBoundRule("expression");

        var term = Or(Integer(), And(Char('('), expression, Char(')')));
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
        var term = Or(Integer(), And(Char('('), expression, Char(')')));

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
        Assert.Throws<InvalidOperationException>(() => expression.Flatten(FlattenType.None));
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
}
