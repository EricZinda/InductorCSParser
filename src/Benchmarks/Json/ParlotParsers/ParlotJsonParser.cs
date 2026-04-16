// Forked from Parlot (https://github.com/sebastienros/parlot, BSD-3-Clause). See LICENSE-PARLOT.txt.
using Parlot.Fluent;
using System.Collections.Generic;
using static Parlot.Fluent.Parsers;

namespace InductorParser.Benchmarks.Json.ParlotParsers;

public class ParlotJsonParser
{
    public static readonly Parser<IJson> Json;

    static ParlotJsonParser()
    {
        var LBrace = Terms.Char('{');
        var RBrace = Terms.Char('}');
        var LBracket = Terms.Char('[');
        var RBracket = Terms.Char(']');
        var Colon = Terms.Char(':');
        var Comma = Terms.Char(',');

        var String = Terms.String(StringLiteralQuotes.Double);

        var jsonString =
            String
                .Then(static s => (IJson)new JsonString(s.ToString()!));

        var json = Deferred<IJson>();

        var jsonArray =
            Between(LBracket, Separated(Comma, json), RBracket)
                .Then(static els => (IJson)new JsonArray(els));

        var jsonMember =
            String.And(Colon).And(json)
                .Then(static member => new KeyValuePair<string, IJson>(member.Item1.ToString()!, member.Item3));

        var jsonObject =
            Between(LBrace, Separated(Comma, jsonMember), RBrace)
                .Then(static kvps => (IJson)new JsonObject(new Dictionary<string, IJson>(kvps)));

        Json = json.Parser = OneOf<IJson>(jsonString, jsonArray, jsonObject);
    }

    public static IJson? Parse(string input)
    {
        if (Json.TryParse(input, out var result))
        {
            return result;
        }
        return null;
    }
}
