using System.Text;
using global::InductorParser;
using global::InductorParser.SyntaxTree;
using static global::InductorParser.Rules;

namespace InductorParser.Benchmarks.Json;

// JSON grammar for the shape the JsonBench harness generates: strings,
// objects, and arrays only. No numbers, booleans, nulls, or escape
// sequences. The bench never generates them and the parser-combinator
// competitors (Pidgin/Sprache/Superpower) don't handle them either. The
// string rule accepts "any char except U+0022" so it does the same
// per-char work the competitors do.
public static class InductorJsonParser
{
    public static readonly Rule Json;

    public static readonly Rule JsonString;
    public static readonly Rule JsonArray;
    public static readonly Rule JsonObject;
    public static readonly Rule JsonMember;

    // MaxDepth=0 disables the recursion-depth budget. The Deep benchmark
    // input is 256 levels of nested objects, and each JSON level consumes
    // several rule frames (value → Or → jsonObject → And → Optional → And
    // → jsonMember → And → value), so the default MaxDepth=1000 trips
    // well before we finish. Disabling the budget matches what Newtonsoft
    // and System.Text.Json already do in the bench via MaxDepth=1024 on
    // their Deep-specific settings objects.
    private static readonly ParseOptions _options = new()
    {
        InputUnit = InputUnit.Rune,
        MaxDepth = 0,
    };

    static InductorJsonParser()
    {
        var simpleEscape = RuneIn(RuneSet.Runes("\"\\/bfnrt"));
        var hexDigit = RuneIn(RuneSet.Ascii.Digits | RuneSet.Range('a', 'f') | RuneSet.Range('A', 'F'));
        var unicodeEscape = And(Token('u'), hexDigit, hexDigit, hexDigit, hexDigit);
        // FlattenType.Delete so StringCharsRule's per-escape TryParse
        // call doesn't force Rule.TryParse to allocate a throwaway
        // List<Symbol>.
        // StringChars discards escapeEnd's Symbol anyway (it emits a
        // single leaf covering the whole string body), so the tree
        // shape is unchanged.
        var escapeEnd = Or(simpleEscape, unicodeEscape).Flatten(FlattenType.Delete);

        // StringChars collapses the per-rune `ZeroOrMore(Or(body,
        // escape))` hot loop into one rule that scans the whole string
        // body in place. The stopper set is just the closing quote:
        // the scan runs forward until it sees a ", and everything in
        // between gets consumed as body (or dispatched to `escapeEnd`
        // when a \ shows up). One leaf Symbol for the whole run, one
        // escape dispatch per actual escape, no per-rune Symbol or
        // transaction work for the body runes that dominate typical
        // JSON payloads.
        var stringBody = StringChars(RuneSet.Runes("\""), new Rune('\\'), escapeEnd);
        JsonString = And(Token('"'), stringBody, Token('"')).As("string");

        var value = new LateBoundRule("value");

        JsonMember = And(
            JsonString,
            OptionalWhitespace(),
            Token(':'),
            OptionalWhitespace(),
            value
        ).As("member");

        JsonObject = And(
            Token('{'),
            OptionalWhitespace(),
            Optional(And(
                JsonMember,
                ZeroOrMore(And(OptionalWhitespace(), Token(','), OptionalWhitespace(), JsonMember))
            )),
            OptionalWhitespace(),
            Token('}')
        ).As("object");

        JsonArray = And(
            Token('['),
            OptionalWhitespace(),
            Optional(And(
                value,
                ZeroOrMore(And(OptionalWhitespace(), Token(','), OptionalWhitespace(), value))
            )),
            OptionalWhitespace(),
            Token(']')
        ).As("array");

        var valueBody = Or(JsonString, JsonObject, JsonArray);
        value.Bind(valueBody);

        Json = value;
        Json.Compile();
    }

    public static ParseResult Parse(string input) => Json.Parse(input, _options);

    // Round-trip variant used by the spot-check. Parse-time Delete filtering
    // would drop the JSON delimiters (Token('{'), '}', ',', ':', '"') from
    // the tree, so Tree.ToString() on a normally-parsed value returns just
    // the concatenated non-delimiter content rather than the original
    // input. PreserveFlattenWrappers keeps every grammar node in the tree
    // for verification purposes. It is not used by the benchmark runs.
    private static readonly ParseOptions _roundTripOptions = new()
    {
        InputUnit = InputUnit.Rune,
        MaxDepth = 0,
        PreserveFlattenWrappers = true,
    };

    public static ParseResult ParseForRoundTrip(string input) => Json.Parse(input, _roundTripOptions);
}
