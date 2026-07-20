using System;

namespace InductorParser.Lexing;

/// <summary>
/// Shared grapheme-cluster helpers built on <c>GraphemeSegmentation</c>,
/// so callers get the same answers the lexer does.
/// This is the grapheme-cluster layer. Its rune-layer counterpart is
/// <c>RuneHelpers</c>, kept separate because runes and UAX #29
/// clusters are different units. Public so user-defined rules can ask
/// the same segmentation questions the built-in rules do. The
/// <see cref="Segmenter"/> property selects which segmenter is used,
/// process-wide.
/// </summary>
public static class GraphemeHelpers
{
    /// <summary>
    /// Which segmenter this process uses. Defaults to
    /// <see cref="GraphemeSegmenter.Automatic"/>:
    /// the bundled segmenter on Unity, the runtime's StringInfo
    /// everywhere else. Set it once at startup, before building grammars or
    /// parsing. The first segmentation query (constructing a Token rule,
    /// compiling, parsing, or calling any method on this class) freezes
    /// the choice for the life of the process, and setting it after that
    /// throws <see cref="InvalidOperationException"/>. Frozen because
    /// cluster boundaries are cached per input string and reused across
    /// parsing and position mapping, and switching segmenters
    /// mid-process would mix boundaries from two implementations.
    /// Reading this property never freezes anything and returns the
    /// requested value, which may still be Automatic. For the segmenter
    /// actually in use, read <see cref="ActiveSegmenter"/>.
    /// </summary>
    public static GraphemeSegmenter Segmenter
    {
        get => GraphemeSegmentation.RequestedSegmenter;
        set => GraphemeSegmentation.RequestedSegmenter = value;
    }

    /// <summary>
    /// The segmenter being used. It's one of
    /// <see cref="GraphemeSegmenter.Runtime"/> or
    /// <see cref="GraphemeSegmenter.Bundled"/>, never Automatic.
    /// Reading it resolves and freezes the choice the same way the first
    /// segmentation query does, so the answer can never be invalidated
    /// by a later change.
    /// </summary>
    public static GraphemeSegmenter ActiveSegmenter =>
        GraphemeSegmentation.ActiveSegmenter;

    /// <summary>
    /// Number of user-perceived characters (grapheme clusters) in the
    /// string. A single rune, a surrogate-pair emoji, and a multi-rune ZWJ
    /// sequence each count as one. Returns 0 for the empty string.
    /// </summary>
    public static int Count(string text)
    {
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
    /// past the end of the string are treated as <c>text.Length</c>.
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
