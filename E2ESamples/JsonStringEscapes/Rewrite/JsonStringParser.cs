// Typed entry point for the InductorParser JSON-string rewrite.
//
// JsonStringGrammar validates the literal and reports positioned errors.
// This file adds the value side: on a successful parse it walks the tree
// (Body -> a run of `text` / `escape` nodes) and decodes it into the actual
// string value, the way serde_json's parse_str produces a `String`.
//
// Decoding never fails here: the grammar already proved every escape is
// well-formed and every surrogate is paired, so DecodeEscape can assume it.

using System;
using System.Globalization;
using System.Text;

namespace JsonStringEscapes.Rewrite;

public sealed record JsonStringParseError(string Message, int CharIndex, int Line, int Column, string Source)
{
    // Message, the source line, and a caret under the offending column.
    public override string ToString()
    {
        string caret = new string(' ', Math.Max(0, CharIndex)) + "^";
        return $"{Message}\n    {Source}\n    {caret}";
    }
}

public static class JsonStringParser
{
    public static string Parse(string literal)
    {
        if (!TryParse(literal, out var value, out var error))
            throw new FormatException(error!.ToString());
        return value!;
    }

    public static bool TryParse(string literal, out string? value, out JsonStringParseError? error)
    {
        value = null;

        var result = JsonStringGrammar.JsonString.Parse(literal);
        if (!result.Success)
        {
            error = new JsonStringParseError(
                result.ErrorMessage,
                result.ErrorCharIndex,
                result.ErrorLine,
                result.ErrorColumn,
                literal);
            return false;
        }

        // serde_json's sixth string error, InvalidUnicodeCodePoint, fires for
        // a bare unpaired surrogate code unit in the string content. The
        // grammar can't catch it: the surrogate block 0xD800..0xDFFF isn't
        // expressible as a TokenSet (see JsonStringGrammar's header comment
        // and backlog item 0000a). So it's a post-parse check here.
        int loneSurrogate = FindUnpairedSurrogate(literal);
        if (loneSurrogate >= 0)
        {
            error = new JsonStringParseError(
                "invalid unicode code point; the string content contains an " +
                "unpaired UTF-16 surrogate code unit",
                loneSurrogate, 0, loneSurrogate, literal);
            return false;
        }

        var builder = new StringBuilder();
        var body = result.Tree!.Find(JsonStringGrammar.Body);
        if (body is not null)
        {
            foreach (var node in body.Children)
            {
                if (node.Is(JsonStringGrammar.Escape))
                    builder.Append(DecodeEscape(node.SourceText));
                else if (node.Is(JsonStringGrammar.Text))
                    builder.Append(node.SourceText);
            }
        }

        value = builder.ToString();
        error = null;
        return true;
    }

    // Decode one escape node's raw text. The grammar guarantees it's
    // well-formed, so this is a total function: a simple escape, a BMP
    // `\uXXXX`, or a `\uD8xx\uDCxx` surrogate pair.
    private static string DecodeEscape(string escape)
    {
        if (escape[1] != 'u')
        {
            return escape[1] switch
            {
                '"' => "\"",
                '\\' => "\\",
                '/' => "/",
                'b' => "\b",
                'f' => "\f",
                'n' => "\n",
                'r' => "\r",
                't' => "\t",
                _ => throw new InvalidOperationException($"grammar admitted an escape it shouldn't have: {escape}"),
            };
        }

        int first = Hex4(escape, 2);
        if (first is >= 0xD800 and <= 0xDBFF)
        {
            // A surrogate pair is one escape node: \uD8xx\uDCxx, 12 chars.
            int second = Hex4(escape, 8);
            int codePoint = (((first - 0xD800) << 10) | (second - 0xDC00)) + 0x10000;
            return char.ConvertFromUtf32(codePoint);
        }
        return ((char)first).ToString();
    }

    private static int Hex4(string text, int offset) =>
        int.Parse(text.AsSpan(offset, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    // Index of the first UTF-16 surrogate code unit that isn't part of a
    // valid high+low pair, or -1 if the string is well-formed UTF-16.
    private static int FindUnpairedSurrogate(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    i++;          // a well-formed pair, skip its low half
                    continue;
                }
                return i;         // a high surrogate with no low half after it
            }
            if (char.IsLowSurrogate(c))
                return i;         // a low surrogate with no high half before it
        }
        return -1;
    }
}

