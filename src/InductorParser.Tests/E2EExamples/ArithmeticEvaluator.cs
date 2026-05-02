using System;
using System.Globalization;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Tests.ArithmeticGrammar;

namespace InductorParser.Tests;

// A "little compiler" for ArithmeticGrammar. Parses an expression,
// flattens the tree, and evaluates it to a long.
//
// What the flattened tree looks like: (Expr, Term,
// Number) is FlattenType.Preserve so they stay in the final tree.
// Everything else disappears since it's just syntax for the user:
//
//   expr
//   ├── term
//   │   └── number ── "1"
//   ├── "+"
//   └── term
//       ├── number ── "2"
//       ├── "*"
//       └── number ── "3"
//
// Parentheses add nothing to the tree besides recording subexpressions
// since those are represented by the tree, we can remove those symbols too
// For "(1+2)*3":
//
//   expr
//   └── term
//       ├── expr            (parenthesized, the '(' and ')' were deleted)
//       │   ├── term
//       │   │   └── number ── "1"
//       │   ├── "+"
//       │   └── term
//       │       └── number ── "2"
//       ├── "*"
//       └── number ── "3"
//
// Evaluating the tree takes three small methods that call each other.
//
//   EvaluateExpr   an expr node. Its children are laid out as
//                  term, +/-, term, +/-, term, ... Walk left to
//                  right and apply each operator to the running total.
//   EvaluateTerm   a term node. Same shape as expr but with * and /
//                  between the operands.
//   EvaluateFactor one operand from a term. It's either a number
//                  (parse the digits) or a parenthesized expr (call
//                  EvaluateExpr again).
//
// factor exists for precedence. At parse time, expr only lets you
// join terms with + and -, term only lets you join factors with
// * and /, and factor is an atom: a number or a parenthesized
// expression, nothing else. That nesting is what makes 1+2*3 group as
// 1+(2*3). The * operator is only allowed at the factor-joining
// level, so it grabs its neighbors before + gets a chance.
//
// Once parsing is done, factor's work is finished so we don't need to
// keep it in the tree as a node. A term's operand
// shows up directly as either a Number or an Expr (the parenthesized
// case). EvaluateFactor picks between those two, and the Expr branch
// is where the call chain loops back to EvaluateExpr for nested
// parens.
//
// Walking each level left to right is what makes "1-2-3" evaluate to
// -4 instead of 2: the leftmost operator binds first. The grammar
// already laid the children out in that order, so nothing clever is
// needed in the evaluator.
public static class ArithmeticEvaluator
{
    public static long Evaluate(string input)
    {
        var result = Document.Parse(input);
        if (!result.Success)
            throw new FormatException(result.ErrorMessage);

        return EvaluateExpr(result.Tree!);
    }

    // Handles + and -. Children are laid out as term, +/-, term, +/-,
    // term, so walk left to right and apply each operator to a running
    // total.
    private static long EvaluateExpr(Symbol symbol)
    {
        if (!symbol.Is(Expr))
            throw new InvalidOperationException($"Expected expr, got {symbol.Id.Value}");

        long accumulator = EvaluateTerm(symbol.Children[0]);
        for (int i = 1; i < symbol.Children.Count; i += 2)
        {
            string operatorText = symbol.Children[i].ToString();
            long right = EvaluateTerm(symbol.Children[i + 1]);
            accumulator = operatorText == "+" ? accumulator + right : accumulator - right;
        }
        return accumulator;
    }

    // Called by EvaluateExpr on each term child. Handles * and /. Same
    // child layout as expr but with * and / between the operands, so
    // walk left to right the same way.
    private static long EvaluateTerm(Symbol symbol)
    {
        if (!symbol.Is(Term))
            throw new InvalidOperationException($"Expected term, got {symbol.Id.Value}");

        long accumulator = EvaluateFactor(symbol.Children[0]);
        for (int i = 1; i < symbol.Children.Count; i += 2)
        {
            string operatorText = symbol.Children[i].ToString();
            long right = EvaluateFactor(symbol.Children[i + 1]);
            accumulator = operatorText == "*" ? accumulator * right : accumulator / right;
        }
        return accumulator;
    }

    // Called by EvaluateTerm on each operand. The
    // operand is either a Number (parse the digits) or an Expr (the
    // parenthesized case, recurse back to EvaluateExpr). This is where
    // the call chain loops.
    private static long EvaluateFactor(Symbol symbol)
    {
        if (symbol.Is(Number))
            return long.Parse(symbol.ToString(), CultureInfo.InvariantCulture);
        if (symbol.Is(Expr))
            return EvaluateExpr(symbol);
        throw new InvalidOperationException($"Unexpected factor id {symbol.Id.Value}");
    }
}
