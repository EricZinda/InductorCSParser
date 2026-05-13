// Original newsboat filter-language parser. Hand-written recursive
// descent in idiomatic C#. Mirrors the upstream behavior: same AST,
// same 12 operators, same value forms (quoted string / number / range),
// same ASCII-space-only whitespace rule, and the same quirk that
// `and` / `or` must be followed by a space or `(` so `x=1andy=0` is
// rejected but `x=1and(y=0)` and `x=1and y=0` are accepted.
//
// The upstream parser ships in two flavors: a Coco/R-generated C++
// parser (filter/filter.atg + Parser.cpp + Scanner.cpp + FilterParser.cpp)
// and a hand-written Rust nom port (rust/libnewsboat/src/filterparser.rs).
// This C# port matches the Rust port's AST and error shape, since that
// version is the cleaner reference (the C++ uses parser-generator
// scaffolding). License: MIT (see ../README.md for attribution).
//
// The InductorParser rewrite under ../Rewrite/ keeps the same public
// API and AST shape so behavioral parity tests can compare them
// directly.

using System;
using System.Text;

namespace NewsboatSample.Original;

public enum OriginalOperator
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

public abstract record OriginalExpression;
public sealed record OriginalAnd(OriginalExpression Left, OriginalExpression Right) : OriginalExpression;
public sealed record OriginalOr(OriginalExpression Left, OriginalExpression Right) : OriginalExpression;
public sealed record OriginalComparison(string Attribute, OriginalOperator Op, string ValueLiteral) : OriginalExpression;

public sealed record OriginalParseError(string Message, int CharIndex)
{
    public override string ToString() => $"position {CharIndex}: {Message}";
}

public static class OriginalFilterParser
{
    private const string ExpectedOperators =
        "expected one of: =~, ==, =, !~, !=, <=, >=, <, >, between, #, !#";
    private const string ExpectedValue =
        "expected one of: quoted string, range, number";

    public static OriginalExpression Parse(string input)
    {
        if (!TryParse(input, out var expression, out var error))
            throw new FormatException(error!.ToString());
        return expression!;
    }

    public static bool TryParse(string input, out OriginalExpression? expression, out OriginalParseError? error)
    {
        expression = null;
        error = null;
        if (input == null)
        {
            error = new OriginalParseError("Input cannot be null.", 0);
            return false;
        }

        var cursor = new Cursor(input);
        try
        {
            cursor.SkipSpaces();
            expression = ParseExpressionOrPrimary(cursor);
            cursor.SkipSpaces();
            if (!cursor.AtEnd)
            {
                error = new OriginalParseError(
                    $"trailing characters: {cursor.Tail}",
                    cursor.Position);
                expression = null;
                return false;
            }
            return true;
        }
        catch (FilterParseException failure)
        {
            error = new OriginalParseError(failure.Message, failure.Position);
            return false;
        }
    }

    private sealed class FilterParseException : Exception
    {
        public int Position { get; }
        public FilterParseException(int position, string message) : base(message)
        {
            Position = position;
        }
    }

    private sealed class Cursor
    {
        public readonly string Source;
        public int Position;

        public Cursor(string input)
        {
            Source = input;
            Position = 0;
        }

        public bool AtEnd => Position >= Source.Length;
        public char Peek => Source[Position];
        public string Tail => Source.Substring(Position);
        public string Slice(int start, int endExclusive) => Source.Substring(start, endExclusive - start);

        public bool StartsWith(string text)
        {
            if (Position + text.Length > Source.Length) return false;
            for (int i = 0; i < text.Length; i++)
                if (Source[Position + i] != text[i]) return false;
            return true;
        }

        public void Advance(int count = 1) => Position += count;

        public void SkipSpaces()
        {
            while (!AtEnd && Source[Position] == ' ')
                Position++;
        }

        public bool TryConsume(string text)
        {
            if (!StartsWith(text)) return false;
            Position += text.Length;
            return true;
        }
    }

