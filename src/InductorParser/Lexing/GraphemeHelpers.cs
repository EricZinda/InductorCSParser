using System;
using InductorParser.Lexing.Unicode;

namespace InductorParser.Lexing;

/// <summary>
/// Shared grapheme-cluster helpers built on <c>GraphemeSegmentation</c>,
/// so callers get the same answers the lexer does.
/// This is the grapheme-cluster layer. Its rune-layer counterpart is
/// <see cref="RuneHelpers"/>, kept separate because runes and <a href="https://www.unicode.org/reports/tr29/">UAX #29</a>
/// clusters are different units. Public so user-defined rules can ask
/// the same segmentation questions the built-in rules do. Which
/// segmenter is used is the process-wide
/// <see cref="UnicodeEnvironment.Implementation">UnicodeEnvironment.Implementation</see> setting, which
/// governs segmentation and normalization together.
/// </summary>
public static class GraphemeHelpers
{
    /// <summary>
    /// Number of user-perceived characters (grapheme clusters) in the
    /// string. A single rune, a surrogate-pair emoji, and a multi-rune ZWJ
    /// sequence each count as one. Returns 0 for the empty string.
    /// </summary>
    public static int Count(string text)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        ReadOnlySpan<char> remaining = text.AsSpan();
        int count = 0;
        while (!remaining.IsEmpty)
        {
            remaining = remaining.Slice(FirstClusterLength(remaining));
            count++;
        }
        return count;
    }

    /// <summary>
    /// Length in chars (UTF-16 code units) of the first grapheme cluster
    /// in <paramref name="text"/>. Returns 0 for an empty span. A lone
    /// surrogate reads as U+FFFD, so by itself it's a one-char cluster
    /// and a following combining mark glues to it.
    /// </summary>
    public static int FirstClusterLength(ReadOnlySpan<char> text) =>
        GraphemeSegmentation.GetLengthOfFirstExtendedGraphemeCluster(text);

    /// <summary>
    /// The largest grapheme cluster boundary of <paramref name="text"/>
    /// at or below <paramref name="position"/>. A position that lands
    /// mid-cluster is pulled back to the start of that cluster, and a
    /// position already on a boundary comes back unchanged. Positions
    /// past the end of the string are treated as <c><see cref="string.Length">text.Length</see></c>.
    /// </summary>
    /// <remarks>
    /// Cluster boundaries come from the per-string boundary cache, so
    /// repeated calls against the same string instance (a rule flooring
    /// failure offsets into its expected text, for example) allocate
    /// nothing after the first call.
    /// </remarks>
    public static int FloorToClusterStart(string text, int position)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        if (position <= 0) return 0;
        if (position > text.Length) position = text.Length;

        var index = GraphemeClusterIndex.For(text);
        int boundary = 0;
        while (boundary < position)
        {
            int clusterLength = index.LengthAt(boundary);
            if (boundary + clusterLength > position) break;
            boundary += clusterLength;
        }
        return boundary;
    }
}
