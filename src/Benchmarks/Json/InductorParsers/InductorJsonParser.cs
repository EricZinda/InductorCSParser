using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using global::InductorParser;
using global::InductorParser.SyntaxTree;

namespace InductorParser.Benchmarks.Json.InductorParsers;

// The compiled JSON grammar the benchmark rows parse with, plus the
// typed-tree building the InductorParserTyped rows measure. The grammar
// itself lives in InductorJsonGrammar, whose file comment covers the
// scope and shape decisions.
public static class InductorJsonParser
{
    public static readonly Rule JsonRule;
    public static readonly Rule JsonStringRule;
    public static readonly Rule JsonArrayRule;
    public static readonly Rule JsonObjectRule;
    public static readonly Rule JsonMemberRule;

    static InductorJsonParser()
    {
        var grammar = InductorJsonGrammar.Build();
        JsonRule = grammar.RootRule;
        JsonStringRule = grammar.StringRule;
        JsonArrayRule = grammar.ArrayRule;
        JsonObjectRule = grammar.ObjectRule;
        JsonMemberRule = grammar.MemberRule;
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
