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
// so it survives flattening as a named node the evaluator can see.
// The AllOf/FirstOf/ZeroOrMore compositors around them keep their default
// FlattenType.Flatten and dissolve at flatten time, so a term's children
// flatten to alternating factors and mulOps with no wrapper nodes.
//
// AddOp and MulOp use OneOf so the matched rune survives as a leaf and
// ToString() returns "+", "-", "*", or "/" for the evaluator to dispatch on.
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
        Number = OneOrMore(OneOf(TokenSet.Ascii.Digits))
            .As("number").Preserve();

        AddOp = OneOf("+-").As("addOp").Preserve();
        MulOp = OneOf("*/").As("mulOp").Preserve();

        var exprForward = new LateBoundRule("expr");

        var factor = FirstOf(
            Number,
            AllOf(
                Token('('),
                Optional(AnyWhitespace()),
                exprForward,
                Optional(AnyWhitespace()),
                Token(')')
            )
        );

        Term = AllOf(
            factor,
            ZeroOrMore(AllOf(
                Optional(AnyWhitespace()),
                MulOp,
                Optional(AnyWhitespace()),
                factor
            ))
        ).As("term").Preserve();

        Expr = AllOf(
            Term,
            ZeroOrMore(AllOf(
                Optional(AnyWhitespace()),
                AddOp,
                Optional(AnyWhitespace()),
                Term
            ))
        ).As("expr").Preserve();

        exprForward.Bind(Expr);

        // Document wraps Expr with optional surrounding whitespace and a
        // strict Eof so "1+2 garbage" fails instead of silently parsing
        // the "1+2" prefix. Expr itself has no Eof so the parenthesized
        // factor can reuse it recursively.
        Document = AllOf(
            Optional(AnyWhitespace()),
            Expr,
            Optional(AnyWhitespace()),
            Eof()
        );
        Document.Compile();
    }
}
