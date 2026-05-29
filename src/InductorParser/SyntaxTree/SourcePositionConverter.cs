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
        // so this call is pure bool-array reads with no StringInfo
        // calls and no per-cluster substring allocations.
        return GraphemeClusterIndex.For(input).CountClustersUpTo(charIndex);
    }

    public static void ToLineColumn(string input, int charIndex, out int line, out int column)
    {
        Invariant.That(charIndex >= 0 && charIndex <= input.Length,
            $"charIndex {charIndex} is outside [0, {input.Length}]. Every caller passes a clamped "
            + "(SourcePosition.From) or in-range recorded failure position (ParseResult.ErrorCharIndex).");

        line = 0;
        int lineStart = 0;
        for (int i = 0; i < charIndex; i++)
        {
            char c = input[i];
            // Single-rune line terminators, tested through the shared
            // TokenSet.IsLineTerminator predicate so this stays in step with
            // TokenSet.LineTerminators (and Rules.EndOfLine(), which matches
            // that set) instead of re-listing the code points. That alignment
            // is what makes ErrorLine/ErrorColumn agree with grammars that
            // consume any of these as a newline. CR is excluded here and
            // handled below so CRLF counts as one break.
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
        column = charIndex - lineStart;
    }
}
