using System.Text;
using global::InductorParser;
using global::InductorParser.SyntaxTree;
using static global::InductorParser.Rules;

namespace InductorParser.Benchmarks.Json;

// JSON grammar for the shape the JsonBench harness generates: strings,
// objects, and arrays only. No numbers, booleans, or nulls, because the
// harness doesn't generate them (see JsonBench.BuildObject). String
// bodies handle the full set of JSON escapes (\", \\, \/, \b, \f, \n,
// \r, \t, and \uXXXX) to match what every competitor in the bench does
// (Pidgin, Sprache, Superpower, Pegasus, Parlot), so the string-parsing
// hot path is apples-to-apples.
public static class InductorJsonParser
{
    public static readonly Rule Json;
    public static readonly Rule JsonString;
    public static readonly Rule JsonArray;
    public static readonly Rule JsonObject;
    public static readonly Rule JsonMember;

    static InductorJsonParser()
    {
        var simpleEscape = RuneIn(RuneSet.Runes("\"\\/bfnrt"));
        var hexDigit = RuneIn(RuneSet.Ascii.Digits | RuneSet.Range('a', 'f') | RuneSet.Range('A', 'F'));
        var unicodeEscape = And(Token('u'), hexDigit, hexDigit, hexDigit, hexDigit);
        var escapeEnd = Or(simpleEscape, unicodeEscape).Flatten(FlattenType.Delete);
        var stringBody = StringChars(stopAt: RuneSet.Runes("\""), escapeStart: new Rune('\\'), escapeEnd: escapeEnd);
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

    // MaxDepth=0 disables the recursion-depth budget. The Deep benchmark
    // input is 256 levels of nested objects, so the default MaxDepth=1000 trips. 
    // Disabling the budget matches what Newtonsoft
    // and System.Text.Json already do in the bench via MaxDepth=1024 on
    // their Deep-specific settings objects.
    private static readonly ParseOptions _options = new()
    {
        InputUnit = InputUnit.Rune,
        MaxDepth = 0,
    };

    public static ParseResult Parse(string input) => Json.Parse(input, _options);

    // Round-trip variant used by the spot-check. Parse-time Delete filtering
    // would drop the JSON delimiters (Token('{'), '}', ',', ':', '"') from
    // the tree, so Tree.ToString() on a normally-parsed value returns just
    // the concatenated non-delimiter content rather than the original
    // input. PreserveFlattenWrappers keeps every grammar node in the tree
    // for verification purposes. It is not used by the benchmark runs because
    // it wouldn't be used by a real caller either, just here for verification.
    private static readonly ParseOptions _roundTripOptions = new()
    {
        InputUnit = InputUnit.Rune,
        MaxDepth = 0,
        PreserveFlattenWrappers = true,
    };

    public static ParseResult ParseForRoundTrip(string input) => Json.Parse(input, _roundTripOptions);
}
