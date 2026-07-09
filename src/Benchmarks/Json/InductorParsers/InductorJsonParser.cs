using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using global::InductorParser;
using global::InductorParser.SyntaxTree;
using static global::InductorParser.Rules;

namespace InductorParser.Benchmarks.Json.InductorParsers;

// JSON grammar for the shape the JsonBench harness generates: strings,
// objects, and arrays only. No numbers, booleans, or nulls, because the
// harness doesn't generate them (see JsonBench.BuildObject) and because
// every competitor in the bench (Pidgin, Sprache, Superpower, Pegasus,
// Parlot) also stops there. Including them here would make the comparison
// unfair on inputs competitors can't handle, and would also measure a
// code path that the competitors don't exercise.
//
// String bodies handle the full set of JSON escapes (\", \\, \/, \b, \f,
// \n, \r, \t, and \uXXXX) to match what the competitors do, so the
// string-parsing hot path is apples-to-apples.
//
// Entry point is the bare value rule (no surrounding
// And(Optional(AnyWhitespace()), value, Optional(AnyWhitespace()), Eof)). The harness
// feeds clean input that starts and ends at the value, competitors
// likewise skip a trailing Eof rule, and adding one would spend
// time on every parse that the bench isn't trying to measure.
public static class InductorJsonParser
{
    public static readonly Rule JsonRule;
    public static readonly Rule JsonStringRule;
    public static readonly Rule JsonArrayRule;
    public static readonly Rule JsonObjectRule;
    public static readonly Rule JsonMemberRule;

    static InductorJsonParser()
    {
        var simpleEscapeEnd = OneOf(TokenSet.Runes("\"\\/bfnrt"));
        var hexDigit = OneOf(TokenSet.Ascii.HexDigits);
        var unicodeEscapeEnd = And(Token('u'), hexDigit, hexDigit, hexDigit, hexDigit);
        var escapeEnd = Or(simpleEscapeEnd, unicodeEscapeEnd).Flatten(FlattenType.Delete);
        var stringBody = ScanUntil(stopAt: TokenSet.Runes("\""), escapeStart: new Rune('\\'), escapeEnd: escapeEnd);
        JsonStringRule = And(Token('"'), stringBody, Token('"')).As("string");

        var value = new LateBoundRule("value");

        JsonMemberRule = And(
            JsonStringRule,
            Optional(AnyWhitespace()),
            Token(':'),
            Optional(AnyWhitespace()),
            value
        ).As("member");

        JsonObjectRule = And(
            Token('{'),
            Optional(AnyWhitespace()),
            Optional(And(
                JsonMemberRule,
                ZeroOrMore(And(Optional(AnyWhitespace()), Token(','), Optional(AnyWhitespace()), JsonMemberRule))
            )),
            Optional(AnyWhitespace()),
            Token('}')
        ).As("object");

        JsonArrayRule = And(
            Token('['),
            Optional(AnyWhitespace()),
            Optional(And(
                value,
                ZeroOrMore(And(Optional(AnyWhitespace()), Token(','), Optional(AnyWhitespace()), value))
            )),
            Optional(AnyWhitespace()),
            Token(']')
        ).As("array");

        var valueBody = Or(JsonStringRule, JsonObjectRule, JsonArrayRule);
        value.Bind(valueBody);

        JsonRule = value;
        JsonRule.Compile();
    }

    // MaxDepth=0 disables the recursion-depth budget. The Deep benchmark
    // input is 256 levels of nested objects, so the default MaxDepth=1000 trips.
    // Disabling the budget matches what System.Text.Json already does in
    // the bench via MaxDepth=1024 on its Deep-specific settings object.
    private static readonly ParseOptions _options = new()
    {
        MaxDepth = 0,
    };

    public static ParseResult Parse(string input) => JsonRule.Parse(input, _options);

    // Token-lexer is the only lexer now. ParseGrapheme is kept as
    // an alias of Parse so existing benchmark rows continue to call it.
    public static ParseResult ParseToken(string input) => JsonRule.Parse(input, _options);

