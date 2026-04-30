using System.Text;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// A JSON grammar built so a downstream consumer (JsonParserTyped)
// can walk the flattened parse tree and produce a strongly-typed
// JsonValue hierarchy (JsonStringValue / JsonNumberValue /
// JsonObjectValue / JsonArrayValue / JsonBoolValue / JsonNullValue).
//
// The point is to show how grammar-author flatten choices shape the
// consumer: every rule we want to dispatch on in the parser gets
// .As("name").Preserve() so it survives flattening as a
// named, identifiable node. Rules that are structural noise (whitespace,
// quotes, commas, colons, braces, brackets) keep their default
// FlattenType.Delete and never appear in the tree.
//
// The grammar covers all six JSON value types plus the standard string
// escape sequences (\" \\ \/ \b \f \n \r \t and \uXXXX). It skips only
// the fiddlier spec corners (leading-zero checks on numbers, surrogate-
// pair validation on \u escapes). The benchmark
// InductorJsonParser in src/Benchmarks/Json is the perf-tuned variant;
// this one is optimized for readability of the consumer story.
public static class JsonGrammar
{
    public static readonly Rule Json;
    public static readonly Rule JsonString;
    public static readonly Rule JsonScanUntil;
    public static readonly Rule JsonNumber;
    public static readonly Rule JsonObject;
    public static readonly Rule JsonArray;
    public static readonly Rule JsonMember;
    public static readonly Rule JsonTrue;
    public static readonly Rule JsonFalse;
    public static readonly Rule JsonNull;

    static JsonGrammar()
    {
        var simpleEscapeEnd = OneOf(RuneSet.Runes("\"\\/bfnrt"));
        var hexDigit = OneOf(RuneSet.Ascii.HexDigits);
        var unicodeEscapeEnd = AllOf(Grapheme('u'), hexDigit, hexDigit, hexDigit, hexDigit);
        // .Delete() on the escapeEnd FirstOf triggers the ScanUntil
        // "don't allocate child Symbols for the escape-end match" fast
        // path. The ScanUntil primitive always produces a single leaf
        // over the raw body text, so the escape-end sub-rules' output
        // is discarded either way. Marking them Delete skips the
        // allocation.
        JsonScanUntil = ScanUntil(
                stopAt: RuneSet.Runes("\""),
                escapeStart: new Rune('\\'),
                escapeEnd: FirstOf(simpleEscapeEnd, unicodeEscapeEnd).Delete())
            .As("stringBody");
        JsonString = AllOf(Grapheme('"'), JsonScanUntil, Grapheme('"'))
            .As("string").Preserve();

        var digits = OneOrMore(OneOf(RuneSet.Ascii.Digits));
        var decimalPoint = Grapheme('.').Preserve(); // default FlattenType is Delete, but we want this in final string
        var sign = Grapheme('-').Preserve(); // default FlattenType is Delete, but we want this in final string
        var fraction = AllOf(decimalPoint, digits);
        var exponent = AllOf(OneOf("eE"), Optional(OneOf("+-")), digits);
        JsonNumber = AllOf(
            Optional(sign),
            digits,
            Optional(fraction),
            Optional(exponent)
        ).As("number").Preserve();

        // Literal is FlattenType.Delete by default, 
        // but we want to keep these in the final string
        JsonTrue = Literal("true").As("true").Preserve();
        JsonFalse = Literal("false").As("false").Preserve();
        JsonNull = Literal("null").As("null").Preserve();

        var value = new LateBoundRule("value");

        JsonMember = AllOf(
            JsonString,
            Optional(Whitespace()),
            Grapheme(':'),
            Optional(Whitespace()),
            value
        ).As("member").Preserve();

        JsonObject = AllOf(
            Grapheme('{'),
            Optional(Whitespace()),
            Optional(AllOf(
                JsonMember,
                ZeroOrMore(AllOf(Optional(Whitespace()), Grapheme(','), Optional(Whitespace()), JsonMember))
            )),
            Optional(Whitespace()),
            Grapheme('}')
        ).As("object").Preserve();

        JsonArray = AllOf(
            Grapheme('['),
            Optional(Whitespace()),
            Optional(AllOf(
                value,
                ZeroOrMore(AllOf(Optional(Whitespace()), Grapheme(','), Optional(Whitespace()), value))
            )),
            Optional(Whitespace()),
            Grapheme(']')
        ).As("array").Preserve();

        value.Bind(FirstOf(JsonString, JsonNumber, JsonObject, JsonArray, JsonTrue, JsonFalse, JsonNull));

        Json = AllOf(Optional(Whitespace()), value, Optional(Whitespace()), Eof());
        Json.Compile();
    }
}
