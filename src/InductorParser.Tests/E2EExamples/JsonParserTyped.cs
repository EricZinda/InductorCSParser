using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.JsonGrammar;

namespace InductorParser.Tests;

// Consumer for JsonGrammar. Walks the flattened parse tree and produces
// a strongly-typed hierarchy (JsonValue abstract base plus six sealed
// record subtypes, one per JSON value kind). This mirrors how real
// .NET JSON libraries expose their parse output: System.Text.Json's
// JsonElement uses a tagged-union struct, Newtonsoft's JToken uses an
// abstract-base-plus-concrete-subclasses hierarchy, and the
// parser-combinator competitors (Pidgin, Sprache, Superpower, Pegasus,
// Parlot) all build a typed IJson per value during parsing.
//
// The walker is compact because JsonGrammar already did the work at
// the grammar layer: every value-carrying rule is
// FlattenType.Preserve, so the parse tree is a clean spine of named
// nodes that map one-to-one onto the JsonValue subtypes.
//
// JsonValue and its subtypes expose the same ergonomic accessors that
// JToken does: explicit conversions like (string)v, (double)v, (bool)v
// plus indexers so chained access like root["address"]["city"][0]
// compiles and dispatches at runtime. See the JsonValue record at the
// bottom of this file.
public static class JsonParserTyped
{
    public static JsonValue Project(string input)
    {
        var result = Json.Parse(input);
        if (!result.Success)
            throw new FormatException(result.ErrorMessage);

        // result.Tree is the single top-level Preserve wrapper. Master's
        // parse model flattens during parse (Flatten-typed wrappers are
        // lifted into their parent's children list as the parse runs),
        // so no post-hoc .Flatten() call is needed.
        return ConvertToValue(result.Tree!);
    }

    // Dispatch on which JsonGrammar rule produced this Symbol. Each arm
    // wraps its result in the corresponding JsonValue subtype so callers
    // get compile-time type safety.
    private static JsonValue ConvertToValue(Symbol symbol) => symbol switch
    {
        _ when symbol.Is(JsonNull)   => JsonNullValue.Instance,
        _ when symbol.Is(JsonTrue)   => new JsonBoolValue(true),
        _ when symbol.Is(JsonFalse)  => new JsonBoolValue(false),
        _ when symbol.Is(JsonNumber) => new JsonNumberValue(double.Parse(symbol.ToString(), CultureInfo.InvariantCulture)),
        _ when symbol.Is(JsonString) => new JsonStringValue(DecodeString(symbol)),
        _ when symbol.Is(JsonObject) => CreateObjectFrom(symbol),
        _ when symbol.Is(JsonArray)  => CreateArrayFrom(symbol),
        _ => throw new InvalidOperationException($"Unexpected json node id: {symbol.Id.Value}")
    };

    private static JsonObjectValue CreateObjectFrom(Symbol objectNode)
    {
        var dictionary = new Dictionary<string, JsonValue>();
        foreach (var member in objectNode.Children)
        {
            // member.Children = [keyString, value]. Colon, commas, and
            // whitespace were filtered at parse time.
            var key = DecodeString(member.Children[0]);
            var value = ConvertToValue(member.Children[1]);
            dictionary[key] = value;
        }
        return new JsonObjectValue(dictionary);
    }

    private static JsonArrayValue CreateArrayFrom(Symbol arrayNode)
    {
        var list = new List<JsonValue>();
        foreach (var valueNode in arrayNode.Children)
            list.Add(ConvertToValue(valueNode));
        return new JsonArrayValue(list);
    }

    // Extract a JsonString's body as a decoded C# string. The grammar's
    // ScanUntil is a single leaf over the raw source text, so escape
    // sequences appear in the tree as their literal characters (for
    // example "\n" as the two chars '\' and 'n'). The PEG-based decoder
    // below turns them into the real code points.
    private static string DecodeString(Symbol stringNode)
    {
        // JsonString = AllOf('"', stringBody, '"'). Quotes are
        // FlattenType.Delete, body is FlattenType.Preserve, so the only
        // surviving child is the body leaf.
        var rawString = stringNode.Children[0].ToString();
        if (rawString.IndexOf('\\') < 0) return rawString;
        return DecodeEscapes(rawString);
    }

