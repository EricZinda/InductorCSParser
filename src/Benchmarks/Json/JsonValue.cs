// Forked from Parlot (https://github.com/sebastienros/parlot, BSD-3-Clause). See LICENSE-PARLOT.txt.
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InductorParser.Benchmarks.Json;

public interface IJson
{
}

public class JsonArray : IJson
{
    public IReadOnlyList<IJson> Elements { get; }
    public JsonArray(IReadOnlyList<IJson> elements)
    {
        Elements = elements;
    }
    public override string ToString()
        => $"[{string.Join(",", Elements.Select(e => e.ToString()))}]";
}

public class JsonObject : IJson
{
    public IDictionary<string, IJson> Members { get; }
    public JsonObject(IDictionary<string, IJson> members)
    {
        Members = members;
    }
    public override string ToString()
        => $"{{{string.Join(",", Members.Select(kvp => $"\"{JsonString.Escape(kvp.Key)}\":{kvp.Value}"))}}}";
}

public class JsonString : IJson
{
    public string Value { get; }
    public JsonString(string value)
    {
        Value = value;
    }

    public override string ToString()
        => $"\"{Escape(Value)}\"";

    // Re-emit a decoded string as JSON string content. Must be the inverse
    // of whatever decoding the grammars do, otherwise round-trip verification
    // in the spot-check fails. Scope: the escapes the input generator can
    // emit (quote, backslash, and the five C-style control escapes). No \/
    // and no \uXXXX. The constraint is preserved here even though it was
    // originally added because Newtonsoft canonicalized those forms; the
    // input generator hasn't been re-evaluated since Newtonsoft was
    // removed, and tightening it now would change what the bench measures.
    public static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
