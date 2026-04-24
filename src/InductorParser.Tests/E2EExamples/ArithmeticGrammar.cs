using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// E2E example: a tiny arithmetic expression grammar that pairs with
// ArithmeticEvaluator to show the "little compiler" story. Supports
// integer literals, +/-/*/, parentheses, and surrounding whitespace.
//
// Precedence and associativity fall out of the grammar shape:
//   expression = term (addOp term)*      -- left-associative, low precedence
//   term       = factor (mulOp factor)*  -- left-associative, higher precedence
//   factor     = number | '(' expression ')'
//
// Every spine rule (expr, term, number) is .As(name) + FlattenType.Preserve
// so it survives flattening as a named node the evaluator can dispatch
// on. The And/Or/ZeroOrMore compositors around them keep their default
// FlattenType.Flatten and dissolve at flatten time, so a term's
// flattened children are a clean alternation of factor-payloads and
// mulOp leaves with no anonymous wrapper layers in between.
//
// The addOp / mulOp rules are OneOf (not Token) so the matched operator
// rune survives as a FlattenType.Preserve leaf and ToString() gives back
// "+", "-", "*", or "/". Swap OneOf for Token and the leaf vanishes
// under default FlattenType.Delete, and the evaluator has nothing to
// pattern-match on.
public static class ArithmeticGrammar
{
    public static readonly Rule Number;
    public static readonly Rule AddOp;
    public static readonly Rule MulOp;
    public static readonly Rule Term;
    public static readonly Rule Expr;
    public static readonly Rule Document;

    static ArithmeticGrammar()
    {
        Number = OneOrMore(OneOf(RuneSet.Ascii.Digits))
            .As("number").Preserve();

        AddOp = OneOf("+-").As("addOp").Preserve();
        MulOp = OneOf("*/").As("mulOp").Preserve();

        var exprForward = new LateBoundRule("expr");

        var factor = Or(
            Number,
            And(
                Token('('),
                OptionalWhitespace(),
                exprForward,
                OptionalWhitespace(),
                Token(')')
            )
        );

        Term = And(
            factor,
            ZeroOrMore(And(OptionalWhitespace(), MulOp, OptionalWhitespace(), factor))
        ).As("term").Preserve();

        Expr = And(
            Term,
            ZeroOrMore(And(OptionalWhitespace(), AddOp, OptionalWhitespace(), Term))
        ).As("expr").Preserve();

        exprForward.Bind(Expr);

        // Document wraps Expr with optional surrounding whitespace and a
        // strict Eof so "1+2 garbage" fails instead of silently parsing
        // the "1+2" prefix. Expr itself has no Eof so the parenthesized
        // factor can reuse it recursively.
        Document = And(OptionalWhitespace(), Expr, OptionalWhitespace(), Eof());
        Document.Compile();
    }
}
