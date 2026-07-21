// Newsboat filter parser using InductorParser, vs ../Original/FilterParser.cs:
//
//                     Original (hand-written)         Rewrite (this file)
//   Failure position  char index                      char index + line/column
//   Error message     attribute / operator / value    same, named via WithError
//   AST shape         OriginalAnd/Or/Comparison       RewriteAnd/Or/Comparison
//   Source map        none                            SourceRange on every AST node
//
// The rewrite keeps the original source range on every AST node.
// Comparisons read theirs straight from Symbol.SourceRange, and a
// compound And / Or spans from the start of its left subtree to the
// end of its right one (new SourceRange(left.Start, right.End)). A
// downstream tool (a Newsboat config editor, a syntax highlighter,
// a query builder UI) can highlight or rewrite individual sub-
// expressions without re-parsing. Each AST node's text comes from
// Symbol.SourceText, which is a verbatim section of the user's input,
// so we don't have to .Preserve() syntactic punctuation just to keep
// it in the tree (the leading minus on a number, the colon in a range,
// the surrounding quotes on a string, etc.).

using System;
using System.Collections.Generic;
using InductorParser;
using InductorParser.SyntaxTree;
using static NewsboatSample.Rewrite.FilterGrammar;

namespace NewsboatSample.Rewrite;

public enum RewriteOperator
{
    Equals,
    NotEquals,
    RegexMatches,
    NotRegexMatches,
    LessThan,
    GreaterThan,
    LessThanOrEquals,
    GreaterThanOrEquals,
    Between,
    Contains,
    NotContains,
}

public abstract record RewriteExpression(SourceRange? Range);
public sealed record RewriteAnd(RewriteExpression Left, RewriteExpression Right, SourceRange? Range)
    : RewriteExpression(Range);
public sealed record RewriteOr(RewriteExpression Left, RewriteExpression Right, SourceRange? Range)
    : RewriteExpression(Range);
public sealed record RewriteComparison(string Attribute, RewriteOperator Op, string ValueLiteral, SourceRange? Range)
    : RewriteExpression(Range);

public sealed record FilterParseError(string Message, int CharIndex, int Line, int Column)
{
    public override string ToString() =>
        $"line {Line + 1}, column {Column + 1}: {Message}";
}

public static class FilterParser
{
    public static RewriteExpression Parse(string input)
    {
        if (!TryParse(input, out var expression, out var error))
            throw new FormatException(error!.ToString());
        return expression!;
    }

    public static bool TryParse(string input, out RewriteExpression? expression, out FilterParseError? error)
    {
        expression = null;
        // This sample builds its own "line L, column C:" prefix from the
        // ErrorLine / ErrorCharColumn fields (see FilterParseError.ToString), so the
        // message stays position-less to avoid repeating the position.
        var options = new ParseOptions
        {
            PositionalErrorTemplate = "unexpected '{character}'.",
            EndOfInputErrorTemplate = "unexpected end of input.",
            WithErrorTemplate = "{message}",
        };
        var result = Filter.Parse(input, options);
        if (!result.Success)
        {
            error = new FilterParseError(
                result.ErrorMessage,
                result.ErrorCharIndex,
                result.ErrorLine,
                result.ErrorCharColumn);
            return false;
        }

        var filterNode = result.Tree!;
        expression = BuildExpression(filterNode.Children, 0, filterNode.Children.Count);
        error = null;
        return true;
    }

    // The flat children list under "filter" (and under each "group")
    // looks like [primary, logop, primary, logop, primary, ...] because
    // the recursive expression rule is transparent and its inner Ands
    // flatten. Right-fold collapses the chain into the right-
    // associative AST shape the upstream parser produces.
    private static RewriteExpression BuildExpression(IReadOnlyList<Symbol> children, int start, int endExclusive)
    {
        var rightmost = BuildPrimary(children[endExclusive - 1]);
        for (int index = endExclusive - 3; index >= start; index -= 2)
        {
            var left = BuildPrimary(children[index]);
            var op = children[index + 1];
            var range = JoinRanges(left.Range, rightmost.Range);
            rightmost = op.Is(AndKeyword)
                ? new RewriteAnd(left, rightmost, range)
                : new RewriteOr(left, rightmost, range);
        }
        return rightmost;
    }

    // A compound node spans from the start of its left subtree to the end
    // of its right one. When the left subtree came from a parenthesized
    // group, the span starts at the first comparison inside the group, not
    // at the "(", because the parens aren't part of either subtree.
    private static SourceRange? JoinRanges(SourceRange? left, SourceRange? right) =>
        left.HasValue && right.HasValue
            ? new SourceRange(left.Value.Start, right.Value.End)
            : null;

    private static RewriteExpression BuildPrimary(Symbol symbol)
    {
        if (symbol.Is(Group))
            return BuildExpression(symbol.Children, 0, symbol.Children.Count);
        if (symbol.Is(Comparison))
            return BuildComparison(symbol);
        throw new InvalidOperationException($"Unexpected primary node id {symbol.Id}");
    }

    private static RewriteExpression BuildComparison(Symbol comparison)
    {
        var attributeNode = comparison.Find(AttributeName)!;
        var operatorNode = comparison.Find(ComparisonOperator)!;
        var valueNode = comparison.Find(ComparisonValue)!;

        string attribute = attributeNode.SourceText;
        var op = MapOperator(operatorNode.SourceText);
        string value = ExtractValueLiteral(valueNode);

        return new RewriteComparison(attribute, op, value, comparison.SourceRange);
    }

    private static RewriteOperator MapOperator(string text) => text switch
    {
        "=~" => RewriteOperator.RegexMatches,
        "==" => RewriteOperator.Equals,
        "=" => RewriteOperator.Equals,
        "!~" => RewriteOperator.NotRegexMatches,
        "!=" => RewriteOperator.NotEquals,
        "<=" => RewriteOperator.LessThanOrEquals,
        ">=" => RewriteOperator.GreaterThanOrEquals,
        "<" => RewriteOperator.LessThan,
        ">" => RewriteOperator.GreaterThan,
        "between" => RewriteOperator.Between,
        "#" => RewriteOperator.Contains,
        "!#" => RewriteOperator.NotContains,
        _ => throw new InvalidOperationException($"Unknown operator '{text}'")
    };

    private static string ExtractValueLiteral(Symbol valueNode)
    {
        var quotedString = valueNode.Find(QuotedStringLiteral);
        if (quotedString != null)
        {
            // Body excludes the surrounding quotes. SourceText on the
            // body returns the raw inner text with escape sequences
            // still in place. UnescapeQuotedBody collapses `\X` to `X`.
            var body = quotedString.Find(QuotedStringBody);
            return UnescapeQuotedBody(body?.SourceText ?? string.Empty);
        }

        // Range or Number. SourceText covers the verbatim section of
        // the original input, so the colon in a range and the leading
        // minus on a number are present without needing per-token
        // .Preserve() in the grammar.
        return valueNode.SourceText;
    }

    private static string UnescapeQuotedBody(string body)
    {
        // Walk the body, collapsing `\X` escapes to `X` to match the
        // upstream behavior in rust/libnewsboat/src/filterparser.rs.
        var output = new System.Text.StringBuilder(body.Length);
        for (int index = 0; index < body.Length; index++)
        {
            char current = body[index];
            if (current == '\\' && index + 1 < body.Length)
            {
                output.Append(body[index + 1]);
                index++;
                continue;
            }
            output.Append(current);
        }
        return output.ToString();
    }
}
