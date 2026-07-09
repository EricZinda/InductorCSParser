// Forked from Parlot (https://github.com/sebastienros/parlot, BSD-3-Clause). See LICENSE-PARLOT.txt.
using InductorParser.Benchmarks.Json;
using Pidgin;
using System.Collections.Generic;
using System.Linq;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

namespace InductorParser.Benchmarks.Json.PidginParsers;

public static class PidginJsonParser
{
    private static readonly Parser<char, char> LBrace = Char('{');
    private static readonly Parser<char, char> RBrace = Char('}');
    private static readonly Parser<char, char> LBracket = Char('[');
    private static readonly Parser<char, char> RBracket = Char(']');
    private static readonly Parser<char, char> Quote = Char('"');
    private static readonly Parser<char, char> Colon = Char(':');
    private static readonly Parser<char, char> ColonWhitespace =
        Colon.Between(SkipWhitespaces);
    private static readonly Parser<char, char> Comma = Char(',');

    // Decoded escape sequence: match "\" then one of the JSON escape
    // suffixes, return the decoded char. \uXXXX is a unicode escape.
    // The simple escapes map their suffix to the corresponding control
    // or literal char.
    private static readonly Parser<char, char> HexDigit =
        Token(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));

    private static readonly Parser<char, char> UnicodeEscape =
        Char('u').Then(
            Map((h1, h2, h3, h4) => (char)int.Parse(
                    new string(new[] { h1, h2, h3, h4 }),
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture),
                HexDigit, HexDigit, HexDigit, HexDigit));

    private static readonly Parser<char, char> EscapeSuffix =
        Token(c => c == '"' || c == '\\' || c == '/' || c == 'b' || c == 'f' || c == 'n' || c == 'r' || c == 't')
            .Select(c => c switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                'b' => '\b',
                'f' => '\f',
                _ => c,
            });

    private static readonly Parser<char, char> EscapedChar =
        Char('\\').Then(EscapeSuffix.Or(UnicodeEscape));

    // Bulk match of literal string-body chars. AtLeastOnceString so the outer
    // LiteralRun.Or(EscapeAsString).Many() can't loop forever on an empty
    // match. 3% of chars are escapes, so most iterations of the outer loop
    // consume a LiteralRun of ~30 chars in a single tight Token-predicate
    // loop instead of dispatching the outer .Or per character.
    private static readonly Parser<char, string> LiteralRun =
        Token(c => c != '"' && c != '\\').AtLeastOnceString();

    private static readonly Parser<char, string> EscapeAsString =
        EscapedChar.Select(c => c.ToString());

    private static readonly Parser<char, string> String =
        LiteralRun.Or(EscapeAsString)
            .Many()
            .Select(chunks => string.Concat(chunks))
            .Between(Quote);
    private static readonly Parser<char, IJson> JsonString =
        String.Select<IJson>(s => new JsonString(s));

    private static readonly Parser<char, IJson> Json =
        JsonString.Or(Rec(() => JsonArray)).Or(Rec(() => JsonObject));

    private static readonly Parser<char, IJson> JsonArray =
        Json.Between(SkipWhitespaces)
            .Separated(Comma)
            .Between(LBracket, RBracket)
            .Select<IJson>(els => new JsonArray(els.ToArray()));

    private static readonly Parser<char, KeyValuePair<string, IJson>> JsonMember =
        String
            .Before(ColonWhitespace)
            .Then(Json, (name, val) => new KeyValuePair<string, IJson>(name, val));

    private static readonly Parser<char, IJson> JsonObject =
        JsonMember.Between(SkipWhitespaces)
            .Separated(Comma)
            .Between(LBrace, RBrace)
            .Select<IJson>(kvps => new JsonObject(new Dictionary<string, IJson>(kvps)));

    public static Result<char, IJson> Parse(string input) => Json.Parse(input);
}