    // Why string decoding lives here instead of in JsonGrammar.
    //
    // The main JSON grammar uses ScanUntil for the string body, which
    // is a single rule that scans the whole body in one tight loop and
    // returns one leaf Symbol over the raw source text, including
    // escape characters written literally.
    //
    // From ScanUntilRule.cs: "ToString() returns the raw source text,
    // including escape-start runes and their ends as written originally.
    // Callers who want to actually decode the escapes need to walk the
    // text themselves. Lazy decoding means a syntax highlighter or a
    // code-formatter, which WANTS the raw source preserved, doesn't
    // have to pay for it."
    //
    // So the consumer does the decoding. The side-grammar below emits
    // named pieces (LiteralChunk for runs of unescaped characters plus
    // one rule per escape kind). DecodeEscapes re-parses the raw body
    // through that grammar and concatenates each piece's decoded form.
    private static readonly Rule LiteralChunk = OneOrMore(NoneOf("\\")).As("literalChunk").Preserve();

    private static readonly Rule EscapeQuote = Literal("\\\"").As("escapeQuote").Preserve();
    private static readonly Rule EscapeBackslash = Literal("\\\\").As("escapeBackslash").Preserve();
    private static readonly Rule EscapeSlash = Literal("\\/").As("escapeSlash").Preserve();
    private static readonly Rule EscapeBackspace = Literal("\\b").As("escapeBackspace").Preserve();
    private static readonly Rule EscapeFormfeed = Literal("\\f").As("escapeFormfeed").Preserve();
    private static readonly Rule EscapeNewline = Literal("\\n").As("escapeNewline").Preserve();
    private static readonly Rule EscapeReturn = Literal("\\r").As("escapeReturn").Preserve();
    private static readonly Rule EscapeTab = Literal("\\t").As("escapeTab").Preserve();

    // Literal("\\u") is FlattenType.Delete by default, so the "\u"
    // prefix never reaches the tree. The four hex digits survive as
    // OneOf leaves, so escapeUnicode.ToString() returns just those
    // digits and int.Parse can consume them directly.
    private static readonly Rule HexDigit = OneOf(RuneSet.Ascii.HexDigits);
    private static readonly Rule EscapeUnicode = AllOf(Literal("\\u"), HexDigit, HexDigit, HexDigit, HexDigit)
        .As("escapeUnicode").Preserve();

    private static readonly Rule StringParser = AllOf(
        ZeroOrMore(FirstOf(
            LiteralChunk,
            FirstOf(EscapeQuote, EscapeBackslash, EscapeSlash,
               EscapeBackspace, EscapeFormfeed, EscapeNewline,
               EscapeReturn, EscapeTab, EscapeUnicode))),
        Eof()
    ).Compile();

    // Reparse the raw body through StringParser, project each piece to
    // its decoded string form, concatenate. The parser already did the
    // "which escape is it" classification, so DecodePiece is a dispatch
    // table.
    private static string DecodeEscapes(string rawString)
    {
        var result = StringParser.Parse(rawString);
        if (!result.Success)
            throw new InvalidOperationException(
                "String body re-parse failed: " + result.ErrorMessage);

        // StringParser root is an AllOf with default FlattenType.Flatten,
        // so its children (LiteralChunk and the Escape* rules) end up as
        // the top-level entries of result.Symbols. Tree is null here
        // because Symbols.Count != 1.
        return string.Concat(result.Symbols.Select(DecodePiece));
    }

