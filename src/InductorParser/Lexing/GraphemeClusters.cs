using System.Globalization;

namespace InductorParser.Lexing;

/// <summary>
/// Shared grapheme-cluster helpers built on <see cref="StringInfo"/>
/// (UAX #29 on .NET 5+).
/// </summary>
public static class GraphemeClusters
{
    /// <summary>
    /// Number of user-perceived characters (grapheme clusters) in the
    /// string. A single rune, a surrogate-pair emoji, and a multi-rune ZWJ
    /// sequence each count as one. Returns 0 for the empty string.
    /// </summary>
    public static int Count(string text)
    {
        if (text.Length == 0) return 0;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        int count = 0;
        while (enumerator.MoveNext()) count++;
        return count;
    }
}
