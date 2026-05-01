namespace InductorParser.SyntaxTree;

// A point in a source string, expressed in four units. CharIndex is
// the canonical one (UTF-16 code units, what string.Substring uses);
// the others are derived from it by walking the input from offset 0.
//
// Line and Column follow the Language Server Protocol convention used
// by editor diagnostics: zero-based, with line breaks at "\n", "\r\n"
// (one break, not two), and lone "\r". Column is in chars, same unit
// as CharIndex.
public readonly struct SourcePosition
{
    // UTF-16 code units, what string.Substring / Span<char>.Slice / LSP use.
    public int CharIndex { get; }

    // User-perceived characters, using the same StringInfo text-element
    // segmentation the lexer uses. On modern .NET that follows UAX #29
    // extended grapheme clusters. An emoji ZWJ sequence or a
    // letter-plus-combining-mark may count as one grapheme, so this index
    // is smaller than or equal to CharIndex on any input containing
    // multi-char graphemes.
    public int GraphemeIndex { get; }

    // Zero-based line number, LSP convention.
    public int Line { get; }

    // Zero-based column within the line, in chars (UTF-16 code units),
    // same unit as CharIndex.
    public int Column { get; }

    internal SourcePosition(int charIndex, int graphemeIndex, int line, int column)
    {
        CharIndex = charIndex;
        GraphemeIndex = graphemeIndex;
        Line = line;
        Column = column;
    }

    // Compute all four position units from a character index into the
    // input string. Walks the string from 0 to charIndex once, counting
    // graphemes and line breaks along the way. O(charIndex).
    // Out-of-range charIndex values are clamped to [0, input.Length].
    public static SourcePosition From(string input, int charIndex)
    {
        if (input == null) input = string.Empty;
        int limit = charIndex;
        if (limit < 0) limit = 0;
        if (limit > input.Length) limit = input.Length;

        int graphemeIndex = SourcePositionConverter.ToGraphemeIndex(input, limit);
        SourcePositionConverter.ToLineColumn(input, limit, out int line, out int column);
        return new SourcePosition(limit, graphemeIndex, line, column);
    }
}
