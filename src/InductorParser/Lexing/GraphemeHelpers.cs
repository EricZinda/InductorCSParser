using System;
using InductorParser.Lexing.Unicode;

namespace InductorParser.Lexing;

/// <summary>
/// Shared grapheme-cluster helpers built on the same segmenter the lexer uses.
/// This is the grapheme-cluster layer. Its rune-layer counterpart is
/// <see cref="RuneHelpers"/>, kept separate because runes and <a href="https://www.unicode.org/reports/tr29/">UAX #29</a>
/// clusters are different units. Which
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
    /// Moves a char index that may have landed inside a grapheme cluster back to the start of
    /// that cluster. A rule needs this when it has an offset that didn't come from reading whole
    /// tokens, such as how far a literal compare got before it stopped or an index from a string
    /// search, and has to record a failure position or build a Symbol on a token boundary, since
    /// a position inside a cluster points into the middle of a character the user sees as one.
    /// If <paramref name="position"/> is inside a grapheme cluster, returns the index where that
    /// cluster starts. Otherwise, returns the position unchanged. Indices count UTF-16 code units
    /// (C# <c>char</c> values). Positions below zero return zero. Positions beyond the end return
    /// <c><see cref="string.Length">text.Length</see></c>.
    /// </summary>
    /// <remarks>
    /// Cluster boundaries come from the per-string boundary cache, so
    /// repeated calls against the same string instance (a rule flooring
    /// failure offsets into its expected text, for example) allocate
    /// nothing after the first call.
    /// </remarks>
    /// <example>
    /// In <c>"e\u0301x"</c>, the letter <c>e</c> at index 0 and the combining accent at index 1
    /// form one grapheme cluster. Position 1 therefore returns 0. Position 2 returns 2 because
    /// it's already at the start of the next cluster, <c>x</c>.
    /// </example>
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
