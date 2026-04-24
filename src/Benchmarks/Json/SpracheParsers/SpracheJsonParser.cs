// Forked from Parlot (https://github.com/sebastienros/parlot, BSD-3-Clause). See LICENSE-PARLOT.txt.
using InductorParser.Benchmarks.Json;
using Sprache;
using System.Collections.Generic;
using System.Linq;
using static Sprache.Parse;

namespace InductorParser.Benchmarks.Json.SpracheParsers;

public static class SpracheJsonParser
{
    private static readonly Sprache.Parser<char> LBrace = Char('{');
    private static readonly Sprache.Parser<char> RBrace = Char('}');
    private static readonly Sprache.Parser<char> LBracket = Char('[');
    private static readonly Sprache.Parser<char> RBracket = Char(']');
    private static readonly Sprache.Parser<char> Quote = Char('"');
    private static readonly Sprache.Parser<char> Colon = Char(':');
    private static readonly Sprache.Parser<char> ColonWhitespace =
        Colon.Contained(WhiteSpace.Many(), WhiteSpace.Many());
    private static readonly Sprache.Parser<char> Comma = Char(',');

    // Decoded escape sequence: match "\" then one of the JSON escape
    // suffixes, return the decoded char. Mirrors what Pidgin and
    // Superpower do.
    private static readonly Sprache.Parser<char> HexDigit =
        Char(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'), "hex digit");

    private static readonly Sprache.Parser<char> UnicodeEscape =
        from u in Char('u')
        from h1 in HexDigit
        from h2 in HexDigit
        from h3 in HexDigit
        from h4 in HexDigit
        select (char)int.Parse(
            new string(new[] { h1, h2, h3, h4 }),
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture);

    private static readonly Sprache.Parser<char> SimpleEscape =
        Char(c => "\"\\/bfnrt".IndexOf(c) >= 0, "escape char")
            .Select(c => c switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                'b' => '\b',
                'f' => '\f',
                _ => c,
            });

    private static readonly Sprache.Parser<char> EscapedChar =
        from bs in Char('\\')
        from c in SimpleEscape.Or(UnicodeEscape)
        select c;

    // Per-character ordered choice is the fastest idiomatic Sprache pattern
    // here. The bulk-run pattern (`Char(pred).AtLeastOnce().Text()` inside an
    // outer `.Or().Many()`) measured slower across all shapes because
    // Sprache's `.AtLeastOnce().Text()` materializes a char[] plus a string
    // per run, and the outer `.Many()` pays state-snapshot overhead per
    // chunk. The extra allocations outweigh the per-char `.Or()` dispatch
    // savings. See README "Bulk-run pattern" discussion.
    private static readonly Sprache.Parser<char> StringChar =
        EscapedChar.Or(Char(c => c != '"' && c != '\\', "char except quote or backslash"));

    private static readonly Sprache.Parser<string> String =
        StringChar
            .Many()
            .Contained(Quote, Quote)
            .Select(cs => new string(cs.ToArray()));
    private static readonly Sprache.Parser<IJson> JsonString =
        String.Select(s => (IJson)new JsonString(s));

    private static readonly Sprache.Parser<IJson> Json =
        JsonString.Or(Ref(() => JsonArray)).Or(Ref(() => JsonObject));

    private static readonly Sprache.Parser<IJson> JsonArray =
        Json.Contained(WhiteSpace.Many(), WhiteSpace.Many())
            .DelimitedBy(Comma)
            .Contained(LBracket, RBracket)
            .Select(els => (IJson)new JsonArray(els.ToArray()));

    private static readonly Sprache.Parser<KeyValuePair<string, IJson>> JsonMember =
        from name in String.SelectMany(_ => ColonWhitespace, (name, ws) => name)
        from val in Json
        select new KeyValuePair<string, IJson>(name, val);

    private static readonly Sprache.Parser<IJson> JsonObject =
        JsonMember.Contained(WhiteSpace.Many(), WhiteSpace.Many())
            .DelimitedBy(Comma)
            .Contained(LBrace, RBrace)
            .Select(kvps => (IJson)new JsonObject(new Dictionary<string, IJson>(kvps)));

    public static IResult<IJson> Parse(string input) => Json(new Input(input));
}
