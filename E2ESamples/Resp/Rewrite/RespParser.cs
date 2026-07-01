// A RESP (REdis Serialization Protocol, version 2) parser built on
// InductorParser, vs ../Original/resp_nom_example.rs:
//
//                       Original (nom)            Rewrite (this file)
//   Bulk string         length_data(...)          LengthDataRule
//   Array               length_count(...)         LengthCountRule
//   Failure position    nom's Err + position      char index + line + column
//   AST                 Resp enum                 RespValue record hierarchy
//
// The grammar is thin glue, the way a nom RESP parser is thin glue over its
// combinators: five value types dispatched by their leading byte. The two
// context-sensitive types, bulk string and array, delegate to the custom rules
// in LengthPrefixedRules.cs (the ports of nom's length_data / length_count).
// The other three (simple string, error, integer) are plain built-ins.
//
// RESP is the framing protocol Redis uses (https://redis.io/docs/reference/
// protocol-spec/). A value is one of:
//   +OK\r\n              simple string
//   -ERR bad command\r\n error
//   :1000\r\n            integer
//   $5\r\nhello\r\n      bulk string (5 = byte length of the payload)
//   *2\r\n:1\r\n:2\r\n   array (2 = element count, elements follow)
// with $-1\r\n and *-1\r\n as the null bulk string and null array.
//
// What this is and isn't. It parses the RESP *grammar* (the type bytes, the
// CRLF framing, the length-prefixed strings, nested arrays, the null forms)
// from a decoded .NET string, and it's a faithful demonstration of the
// length_data / length_count combinators. It is NOT a drop-in Redis wire codec.
// Real RESP is a byte protocol: a bulk string's length is a byte count and the
// payload is binary-safe (arbitrary bytes, not necessarily valid UTF-8). This
// parser works on a string and counts lengths in UTF-16 code units, so it's
// byte-for-byte correct only for ASCII / text payloads (which covers commands
// and simple replies). A non-ASCII bulk payload's byte length and its code-unit
// length disagree, and arbitrary binary can't be handed in as a string at all.
// See the length divergence note in LengthPrefixedRules.cs.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using InductorParser;
using InductorParser.SyntaxTree;
using RespSample.Rewrite;
using static InductorParser.Rules;

namespace RespSample.Rewrite;

public abstract record RespValue;
public sealed record RespSimpleString(string Value) : RespValue;
public sealed record RespError(string Message) : RespValue;
public sealed record RespInteger(long Value) : RespValue;
// Value == null is the RESP null bulk string ($-1\r\n), distinct from the
// empty bulk string ($0\r\n\r\n), whose Value is "".
public sealed record RespBulkString(string? Value) : RespValue;
// Items == null is the RESP null array (*-1\r\n), distinct from the empty
// array (*0\r\n), whose Items is an empty list.
public sealed record RespArray(IReadOnlyList<RespValue>? Items) : RespValue;

public sealed record RespParseError(string Message, int CharIndex, int Line, int Column)
{
    public override string ToString() =>
        $"line {Line + 1}, column {Column + 1}: {Message}";
}

public static class RespParser
{
    public static readonly Rule Resp;

