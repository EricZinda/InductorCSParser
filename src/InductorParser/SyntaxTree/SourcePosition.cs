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

    // Unicode scalar values (code points or .Net Runes). A surrogate pair counts as
    // one rune, so this index is smaller than or equal to CharIndex on
    // any input containing supplementary-plane characters.
    public int RuneIndex { get; }

    // User-perceived characters (graphemes) per UAX #29. An emoji ZWJ sequence or a
    // letter-plus-combining-mark counts as one grapheme, so this index
    // is smaller than or equal to RuneIndex on any input containing
    // multi-rune graphemes.
    public int GraphemeIndex { get; }

    // Zero-based line number, LSP convention.
    public int Line { get; }

    // Zero-based column within the line, in chars (UTF-16 code units),
    // same unit as CharIndex.
    public int Column { get; }

    internal SourcePosition(int charIndex, int runeIndex, int graphemeIndex, int line, int column)
    {
        CharIndex = charIndex;
        RuneIndex = runeIndex;
        GraphemeIndex = graphemeIndex;
        Line = line;
        Column = column;
    }

    // Compute all five position units from a character index into the
    // input string. Walks the string from 0 to charIndex once, counting
    // runes, graphemes, and line breaks along the way. O(charIndex).
    // Out-of-range charIndex values are clamped to [0, input.Length].
    public static SourcePosition From(string input, int charIndex)
    {
        if (input == null) input = string.Empty;
        int limit = charIndex;
        if (limit < 0) limit = 0;
        if (limit > input.Length) limit = input.Length;

        int runeIndex = SourcePositionConverter.ToRuneIndex(input, limit);
        int graphemeIndex = SourcePositionConverter.ToGraphemeIndex(input, limit);
        SourcePositionConverter.ToLineColumn(input, limit, out int line, out int column);
        return new SourcePosition(limit, runeIndex, graphemeIndex, line, column);
    }
}
