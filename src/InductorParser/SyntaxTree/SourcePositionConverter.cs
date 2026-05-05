using InductorParser.Lexing;

namespace InductorParser.SyntaxTree;

// Char-index to (token / line / column) conversions, shared
// between ParseResult.Error* properties and Symbol.SourceRange.
internal static class SourcePositionConverter
{
    public static int ToTokenIndex(string input, int charIndex)
    {
        if (input == null || charIndex <= 0) return 0;
        // Goes through GraphemeClusterIndex.For so this share the
        // same cache the Lexer populated during the parse. After a
        // full parse the cache is typically already walked end-to-end,
        // so this call is pure bool-array reads with no StringInfo
        // calls and no per-cluster substring allocations.
        return GraphemeClusterIndex.For(input).CountClustersUpTo(charIndex);
    }

    public static void ToLineColumn(string input, int charIndex, out int line, out int column)
    {
        int limit = charIndex;
        if (limit > input.Length) limit = input.Length;
        if (limit < 0) limit = 0;

        line = 0;
        int lineStart = 0;
        for (int i = 0; i < limit; i++)
        {
            char c = input[i];
            if (c == '\n')
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
        column = limit - lineStart;
    }
}
