namespace InductorParser.SyntaxTree;

// A point in a source string, expressed in four units. CharIndex is
// the canonical one (UTF-16 code units, what string.Substring uses);
// the others are derived from it by walking the input from offset 0.
//
// Line and Column follow the Language Server Protocol convention used
// by editor diagnostics: zero-based, with line breaks at "\n", "\r\n"
// (one break, not two), and lone "\r". Column is in chars, same unit
// as CharIndex.
//
// Input is the string the position is into. Always the user's original
// input. When a grammar normalizes (FormC by default), the parser
// translates parseInput coordinates back to original-input coordinates
// before constructing the SourcePosition, so the value here is in the
// frame the user typed in. 
public readonly struct SourcePosition
{
    // UTF-16 code units, what string.Substring / Span<char>.Slice / LSP use.
    public int CharIndex { get; }

    // Tokens (characters as the user sees them), using the same
    // StringInfo text-element segmentation the lexer uses. On modern
    // .NET that follows UAX #29 extended grapheme clusters. A family
    // emoji or an accented letter the user typed as base + accent is
    // one token even though it's several runes underneath, so this
    // index is smaller than or equal to CharIndex on any input that
    // contains those.
    public int TokenIndex { get; }

    // Zero-based line number, LSP convention.
    public int Line { get; }

    // Zero-based column within the line, in chars (UTF-16 code units),
    // same unit as CharIndex.
    public int Column { get; }

    // The source string CharIndex is an offset into. Always the user's
    // original input, even for parses that normalized the input under
    // the hood. Never null; a SourcePosition built from a null input
    // stores string.Empty.
    public string Input { get; }

    internal SourcePosition(string input, int charIndex, int tokenIndex, int line, int column)
    {
        Input = input ?? string.Empty;
        CharIndex = charIndex;
        TokenIndex = tokenIndex;
        Line = line;
        Column = column;
    }

    // Compute all four position units from a character index into the
    // input string. Walks the string from 0 to charIndex once, counting
    // tokens and line breaks along the way. O(charIndex).
    // Out-of-range charIndex values are clamped to [0, input.Length].
    public static SourcePosition From(string input, int charIndex)
    {
        if (input == null) input = string.Empty;
        int limit = charIndex;
        if (limit < 0) limit = 0;
        if (limit > input.Length) limit = input.Length;

        int tokenIndex = SourcePositionConverter.ToTokenIndex(input, limit);
        SourcePositionConverter.ToLineColumn(input, limit, out int line, out int column);
        return new SourcePosition(input, limit, tokenIndex, line, column);
    }
}