    private static string DecodePiece(Symbol piece) => piece switch
    {
        _ when piece.Is(LiteralChunk)    => piece.ToString(),
        _ when piece.Is(EscapeQuote)     => "\"",
        _ when piece.Is(EscapeBackslash) => "\\",
        _ when piece.Is(EscapeSlash)     => "/",
        _ when piece.Is(EscapeBackspace) => "\b",
        _ when piece.Is(EscapeFormfeed)  => "\f",
        _ when piece.Is(EscapeNewline)   => "\n",
        _ when piece.Is(EscapeReturn)    => "\r",
        _ when piece.Is(EscapeTab)       => "\t",
        // piece.ToString() is the four hex digits (the "\u" prefix was
        // FlattenType.Delete). A surrogate-pair 😀 in the
        // source becomes two adjacent EscapeUnicode pieces that each
        // emit one char, which together form a valid UTF-16 surrogate
        // pair after concatenation.
        _ when piece.Is(EscapeUnicode)   => ((char)int.Parse(piece.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString(),
        _ => throw new InvalidOperationException($"Unexpected body piece id {piece.Id.Value}")
    };
}

public abstract record JsonValue
{
    // Ergonomic accessors that let callers write (string)root["name"]
    // instead of ((JsonStringValue)root["name"]).Value, and
    // root["address"]["city"] instead of the equivalent chain of casts.
    // Modeled on Newtonsoft.Json.Linq.JToken, which uses the same tricks
    // to hide its JValue/JObject/JArray wrapper types behind "looks like
    // you're reading a primitive" syntax.

    // Primitive casts. Each dispatches on the concrete subtype of the
    // JsonValue and either unwraps or throws. JSON null is accepted by
    // the nullable-target variants (string?, double?, bool?) so JSON
    // null round-trips to C# null instead of throwing.
    public static explicit operator string?(JsonValue value) => value switch
    {
        JsonStringValue s => s.Value,
        JsonNullValue    => null,
        _ => throw new InvalidCastException($"Cannot cast {value.GetType().Name} to string.")
    };

    public static explicit operator double(JsonValue value) => value switch
    {
        JsonNumberValue n => n.Value,
        _ => throw new InvalidCastException($"Cannot cast {value.GetType().Name} to double.")
    };

    public static explicit operator double?(JsonValue value) => value switch
    {
        JsonNumberValue n => n.Value,
        JsonNullValue    => null,
        _ => throw new InvalidCastException($"Cannot cast {value.GetType().Name} to double?.")
    };

    public static explicit operator bool(JsonValue value) => value switch
    {
        JsonBoolValue b => b.Value,
        _ => throw new InvalidCastException($"Cannot cast {value.GetType().Name} to bool.")
    };

    public static explicit operator bool?(JsonValue value) => value switch
    {
        JsonBoolValue b => b.Value,
        JsonNullValue  => null,
        _ => throw new InvalidCastException($"Cannot cast {value.GetType().Name} to bool?.")
    };

    // Virtual indexers on the base so chained access (root["x"]["y"][0])
    // compiles and dispatches at runtime. Subtypes that aren't
    // indexable throw; the string / int split mirrors how JObject
    // and JArray in Newtonsoft specialize the same pattern.
    public virtual JsonValue this[string key] =>
        throw new InvalidOperationException($"{GetType().Name} isn't a JSON object (can't index by string).");

    public virtual JsonValue this[int index] =>
        throw new InvalidOperationException($"{GetType().Name} isn't a JSON array (can't index by int).");
}

public sealed record JsonNullValue : JsonValue
{
    public static readonly JsonNullValue Instance = new();
    public override string ToString() => "null";
}

public sealed record JsonBoolValue(bool Value) : JsonValue;

public sealed record JsonNumberValue(double Value) : JsonValue;

public sealed record JsonStringValue(string Value) : JsonValue;

public sealed record JsonArrayValue(IReadOnlyList<JsonValue> Items) : JsonValue
{
    public override JsonValue this[int index] => Items[index];
}

public sealed record JsonObjectValue(IReadOnlyDictionary<string, JsonValue> Members) : JsonValue
{
    public override JsonValue this[string key] => Members[key];
}
