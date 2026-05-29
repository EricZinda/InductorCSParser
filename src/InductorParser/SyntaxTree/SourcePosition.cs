namespace InductorParser.SyntaxTree;

/// <summary>
/// A point in a source string, expressed in four units. <see cref="CharIndex"/> is the canonical
/// one (UTF-16 code units, what string.Substring uses). The others are derived from it by walking
/// the input from offset 0.
/// </summary>
/// <remarks>
/// Line and Column are zero-based. Column counts UTF-16 code units, the same unit as
/// <see cref="CharIndex"/>, matching the Language Server Protocol convention editor diagnostics
/// use. Line breaks follow UAX #18 Annex C, the same set Rules.EndOfLine() accepts: LF, CRLF (one
/// break, not two), lone CR, VT, FF, NEL (U+0085), LS (U+2028), PS (U+2029). Keeping the two sets
/// aligned matters for grammars that use EndOfLine() on Unicode input: every terminator the grammar
/// consumes also bumps the reported line. That set is a superset of the LF, CRLF, and lone CR a
/// Language Server Protocol client recognizes, so the reported line matches an editor on ordinary source and diverges
/// only on input that contains the rarer terminators (VT, FF, NEL, LS, PS).
/// <para>
/// <see cref="Input"/> is always the user's original input. When a grammar normalizes (FormC by
/// default), the parser translates parseInput coordinates back to original-input coordinates before
/// constructing the SourcePosition, so the value here is in the text the user typed in.
/// </para>
/// </remarks>
public readonly struct SourcePosition
{
    /// <summary>
    /// The character offset into <see cref="Input"/>, in UTF-16 code units (the unit string.Substring
    /// and the Language Server Protocol use).
    /// </summary>
    public int CharIndex { get; }

    /// <summary>
    /// The index of the token (i.e. a grapheme: a character as the user sees it) that <see cref="CharIndex"/> falls in,
    /// using the same StringInfo text-element segmentation the lexer uses (UAX #29 extended grapheme
    /// clusters on modern .NET).
    /// </summary>
    /// <remarks>
    /// A family emoji or an accented letter typed as base + accent is one token even though it's
    /// several runes underneath, so this index is less than or equal to <see cref="CharIndex"/> on any
    /// input that contains those.
    /// </remarks>
    public int TokenIndex { get; }

    /// <summary>Zero-based line number, Language Server Protocol convention.</summary>
    public int Line { get; }

    /// <summary>
    /// Zero-based column within the line, in UTF-16 code units (same unit as <see cref="CharIndex"/>).
    /// </summary>
    public int Column { get; }

    /// <summary>
    /// The source string <see cref="CharIndex"/> is an offset into, always the user's original input
    /// even for parses that normalized under the hood. Never null. A SourcePosition built from a null
    /// input stores the empty string.
    /// </summary>
    public string Input { get; }

    internal SourcePosition(string input, int charIndex, int tokenIndex, int line, int column)
    {
        Input = input ?? string.Empty;
        CharIndex = charIndex;
        TokenIndex = tokenIndex;
        Line = line;
        Column = column;
    }

    /// <summary>
    /// Computes all four position units from a character index into <paramref name="input"/>.
    /// </summary>
    /// <remarks>
    /// Walks the string from 0 to <paramref name="charIndex"/> once, counting tokens and line breaks
    /// along the way (O(charIndex)). Out-of-range values are clamped to [0, input.Length].
    /// </remarks>
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