    static RespParser()
    {
        // Fresh instances per call: a rule is single-use in a compiled graph,
        // so each spot that needs CRLF / a digit run / a line body gets its own.
        // CRLF and the prefix bytes are Delete by default (Literal and Token
        // both default to Delete), so only the values reach the tree.
        Rule Crlf() => Literal("\r\n");
        Rule Digits() => OneOrMore(OneOf(TokenSet.Ascii.Digits));
        Rule LineBody() => ScanUntil(TokenSet.LineTerminators);

        // value is recursive: an array's elements are themselves RESP values,
        // so the rule has to mention itself. The forward reference is bound
        // once the alternatives (including array, which uses value) are built.
        var value = new LateBoundRule("value");

        var simpleString = And(Token('+'), LineBody(), Crlf())
            .As("simpleString");

        var error = And(Token('-'), LineBody(), Crlf())
            .As("error");

        // The WithError sits on the value, not the whole And, so it fires only
        // once the ':' has matched and the digits are missing. A different type
        // byte fails at the Token(':') with no message, leaving the Or's
        // "expected a RESP value" to win.
        var integer = And(
                Token(':'),
                Integer().As("intValue").WithError("expected an integer after ':'"),
                Crlf())
            .As("integer");

        var nullBulk = And(Token('$'), Literal("-1"), Crlf())
            .As("nullBulk");

        // $<length>\r\n<payload>\r\n. The custom rule reads <length>, the CRLF,
        // then exactly <length> payload chars. The trailing CRLF after the
        // payload is the outer And's job.
        var bulkString = And(
                Token('$'),
                new LengthDataRule(Digits(), Crlf()).As("bulkData")
                    .WithError("bulk string payload doesn't match its declared length"),
                Crlf().WithError("expected CRLF after the bulk string payload"))
            .As("bulkString");

        var nullArray = And(Token('*'), Literal("-1"), Crlf())
            .As("nullArray");

        // *<count>\r\n<element>... The custom rule reads <count>, the CRLF,
        // then runs `value` exactly <count> times. Elements are self-delimiting
        // (each starts with its own type byte), so there's no inter-element
        // separator.
        var array = And(
                Token('*'),
                new LengthCountRule(Digits(), Crlf(), value))
            .As("array");

        // Null forms first so $-1 / *-1 take the null branch cleanly. (The
        // length-bearing branches would fail on '-' anyway, since a digit run
        // doesn't match a minus sign, but ordering it this way reads clearer.)
        value.Bind(
            Or(simpleString, error, integer, nullBulk, bulkString, nullArray, array)
                .WithError("expected a RESP value starting with one of + - : $ *"));

        Resp = And(value, Eof().WithError("unexpected trailing data after the RESP value"))
            .As("resp");

        // null normalization: RESP payloads are opaque bytes and bulk-string
        // lengths are counted against the raw input, so Unicode normalization
        // (which can change string length) doesn't run. Disabling it also
        // keeps every reported position an exact offset into the user's input.
        Resp.Compile(null);
    }

    public static RespValue Parse(string input)
    {
        if (!TryParse(input, out var value, out var error))
            throw new FormatException(error!.ToString());
        return value!;
    }

    public static bool TryParse(string input, out RespValue? value, out RespParseError? error)
    {
        // RespParseError surfaces position through its Line / Column fields, so
        // the message text stays position-less.
        var options = new ParseOptions
        {
            WithErrorTemplate = "{message}",
            PositionalErrorTemplate = "unexpected '{character}'.",
            EndOfInputErrorTemplate = "unexpected end of input.",
        };
        var result = Resp.Parse(input, options);
        if (!result.Success)
        {
            value = null;
            error = new RespParseError(
                result.ErrorMessage, result.ErrorCharIndex, result.ErrorLine, result.ErrorCharColumn);
            return false;
        }

        // Tree is the "resp" Symbol. Its one child is the matched value.
        value = ProjectValue(result.Tree!.Children[0]);
        error = null;
        return true;
    }

    private static RespValue ProjectValue(Symbol node)
    {
        if (node.Is("simpleString")) return new RespSimpleString(LeafText(node));
        if (node.Is("error")) return new RespError(LeafText(node));
        if (node.Is("integer"))
            return new RespInteger(long.Parse(LeafText(node), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
        if (node.Is("bulkString")) return new RespBulkString(LeafText(node));
        if (node.Is("nullBulk")) return new RespBulkString(null);
        if (node.Is("array")) return new RespArray(node.Children.Select(ProjectValue).ToList());
        if (node.Is("nullArray")) return new RespArray(null);
        throw new InvalidOperationException($"unexpected RESP node: {node}");
    }

    // The simple string, error, integer, and bulk string all wrap exactly one
    // value-bearing child (the line body, the integer's digits, or the bulk
    // payload). An empty body still produces a zero-length child, so the
    // Count > 0 check only covers a defensive "no child" case and is never the
    // path a real parse takes.
    private static string LeafText(Symbol node) =>
        node.Children.Count > 0 ? node.Children[0].ToString() : string.Empty;
}
