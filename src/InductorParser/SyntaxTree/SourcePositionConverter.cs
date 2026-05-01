using System.Globalization;

namespace InductorParser.SyntaxTree;

// Char-index to (token / line / column) conversions, shared
// between ParseResult.Error* properties and Symbol.SourceRange.
internal static class SourcePositionConverter
{
    public static int ToTokenIndex(string input, int charIndex)
    {
        int limit = charIndex;
        if (limit > input.Length) limit = input.Length;
        if (limit <= 0) return 0;

        int count = 0;
        int i = 0;
        while (i < limit)
        {
            string element = StringInfo.GetNextTextElement(input, i);
            int step = element.Length;
            if (step <= 0) step = 1;
            i += step;
            count++;
        }
        return count;
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
