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
// AllOf(Optional(AnyWhitespace()), value, Optional(AnyWhitespace()), Eof)). The harness
// feeds clean input that starts and ends at the value, competitors
// likewise skip a trailing-Eof wrapper, and adding one would spend
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
        var simpleEscapeEnd = OneOf(RuneSet.Runes("\"\\/bfnrt"));
        var hexDigit = OneOf(RuneSet.Ascii.HexDigits);
        var unicodeEscapeEnd = AllOf(Grapheme('u'), hexDigit, hexDigit, hexDigit, hexDigit);
        var escapeEnd = FirstOf(simpleEscapeEnd, unicodeEscapeEnd).Flatten(FlattenType.Delete);
        var stringBody = ScanUntil(stopAt: RuneSet.Runes("\""), escapeStart: new Rune('\\'), escapeEnd: escapeEnd);
        JsonStringRule = AllOf(Grapheme('"'), stringBody, Grapheme('"')).As("string").Preserve();

        var value = new LateBoundRule("value");

        JsonMemberRule = AllOf(
            JsonStringRule,
            Optional(AnyWhitespace()),
            Grapheme(':'),
            Optional(AnyWhitespace()),
            value
        ).As("member");

        JsonObjectRule = AllOf(
            Grapheme('{'),
            Optional(AnyWhitespace()),
            Optional(AllOf(
                JsonMemberRule,
                ZeroOrMore(AllOf(Optional(AnyWhitespace()), Grapheme(','), Optional(AnyWhitespace()), JsonMemberRule))
            )),
            Optional(AnyWhitespace()),
            Grapheme('}')
        ).As("object").Preserve();

        JsonArrayRule = AllOf(
            Grapheme('['),
            Optional(AnyWhitespace()),
            Optional(AllOf(
                value,
                ZeroOrMore(AllOf(Optional(AnyWhitespace()), Grapheme(','), Optional(AnyWhitespace()), value))
            )),
            Optional(AnyWhitespace()),
            Grapheme(']')
        ).As("array").Preserve();

        var valueBody = FirstOf(JsonStringRule, JsonObjectRule, JsonArrayRule);
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
        InputUnit = InputUnit.Rune,
        MaxDepth = 0,
    };

    public static ParseResult Parse(string input) => JsonRule.Parse(input, _options);

    // Grapheme-lexer variant, exposed as its own JsonBench benchmark
    // (XxxJson_InductorParserGrapheme) alongside the Rune-lexer standard
    // path (XxxJson_InductorParserRune). Running both makes the cost of
    // UAX #29 grapheme-cluster assembly visible in the headline numbers,
    // which matters because every competitor in this bench works on
    // char (effectively rune-equivalent) and doesn't pay that cost.
    private static readonly ParseOptions _graphemeOptions = new()
    {
        InputUnit = InputUnit.Grapheme,
        MaxDepth = 0,
    };

    public static ParseResult ParseGrapheme(string input) => JsonRule.Parse(input, _graphemeOptions);

    // Round-trip variant used by the spot-check. Parse-time Delete filtering
    // would drop the JSON delimiters (Grapheme('{'), '}', ',', ':', '"') from
    // the tree, so Tree.ToString() on a normally-parsed value returns just
    // the concatenated non-delimiter content rather than the original
    // input. PreserveAllSymbols keeps every grammar node in the tree
    // for verification purposes. It isn't used by the benchmark runs because
    // it wouldn't be used by a real caller either, just here for verification.
    private static readonly ParseOptions _roundTripOptions = new()
    {
        InputUnit = InputUnit.Rune,
        MaxDepth = 0,
        PreserveAllSymbols = true,
    };

    public static ParseResult ParseForRoundTrip(string input) => JsonRule.Parse(input, _roundTripOptions);

    // Typed variant: parses with the Grapheme lexer and then walks the
    // Symbol tree to build a concrete IJson tree (JsonString / JsonArray /
    // JsonObject). This is the row to compare directly against the
    // IJson-building competitors (Pidgin, Sprache, Superpower, Pegasus,
    // Parlot) because it produces the same output shape they do.
    public static IJson ParseTyped(string input)
    {
        var result = JsonRule.Parse(input, _graphemeOptions);
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
            // JsonMemberRule is Flatten-default, so its two kept children
            // (key JsonStringRule match + value) are lifted into
            // JsonObjectRule's children. Read pairwise: even indices are
            // keys, odd indices are values.
            var members = new Dictionary<string, IJson>(symbol.Children.Count / 2);
            for (int i = 0; i < symbol.Children.Count; i += 2)
            {
                string key = DecodeStringBody(symbol.Children[i]);
                IJson value = BuildTyped(symbol.Children[i + 1]);
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
