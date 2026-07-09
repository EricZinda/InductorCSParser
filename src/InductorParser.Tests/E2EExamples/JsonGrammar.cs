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
// .As("name") so it survives flattening as a
// named, identifiable node. Rules that are structural noise (whitespace,
// quotes, commas, colons, braces, brackets) keep their default
// FlattenType.Delete and never appear in the tree.
//
// The grammar covers all six JSON value types plus the standard string
// escape sequences (\" \\ \/ \b \f \n \r \t and \uXXXX). It skips only
// the fiddlier spec corners (leading-zero checks on numbers, surrogate-
// pair validation on \u escapes). The benchmark
// InductorJsonParser in src/Benchmarks/Json is the perf-tuned variant.
// This one is optimized for readability of the consumer story.
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
        var simpleEscapeEnd = OneOf(TokenSet.Runes("\"\\/bfnrt"));
        var hexDigit = OneOf(TokenSet.Ascii.HexDigits);
        var unicodeEscapeEnd = And(Token('u'), hexDigit, hexDigit, hexDigit, hexDigit);
        // .Delete() on the escapeEnd Or triggers the ScanUntil
        // "don't allocate child Symbols for the escape-end match" fast
        // path. The ScanUntil primitive always produces a single leaf
        // over the raw body text, so the escape-end sub-rules' output
        // is discarded either way. Marking them Delete skips the
        // allocation.
        JsonScanUntil = ScanUntil(
                stopAt: TokenSet.Runes("\""),
                escapeStart: new Rune('\\'),
                escapeEnd: Or(simpleEscapeEnd, unicodeEscapeEnd).Delete())
            .As("stringBody");
        JsonString = And(Token('"'), JsonScanUntil, Token('"'))
            .As("string");

        var digits = OneOrMore(OneOf(TokenSet.Ascii.Digits));
        var decimalPoint = Token('.').Preserve(); // default FlattenType is Delete, but we want this in final string
        var sign = Token('-').Preserve(); // default FlattenType is Delete, but we want this in final string
        var fraction = And(decimalPoint, digits);
        var exponent = And(OneOf("eE"), Optional(OneOf("+-")), digits);
        JsonNumber = And(
            Optional(sign),
            digits,
            Optional(fraction),
            Optional(exponent)
        ).As("number");

        // Literal is FlattenType.Delete by default, 
        // but we want to keep these in the final string
        JsonTrue = Literal("true").As("true");
        JsonFalse = Literal("false").As("false");
        JsonNull = Literal("null").As("null");

        var value = new LateBoundRule("value");

        JsonMember = And(
            JsonString,
            Optional(AnyWhitespace()),
            Token(':'),
            Optional(AnyWhitespace()),
            value
        ).As("member");

        JsonObject = And(
            Token('{'),
            Optional(AnyWhitespace()),
            Optional(And(
                JsonMember,
                ZeroOrMore(And(
                    Optional(AnyWhitespace()),
                    Token(','),
                    Optional(AnyWhitespace()),
                    JsonMember
                ))
            )),
            Optional(AnyWhitespace()),
            Token('}')
        ).As("object");

        JsonArray = And(
            Token('['),
            Optional(AnyWhitespace()),
            Optional(And(
                value,
                ZeroOrMore(And(
                    Optional(AnyWhitespace()),
                    Token(','),
                    Optional(AnyWhitespace()),
                    value
                ))
            )),
            Optional(AnyWhitespace()),
            Token(']')
        ).As("array");

        value.Bind(Or(JsonString, JsonNumber, JsonObject, JsonArray, JsonTrue, JsonFalse, JsonNull));

        Json = And(
            Optional(AnyWhitespace()),
            value,
            Optional(AnyWhitespace()),
            Eof()
        );
        Json.Compile();
    }
}
