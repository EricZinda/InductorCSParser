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

        var term = Or(Integer(), And(Token('('), expression, Token(')')));
        var sum = And(term, ZeroOrMore(And(Token('+'), term)));

        expression.Bind(sum);
        return sum;
    }

    // Tree.ToString() assertions use PreserveAllSymbols so the
    // Token('+') / Token('(') / Token(')') leaves (default FlattenType.Delete)
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

        var term = Or(Integer(), And(Token('('), expression, Token(')')));
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
        var term = Or(Integer(), And(Token('('), expression, Token(')')));

        Assert.Throws<InvalidOperationException>(() => term.Parse("1"));
    }

    [Test]
    public void As_Flatten_WithError_all_throw_on_LateBoundRule()
    {
        // All the modifier methods would silently do nothing on a
        // LateBoundRule because the rule is transparent at parse time.
        // They throw instead.
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
    public void Bind_throws_on_second_call_before_compile()
    {
        // .As(string), .As(SymbolId), and .WithError(...) are all
        // set-once on a Rule and throw on a second call to the same
        // instance. .Bind on a LateBoundRule should be too: a second
        // Bind before Compile silently swaps the target, so an
        // accidental double-bind (a copy-pasted Bind line, a factory
        // function called twice on the same instance, two static-init
        // sites for the same LateBoundRule) is impossible to diagnose
        // from the parse output. The grammar then runs against
        // whichever target initialized last, with no error pointing at
        // the duplicate Bind call.
        var expression = new LateBoundRule();
        expression.Bind(Integer());

        Assert.Throws<InvalidOperationException>(() => expression.Bind(Token('a')));
    }

    [Test]
    public void LateBoundRule_keeps_Preserve_target_symbol_in_parent_children()
    {
        // Regression: LateBoundRule's own FlattenType is Flatten, but when
        // it forwards to a target that's FlattenType.Preserve the target's
        // wrapper Symbol has to reach the enclosing composite's children
        // list. If the proxy drops it, a grammar like
        //   And(X, lateBound, Y)
        // silently loses the Preserve wrapper between X and Y. Every
        // in-tree grammar happens to bind LateBoundRule to a Flatten
        // target (Or/And defaults), so this case was uncovered until
        // ArithmeticGrammar hit it.
        var named = And(Token('1'), Token('2')).As("named").Flatten(FlattenType.Preserve);
        var late = new LateBoundRule("late");
        late.Bind(named);
        var outer = And(Token('a'), late, Token('b'));

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
        var factor = Or(Integer(), And(Token('('), expression, Token(')')));
        var expressionDef = And(factor, ZeroOrMore(And(Token('+'), factor)))
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

    // --- LateBound bound to LateBound -------------------------------------

    [Test]
    public void Chain_of_LateBoundRules_forwards_to_the_real_target()
    {
        // outer -> middle -> Integer(). Every LateBoundRule is transparent
        // at parse time, so a multi-link chain should forward straight
        // through to the real rule with no trace of the placeholders.
        var outer = new LateBoundRule("outer");
        var middle = new LateBoundRule("middle");
        outer.Bind(middle);
        middle.Bind(Integer());

        var result = outer.Parse("42", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("42"));
    }

    [Test]
    public void Chain_works_when_the_tail_is_bound_before_the_head()
    {
        // Binding order between the two placeholders shouldn't matter.
        // Here `middle` is bound to its real target first, then `outer` is
        // bound to `middle`. The chain still resolves the same way as the
        // head-first order above.
        var outer = new LateBoundRule("outer");
        var middle = new LateBoundRule("middle");
        middle.Bind(Integer());
        outer.Bind(middle);

        var result = outer.Parse("99", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("99"));
    }

    [Test]
    public void Chain_of_LateBoundRules_forwards_a_Preserve_target_to_the_parent()
    {
        // Same regression as LateBoundRule_keeps_Preserve_target_symbol_in_
        // parent_children, but the Preserve target sits behind TWO
        // LateBoundRule layers. Each layer's transparent forwarding has to
        // re-add the target's wrapper to the parent list, or the wrapper
        // is lost somewhere in the chain.
        var named = And(Token('1'), Token('2')).As("named").Flatten(FlattenType.Preserve);
        var middle = new LateBoundRule("middle");
        var outer = new LateBoundRule("outer");
        outer.Bind(middle);
        middle.Bind(named);
        var grammar = And(Token('a'), outer, Token('b'));

        var result = grammar.Parse("a12b");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Symbols.Count, Is.EqualTo(1));
        Assert.That(result.Symbols[0].Id, Is.EqualTo(named.Id));
    }

    [Test]
    public void Recursive_grammar_routes_recursion_through_two_LateBoundRules()
    {
        // The expression grammar from BuildExpressionGrammar, but `term`
        // is also a LateBoundRule. The recursion cycle now passes through
        // two placeholders: expression -> term -> expression. Both have to
        // be bound and both have to forward correctly for the cycle to
        // parse.
        var expression = new LateBoundRule("expression");
        var term = new LateBoundRule("term");

        var termDef = Or(Integer(), And(Token('('), expression, Token(')')));
        var expressionDef = And(term, ZeroOrMore(And(Token('+'), term)));

        term.Bind(termDef);
        expression.Bind(expressionDef);

        var result = expressionDef.Parse("((1+2)+3)+4", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("((1+2)+3)+4"));
    }

    [Test]
    public void Compile_throws_when_a_chained_LateBoundRule_tail_is_unbound()
    {
        // `outer` is bound (to `middle`), but `middle` is never bound.
        // Compile's validation walk reaches `middle` through `outer` and
        // must report `middle` by name. `outer`'s own "I'm bound" state
        // can't mask an unbound rule deeper in the chain.
        var outer = new LateBoundRule("outer");
        var middle = new LateBoundRule("middle");
        outer.Bind(middle);
        // middle.Bind(...) deliberately missing.

        var ex = Assert.Throws<InvalidOperationException>(() => outer.Compile());
        Assert.That(ex!.Message, Does.Contain("middle"));
        Assert.That(ex.Message, Does.Contain("never bound"));
    }

    [Test]
    public void Self_bound_LateBoundRule_is_rejected_at_compile()
    {
        // A LateBoundRule bound directly to itself is a broken grammar:
        // the .Bind chain loops with no concrete rule, so it has no
        // FlattenType and can never match input. Compile resolves each
        // LateBoundRule's FlattenType by walking its .Bind chain, detects
        // the loop, and throws a clear error. (Without that walk, the
        // first FlattenType read at parse time would recurse forever into
        // an uncatchable StackOverflowException.)
        var self = new LateBoundRule("self");
        self.Bind(self);

        var ex = Assert.Throws<InvalidOperationException>(() => self.Parse("x"));
        Assert.That(ex!.Message, Does.Contain("loop"));
    }

    [Test]
    public void Two_LateBoundRules_in_a_base_caseless_cycle_are_rejected_at_compile()
    {
        // outer -> inner -> outer, all LateBoundRules, nothing concrete in
        // the loop. Same broken shape as the self-bind above, split across
        // two placeholders. Compile's FlattenType resolution walks the
        // .Bind chain, detects the loop, and throws.
        var outer = new LateBoundRule("outer");
        var inner = new LateBoundRule("inner");
        outer.Bind(inner);
        inner.Bind(outer);

        var ex = Assert.Throws<InvalidOperationException>(() => outer.Parse("x"));
        Assert.That(ex!.Message, Does.Contain("loop"));
    }

    // --- FlattenType forwarding ------------------------------------------

    [Test]
    public void LateBoundRule_reports_its_bound_targets_FlattenType()
    {
        // A LateBoundRule has no FlattenType of its own; after Compile it
        // reports the bound target's. This is what lets a LateBoundRule
        // compose into And / Or / Alias exactly as the target rule would.
        var preserveTarget = And(Token('1'), Token('2')).As("named");   // .As flips to Preserve
        var flattenTarget = Or(Token('a'), Token('b'));                 // Or defaults to Flatten

        var toPreserve = new LateBoundRule("toPreserve");
        toPreserve.Bind(preserveTarget);
        var toFlatten = new LateBoundRule("toFlatten");
        toFlatten.Bind(flattenTarget);

        // Compile so ValidateCompiled resolves each placeholder's FlattenType.
        And(toPreserve, toFlatten).Compile();

        Assert.That(toPreserve.FlattenType, Is.EqualTo(FlattenType.Preserve));
        Assert.That(toFlatten.FlattenType, Is.EqualTo(FlattenType.Flatten));
    }

    [Test]
    public void LateBoundRule_FlattenType_resolves_through_a_chain_to_the_concrete_rule()
    {
        // outer -> middle -> a Preserve rule. FlattenType resolution walks
        // the whole chain of LateBoundRules to the first concrete rule.
        var concrete = OneOrMore(OneOf(TokenSet.Digits)).As("digits");  // Preserve
        var middle = new LateBoundRule("middle");
        var outer = new LateBoundRule("outer");
        outer.Bind(middle);
        middle.Bind(concrete);

        outer.Compile();

        Assert.That(middle.FlattenType, Is.EqualTo(FlattenType.Preserve));
        Assert.That(outer.FlattenType, Is.EqualTo(FlattenType.Preserve));
    }

    [Test]
    public void LateBoundRule_FlattenType_read_before_Compile_throws()
    {
        // The FlattenType is resolved during Compile. Reading it earlier
        // has no answer, so it throws rather than returning a guess that
        // a later Compile would contradict.
        var lateBound = new LateBoundRule("placeholder");
        lateBound.Bind(Or(Token('a'), Token('b')));

        Assert.Throws<InvalidOperationException>(() => { var _ = lateBound.FlattenType; });
    }
}
