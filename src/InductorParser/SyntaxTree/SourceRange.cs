namespace InductorParser.SyntaxTree;

/// <summary>
/// A start/end pair of <see cref="SourcePosition"/>s describing a span of source characters.
/// <see cref="End"/> is one past the last character matched, so End.CharIndex - Start.CharIndex
/// equals the matched length in chars.
/// </summary>
/// <remarks>
/// Both endpoints carry the source string they point into (via <see cref="SourcePosition.Input"/>),
/// so <see cref="SubstringOfInput"/> can produce the matched text without the consumer having to know
/// which input the range came from.
/// </remarks>
public readonly struct SourceRange
{
    /// <summary>The start of the range: the first character matched.</summary>
    public SourcePosition Start { get; }

    /// <summary>The end of the range, one past the last character matched.</summary>
    public SourcePosition End { get; }

    internal SourceRange(SourcePosition start, SourcePosition end)
    {
        Invariant.That(ReferenceEquals(start.Input, end.Input),
            $"SourceRange endpoints point into different strings (Start.Input length "
            + $"{start.Input.Length}, End.Input length {end.Input.Length}). Both must come "
            + "from the same parse's original input, since SubstringOfInput indexes Start.Input "
            + "with End.CharIndex.");
        Start = start;
        End = end;
    }

    /// <summary>
    /// The substring of <see cref="SourcePosition.Input"/> covered by this range.
    /// </summary>
    /// <remarks>
    /// Equivalent to Start.Input.Substring(Start.CharIndex, End.CharIndex - Start.CharIndex), but lets
    /// the caller skip remembering which string the range came from.
    /// </remarks>
    public string SubstringOfInput() =>
        Start.Input.Substring(Start.CharIndex, End.CharIndex - Start.CharIndex);
}
