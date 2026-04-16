using global::InductorParser;
using global::InductorParser.SyntaxTree;
using static global::InductorParser.Rules;

namespace InductorParser.Benchmarks.Json;

// JSON grammar for the shape the JsonBench harness generates: strings,
// objects, and arrays only. No numbers, booleans, nulls, or escape
// sequences — the bench never generates them and the parser-combinator
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
        // A JSON string char is either:
        //   * any rune except U+0022 ('"') and U+005C ('\') — the literal case
        //   * a backslash followed by one of "/\bfnrt or a \uXXXX unicode escape
        //
        // InductorParser has no RuneNotIn primitive yet (see
        // backlog/c000-pass-through-text-primitives); express the literal-char
        // complement as positive ranges around the two excluded code points.
        // Range endpoints must be valid scalar values but interior surrogate
        // halves are harmless — the lexer never produces them.
        var notQuoteOrBackslash =
            RuneSet.Range(0, 0x21) |           // 0..!
            RuneSet.Range(0x23, 0x5B) |         // #..[
            RuneSet.Range(0x5D, 0x10FFFF);      // ]..max
        var literalChar = RuneIn(notQuoteOrBackslash);

        var simpleEscape = RuneIn(RuneSet.Runes("\"\\/bfnrt"));
        var hexDigit = RuneIn(RuneSet.Ascii.Digits | RuneSet.Range('a', 'f') | RuneSet.Range('A', 'F'));
        var unicodeEscape = And(Char('u'), hexDigit, hexDigit, hexDigit, hexDigit);
        var escapeSequence = And(Char('\\'), Or(simpleEscape, unicodeEscape));

        var stringChar = Or(literalChar, escapeSequence);
        JsonString = And(Char('"'), ZeroOrMore(stringChar), Char('"')).As("string");

        var value = new LateBoundRule("value");

        JsonMember = And(
            JsonString,
            OptionalWhitespace(),
            Char(':'),
            OptionalWhitespace(),
            value
        ).As("member");

        JsonObject = And(
            Char('{'),
            OptionalWhitespace(),
            Optional(And(
                JsonMember,
                ZeroOrMore(And(OptionalWhitespace(), Char(','), OptionalWhitespace(), JsonMember))
            )),
            OptionalWhitespace(),
            Char('}')
        ).As("object");

        JsonArray = And(
            Char('['),
            OptionalWhitespace(),
            Optional(And(
                value,
                ZeroOrMore(And(OptionalWhitespace(), Char(','), OptionalWhitespace(), value))
            )),
            OptionalWhitespace(),
            Char(']')
        ).As("array");

        var valueBody = Or(JsonString, JsonObject, JsonArray);
        value.Bind(valueBody);

        Json = value;
        Json.Compile();
    }

    public static ParseResult Parse(string input) => Json.Parse(input, _options);
}
