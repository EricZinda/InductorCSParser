namespace InductorParser.SyntaxTree;

/// <summary>
/// A point in a source string, expressed in several units. <see cref="CharIndex">SourcePosition.CharIndex</see> is the canonical
/// one (UTF-16 code units, what string.Substring uses). The others are derived from it by walking
/// the input from offset 0.
/// </summary>
/// <remarks>
/// Line and <see cref="InductorParser.SyntaxTree.SourcePosition.CharColumn">SourcePosition.CharColumn</see> are zero-based. <see cref="InductorParser.SyntaxTree.SourcePosition.CharColumn">SourcePosition.CharColumn</see> counts UTF-16 code units, the same unit as
/// <see cref="CharIndex">SourcePosition.CharIndex</see>, matching the Language Server Protocol convention editor diagnostics
/// use. <see cref="TokenColumn">SourcePosition.TokenColumn</see> is the grapheme-based counterpart, for human-facing output. Line breaks follow <a href="https://www.unicode.org/reports/tr18/#Line_Boundaries">UTS #18</a> §1.6 (RL1.6), the same set <see cref="InductorParser.Rules.EndOfLine">Rules.EndOfLine()</see> accepts: LF, CRLF (one
/// break, not two), lone CR, VT, FF, NEL (U+0085), LS (U+2028), PS (U+2029). Keeping the two sets
/// aligned matters for grammars that use <see cref="InductorParser.Rules.EndOfLine">Rules.EndOfLine()</see> on Unicode input: every terminator the grammar
/// consumes also bumps the reported line. That set is a superset of the LF, CRLF, and lone CR a
/// Language Server Protocol client recognizes, so the reported line matches an editor on ordinary source and diverges
/// only on input that contains the rarer terminators (VT, FF, NEL, LS, PS).
/// <para>
/// <see cref="Input">SourcePosition.Input</see> is always the user's original input. When a grammar normalizes (FormC by
/// default), the parser translates parseInput coordinates back to original-input coordinates before
/// constructing the <see cref="InductorParser.SyntaxTree.SourcePosition">SourcePosition</see>, so the value here is in the text the user typed in.
/// </para>
/// </remarks>
public readonly struct SourcePosition
{
    /// <summary>
    /// The character offset into <see cref="Input">SourcePosition.Input</see>, in UTF-16 code units (the unit string.Substring
    /// and the Language Server Protocol use).
    /// </summary>
    public int CharIndex { get; }

    /// <summary>
    /// The index of the token (i.e. a grapheme: a character as the user sees it) that <see cref="CharIndex">SourcePosition.CharIndex</see> falls in,
    /// using the same <a href="https://www.unicode.org/reports/tr29/">UAX #29</a> extended-grapheme-cluster segmentation the lexer uses.
    /// </summary>
    /// <remarks>
    /// A family emoji or an accented letter typed as base + accent is one token even though it's
    /// several runes underneath, so this index is less than or equal to <see cref="CharIndex">SourcePosition.CharIndex</see> on any
    /// input that contains those.
    /// </remarks>
    public int TokenIndex { get; }

    /// <summary>Zero-based line number, Language Server Protocol convention.</summary>
    public int Line { get; }

    /// <summary>
    /// Zero-based column within the line, measured in chars (UTF-16 code units, the
    /// same unit as <see cref="CharIndex">SourcePosition.CharIndex</see> and the Language Server Protocol).
    /// </summary>
    public int CharColumn { get; }

    /// <summary>
    /// Zero-based column within the line, measured in tokens (Unicode graphemes).
    /// Computed lazily from <see cref="CharIndex">SourcePosition.CharIndex</see> and <see cref="Input">SourcePosition.Input</see>.
    /// </summary>
    /// <remarks>
    /// The human-facing counterpart to <see cref="CharColumn">SourcePosition.CharColumn</see>: an emoji, a flag,
    /// or a base character plus a combining mark earlier on the line counts as one
    /// column, not as its several UTF-16 code units, so it matches the character a
    /// person sees. Use <see cref="CharColumn">SourcePosition.CharColumn</see> to match an editor or a Language
    /// Server Protocol client, which count columns in chars.
    /// </remarks>
    public int TokenColumn => SourcePositionConverter.ToTokenColumn(Input, CharIndex);

    /// <summary>
    /// One-based line number (<see cref="Line">SourcePosition.Line</see> + 1), the way a person reading an
    /// editor counts lines. Use this for human-facing messages. Use
    /// <see cref="Line">SourcePosition.Line</see> for the zero-based Language Server Protocol value.
    /// </summary>
    public int LineNumber => Line + 1;

    /// <summary>
    /// One-based char column (<see cref="CharColumn">SourcePosition.CharColumn</see> + 1). Use this for
    /// human-facing messages. Use <see cref="CharColumn">SourcePosition.CharColumn</see> for the zero-based
    /// Language Server Protocol value.
    /// </summary>
    public int CharColumnNumber => CharColumn + 1;

    /// <summary>
    /// One-based token (grapheme) column (<see cref="TokenColumn">SourcePosition.TokenColumn</see> + 1), the count
    /// a person makes of the characters they see. This is the column the default
    /// error message reports.
    /// </summary>
    public int TokenColumnNumber => TokenColumn + 1;

    /// <summary>
    /// The source string <see cref="CharIndex">SourcePosition.CharIndex</see> is an offset into, always the user's original input
    /// even for parses that normalized under the hood. Never null. A <see cref="InductorParser.SyntaxTree.SourcePosition">SourcePosition</see> built from a null
    /// input stores the empty string.
    /// </summary>
    public string Input { get; }

    /// <summary>
    /// The text of the line this position falls on: from the start of the line up
    /// to (not including) the next line terminator, taken from <see cref="Input">SourcePosition.Input</see>.
    /// </summary>
    /// <remarks>
    /// The line boundaries are the <a href="https://www.unicode.org/reports/tr18/#Line_Boundaries">UTS #18</a> terminators the parser counts for
    /// <see cref="Line">SourcePosition.Line</see> (see <see cref="TokenSet.IsLineTerminator(char)">TokenSet.IsLineTerminator(char)</see>), so
    /// this stays consistent with <see cref="Line">SourcePosition.Line</see> / <see cref="CharColumn">SourcePosition.CharColumn</see> and
    /// handles CRLF and the rarer terminators that splitting the input on '\n'
    /// would get wrong. Pair it with <see cref="CharColumn">SourcePosition.CharColumn</see> to indent a
    /// compiler-style caret under a token.
    /// </remarks>
    public string SourceLine()
    {
        int lineStart = CharIndex - CharColumn;
        int lineEnd = lineStart;
        while (lineEnd < Input.Length && !TokenSet.IsLineTerminator(Input[lineEnd]))
            lineEnd++;
        return Input.Substring(lineStart, lineEnd - lineStart);
    }

    internal SourcePosition(string input, int charIndex, int tokenIndex, int line, int charColumn)
    {
        Input = input ?? string.Empty;
        CharIndex = charIndex;
        TokenIndex = tokenIndex;
        Line = line;
        CharColumn = charColumn;
    }

    /// <summary>
    /// Computes all four position units from a character index into <paramref name="input"/>.
    /// </summary>
    /// <remarks>
    /// Walks the string from 0 to <paramref name="charIndex"/>: one pass counting tokens and one
    /// counting line breaks, O(charIndex) total (the token column adds a third, lazy
    /// walk on first access). Out-of-range values are clamped to [0, input.Length].
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