    // Round-trip variant used by the spot-check. Parse-time Delete filtering
    // would drop the JSON delimiters (Token('{'), '}', ',', ':', '"') from
    // the tree, so Tree.ToString() on a normally-parsed value returns just
    // the concatenated non-delimiter content rather than the original
    // input. PreserveAllSymbols keeps every grammar node in the tree
    // for verification purposes. It isn't used by the benchmark runs because
    // it wouldn't be used by a real caller either, just here for verification.
    private static readonly ParseOptions _roundTripOptions = new()
    {
        MaxDepth = 0,
        PreserveAllSymbols = true,
    };

    public static ParseResult ParseForRoundTrip(string input) => JsonRule.Parse(input, _roundTripOptions);

    // Typed variant: parses and walks the Symbol tree to build a
    // concrete IJson tree (JsonString / JsonArray / JsonObject). This
    // is the row to compare directly against the IJson-building
    // competitors (Pidgin, Sprache, Superpower, Pegasus, Parlot)
    // because it produces the same output shape they do.
    public static IJson ParseTyped(string input)
    {
        var result = JsonRule.Parse(input, _options);
        if (!result.Success)
            throw new FormatException(result.ErrorMessage);
        return BuildTyped(result.Tree!);
    }

    private static IJson BuildTyped(Symbol symbol)
    {
        if (symbol.Is(JsonStringRule))
            return new JsonString(DecodeStringBody(symbol));
        if (symbol.Is(JsonArrayRule))
        {
            var items = new List<IJson>(symbol.Children.Count);
            foreach (var child in symbol.Children)
                items.Add(BuildTyped(child));
            return new JsonArray(items);
        }
        if (symbol.Is(JsonObjectRule))
        {
            // JsonMemberRule is named with .As("member"), so it's Preserve
            // by default and each member shows up as its own child of the
            // object. The member's two surviving children are the key
            // string and the value (the colon and surrounding whitespace
            // are filtered out as Delete).
            var members = new Dictionary<string, IJson>(symbol.Children.Count);
            foreach (var memberSymbol in symbol.Children)
            {
                string key = DecodeStringBody(memberSymbol.Children[0]);
                IJson value = BuildTyped(memberSymbol.Children[1]);
                members[key] = value;
            }
            return new JsonObject(members);
        }
        throw new InvalidOperationException($"Unexpected symbol id {symbol.Id.Value}");
    }

    // JsonStringRule's single surviving child is the raw body leaf produced
    // by ScanUntil. ScanUntil keeps escape sequences literal ("\\n" is
    // two characters), so decode here to match what competitors' typed
    // output looks like.
    private static string DecodeStringBody(Symbol stringNode)
    {
        string raw = stringNode.Children[0].ToString();
        if (raw.IndexOf('\\') < 0) return raw;

        var builder = new StringBuilder(raw.Length);
        int i = 0;
        while (i < raw.Length)
        {
            char character = raw[i];
            if (character != '\\')
            {
                builder.Append(character);
                i++;
                continue;
            }
            char next = raw[i + 1];
            switch (next)
            {
                case '"':  builder.Append('"');  i += 2; break;
                case '\\': builder.Append('\\'); i += 2; break;
                case '/':  builder.Append('/');  i += 2; break;
                case 'b':  builder.Append('\b'); i += 2; break;
                case 'f':  builder.Append('\f'); i += 2; break;
                case 'n':  builder.Append('\n'); i += 2; break;
                case 'r':  builder.Append('\r'); i += 2; break;
                case 't':  builder.Append('\t'); i += 2; break;
                case 'u':
                    int codeUnit = int.Parse(
                        raw.AsSpan(i + 2, 4),
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture);
                    builder.Append((char)codeUnit);
                    i += 6;
                    break;
                default:
                    throw new InvalidOperationException($"Unknown escape \\{next}");
            }
        }
        return builder.ToString();
    }
}