    private static bool IsAttributeChar(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
        (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.';

    private static OriginalExpression ParseExpressionOrPrimary(Cursor cursor)
    {
        var left = ParsePrimary(cursor);
        var afterLeft = cursor.Position;
        cursor.SkipSpaces();

        var op = TryReadLogOp(cursor);
        if (op == null)
        {
            cursor.Position = afterLeft;
            return left;
        }

        cursor.SkipSpaces();
        var right = ParseExpressionOrPrimary(cursor);
        return op == "and"
            ? new OriginalAnd(left, right)
            : new OriginalOr(left, right);
    }

    private static OriginalExpression ParsePrimary(Cursor cursor)
    {
        if (!cursor.AtEnd && cursor.Peek == '(')
        {
            cursor.Advance();
            cursor.SkipSpaces();
            var inner = ParseExpressionOrPrimary(cursor);
            cursor.SkipSpaces();
            if (cursor.AtEnd || cursor.Peek != ')')
                throw new FilterParseException(cursor.Position, "expected ')'");
            cursor.Advance();
            return inner;
        }
        return ParseComparison(cursor);
    }

    private static string? TryReadLogOp(Cursor cursor)
    {
        int saved = cursor.Position;
        string? op = null;
        if (cursor.TryConsume("and")) op = "and";
        else if (cursor.TryConsume("or")) op = "or";
        if (op == null) return null;

        if (cursor.AtEnd) { cursor.Position = saved; return null; }
        char next = cursor.Peek;
        if (next == ' ' || next == '(') return op;

        cursor.Position = saved;
        return null;
    }

    private static OriginalExpression ParseComparison(Cursor cursor)
    {
        int attributeStart = cursor.Position;
        while (!cursor.AtEnd && IsAttributeChar(cursor.Peek))
            cursor.Advance();
        if (cursor.Position == attributeStart)
            throw new FilterParseException(cursor.Position, "expected attribute name");
        string attribute = cursor.Slice(attributeStart, cursor.Position);

        cursor.SkipSpaces();
        var op = ReadOperator(cursor);
        cursor.SkipSpaces();
        string value = ReadValue(cursor);

        return new OriginalComparison(attribute, op, value);
    }

    private static OriginalOperator ReadOperator(Cursor cursor)
    {
        if (cursor.AtEnd)
            throw new FilterParseException(cursor.Position, ExpectedOperators);

        if (cursor.TryConsume("=~")) return OriginalOperator.RegexMatches;
        if (cursor.TryConsume("==")) return OriginalOperator.Equals;
        if (cursor.TryConsume("!~")) return OriginalOperator.NotRegexMatches;
        if (cursor.TryConsume("!=")) return OriginalOperator.NotEquals;
        if (cursor.TryConsume("<=")) return OriginalOperator.LessThanOrEquals;
        if (cursor.TryConsume(">=")) return OriginalOperator.GreaterThanOrEquals;
        if (cursor.TryConsume("between")) return OriginalOperator.Between;
        if (cursor.TryConsume("!#")) return OriginalOperator.NotContains;
        if (cursor.TryConsume("=")) return OriginalOperator.Equals;
        if (cursor.TryConsume("<")) return OriginalOperator.LessThan;
        if (cursor.TryConsume(">")) return OriginalOperator.GreaterThan;
        if (cursor.TryConsume("#")) return OriginalOperator.Contains;

        throw new FilterParseException(cursor.Position, ExpectedOperators);
    }

    private static string ReadValue(Cursor cursor)
    {
        if (cursor.AtEnd)
            throw new FilterParseException(cursor.Position, ExpectedValue);

        if (cursor.Peek == '"')
            return ReadQuotedString(cursor);

        int start = cursor.Position;
        if (TryReadNumber(cursor))
        {
            int afterFirstNumber = cursor.Position;
            if (!cursor.AtEnd && cursor.Peek == ':')
            {
                cursor.Advance();
                int rangeRightStart = cursor.Position;
                if (!TryReadNumber(cursor))
                    throw new FilterParseException(rangeRightStart, ExpectedValue);
                return cursor.Slice(start, cursor.Position);
            }
            return cursor.Slice(start, afterFirstNumber);
        }
        throw new FilterParseException(cursor.Position, ExpectedValue);
    }

    private static bool TryReadNumber(Cursor cursor)
    {
        int saved = cursor.Position;
        if (!cursor.AtEnd && cursor.Peek == '-')
            cursor.Advance();
        int digitsStart = cursor.Position;
        while (!cursor.AtEnd && cursor.Peek >= '0' && cursor.Peek <= '9')
            cursor.Advance();
        if (cursor.Position == digitsStart)
        {
            cursor.Position = saved;
            return false;
        }
        return true;
    }

    private static string ReadQuotedString(Cursor cursor)
    {
        int openPosition = cursor.Position;
        cursor.Advance();
        var content = new StringBuilder();
        while (true)
        {
            if (cursor.AtEnd)
                throw new FilterParseException(openPosition, ExpectedValue);
            char c = cursor.Peek;
            if (c == '"')
            {
                cursor.Advance();
                return content.ToString();
            }
            if (c == '\\')
            {
                cursor.Advance();
                if (cursor.AtEnd)
                    throw new FilterParseException(openPosition, ExpectedValue);
                content.Append(cursor.Peek);
                cursor.Advance();
                continue;
            }
            content.Append(c);
            cursor.Advance();
        }
    }
}
