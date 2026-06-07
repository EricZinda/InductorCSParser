// Newsboat filter parser using InductorParser, vs ../Original/FilterParser.cs:
//
//                     Original (hand-written)         Rewrite (this file)
//   Failure position  char index                      char index + line/column
//   Error message     attribute / operator / value    same, named via WithError
//   AST shape         OriginalAnd/Or/Comparison       RewriteAnd/Or/Comparison
//   Source map        none                            SourceRange on each comparison
//
// The rewrite carries the original source range on every comparison,
// so a downstream tool (a Newsboat config editor, a syntax highlighter,
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

public abstract record RewriteExpression;
public sealed record RewriteAnd(RewriteExpression Left, RewriteExpression Right) : RewriteExpression;
public sealed record RewriteOr(RewriteExpression Left, RewriteExpression Right) : RewriteExpression;
public sealed record RewriteComparison(string Attribute, RewriteOperator Op, string ValueLiteral, SourceRange? Range)
    : RewriteExpression;

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
        var result = Filter.Parse(input);
        if (!result.Success)
        {
            error = new FilterParseError(
                result.ErrorMessage,
                result.ErrorCharIndex,
                result.ErrorLine,
                result.ErrorColumn);
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
            rightmost = op.Is(AndKeyword)
                ? new RewriteAnd(left, rightmost)
                : new RewriteOr(left, rightmost);
        }
        return rightmost;
    }

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
