using InductorParser.Lexing;

namespace InductorParser.SyntaxTree;

// Char-index to (token / line / column) conversions, shared
// between ParseResult.Error* properties and Symbol.SourceRange.
internal static class SourcePositionConverter
{
    public static int ToTokenIndex(string input, int charIndex)
    {
        if (input == null || charIndex <= 0) return 0;
        // Goes through GraphemeClusterIndex.For so this shares the
        // same cache the Lexer populated during the parse. After a
        // full parse the cache is typically already walked end-to-end,
        // so this call is pure bool-array reads with no segmentation
        // calls and no per-cluster allocations.
        return GraphemeClusterIndex.For(input).CountClustersUpTo(charIndex);
    }

    public static void ToLineColumn(string input, int charIndex, out int line, out int column)
    {
        Invariant.That(charIndex >= 0 && charIndex <= input.Length,
            $"charIndex {charIndex} is outside [0, {input.Length}]. Callers are expected to pass a position already in range, so reaching this means one didn't.");

        line = ScanLine(input, charIndex, out int lineStart);
        column = charIndex - lineStart;
    }

    // Within-line column measured in tokens (grapheme clusters) rather than
    // chars: the human-facing counterpart to the char column ToLineColumn
    // returns. An emoji, a flag, or a base-plus-combining-mark earlier on the
    // line counts as one, not as its several UTF-16 code units. lineStart is
    // the position right after a line terminator, which is always a cluster
    // boundary, so the cluster count up to charIndex minus the cluster count up
    // to lineStart is the exact within-line cluster count.
    public static int ToTokenColumn(string input, int charIndex)
    {
        Invariant.That(charIndex >= 0 && charIndex <= input.Length,
            $"charIndex {charIndex} is outside [0, {input.Length}]. Callers are expected to pass a position already in range, so reaching this means one didn't.");

        if (charIndex <= 0) return 0;
        ScanLine(input, charIndex, out int lineStart);
        var index = GraphemeClusterIndex.For(input);
        return index.CountClustersUpTo(charIndex) - index.CountClustersUpTo(lineStart);
    }

    // Scan to the line containing charIndex. Returns the zero-based line number
    // and sets lineStart to the char index where that line begins. Line breaks
    // are tested through the shared TokenSet.IsLineTerminator predicate so this
    // stays in step with TokenSet.LineTerminators (and Rules.EndOfLine(), which
    // matches that set) instead of re-listing the code points. That alignment is
    // what makes ErrorLine/ErrorCharColumn agree with grammars that consume any of
    // these as a newline. CR is excluded from the first branch and handled in
    // the second so CRLF counts as one break.
    private static int ScanLine(string input, int charIndex, out int lineStart)
    {
        int line = 0;
        lineStart = 0;
        for (int i = 0; i < charIndex; i++)
        {
            char c = input[i];
            if (c != '\r' && TokenSet.IsLineTerminator(c))
            {
                line++;
                lineStart = i + 1;
            }
            else if (c == '\r' && (i + 1 >= input.Length || input[i + 1] != '\n'))
            {
                line++;
                lineStart = i + 1;
            }
        }
        return line;
    }
}
