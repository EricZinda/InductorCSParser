// Forked from Parlot (https://github.com/sebastienros/parlot, BSD-3-Clause). See LICENSE-PARLOT.txt.
using InductorParser.Benchmarks.Json;
using Superpower;
using System.Collections.Generic;
using System.Linq;

namespace InductorParser.Benchmarks.Json.SuperpowerParsers;

public static class SuperpowerJsonParser
{
    private static TextParser<T> Between<T, U, V>(this Superpower.TextParser<T> p, Superpower.TextParser<U> before, Superpower.TextParser<V> after)
        => before.IgnoreThen(p).Then(x => after.Value(x));

    private static readonly TextParser<char> LBrace = Superpower.Parsers.Character.EqualTo('{');
    private static readonly TextParser<char> RBrace = Superpower.Parsers.Character.EqualTo('}');
    private static readonly TextParser<char> LBracket = Superpower.Parsers.Character.EqualTo('[');
    private static readonly TextParser<char> RBracket = Superpower.Parsers.Character.EqualTo(']');
    private static readonly TextParser<char> Quote = Superpower.Parsers.Character.EqualTo('"');
    private static readonly TextParser<char> Colon = Superpower.Parsers.Character.EqualTo(':');
    private static readonly TextParser<char> ColonWhitespace =
        Colon.Between(Superpower.Parsers.Character.WhiteSpace.Many(), Superpower.Parsers.Character.WhiteSpace.Many());
    private static readonly TextParser<char> Comma = Superpower.Parsers.Character.EqualTo(',');

    // Decoded escape sequence: match "\" then one of the JSON escape
    // suffixes, return the decoded char. Mirrors what Pidgin and Sprache do.
    private static readonly TextParser<char> HexDigit =
        Superpower.Parsers.Character.Matching(
            c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'),
            "hex digit");

    private static readonly TextParser<char> UnicodeEscape =
        from u in Superpower.Parsers.Character.EqualTo('u')
        from h1 in HexDigit
        from h2 in HexDigit
        from h3 in HexDigit
        from h4 in HexDigit
        select (char)int.Parse(
            new string(new[] { h1, h2, h3, h4 }),
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture);

    private static readonly TextParser<char> SimpleEscape =
        Superpower.Parsers.Character.Matching(c => "\"\\/bfnrt".IndexOf(c) >= 0, "escape char")
            .Select(c => c switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                'b' => '\b',
                'f' => '\f',
                _ => c,
            });

    private static readonly TextParser<char> EscapedChar =
        from bs in Superpower.Parsers.Character.EqualTo('\\')
        from c in SimpleEscape.Or(UnicodeEscape)
        select c;

    private static readonly TextParser<char> StringChar =
        EscapedChar.Or(Superpower.Parsers.Character.Matching(
            c => c != '"' && c != '\\',
            "char except quote or backslash"));

    private static readonly TextParser<string> String =
        StringChar
            .Many()
            .Between(Quote, Quote)
            .Select(cs => new string(cs));
    private static readonly TextParser<IJson> JsonString =
        String.Select(s => (IJson)new JsonString(s));

    private static readonly TextParser<IJson> Json =
        JsonString.Or(Superpower.Parse.Ref(() => JsonArray)).Or(Superpower.Parse.Ref(() => JsonObject));

    private static readonly TextParser<IJson> JsonArray =
        Json.Between(Superpower.Parsers.Character.WhiteSpace.Many(), Superpower.Parsers.Character.WhiteSpace.Many())
            .ManyDelimitedBy(Comma)
            .Between(LBracket, RBracket)
            .Select(els => (IJson)new JsonArray(els.ToArray()));

    private static readonly TextParser<KeyValuePair<string, IJson>> JsonMember =
        from name in String.SelectMany(_ => ColonWhitespace, (name, ws) => name)
        from val in Json
        select new KeyValuePair<string, IJson>(name, val);

    private static readonly TextParser<IJson> JsonObject =
        JsonMember.Between(Superpower.Parsers.Character.WhiteSpace.Many(), Superpower.Parsers.Character.WhiteSpace.Many())
            .ManyDelimitedBy(Comma)
            .Between(LBrace, RBrace)
            .Select(kvps => (IJson)new JsonObject(new Dictionary<string, IJson>(kvps)));

    public static IJson Parse(string input) => Json.Parse(input);
}
