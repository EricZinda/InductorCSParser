namespace InductorParser.SyntaxTree;

// A start/end pair of SourcePositions describing a span of source
// characters. End is one past the last character matched, so
// End.CharIndex - Start.CharIndex equals the matched length in chars.
public readonly struct SourceRange
{
    public SourcePosition Start { get; }
    public SourcePosition End { get; }

    internal SourceRange(SourcePosition start, SourcePosition end)
    {
        Start = start;
        End = end;
    }
}
