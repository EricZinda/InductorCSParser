namespace InductorParser.SyntaxTree;

// A start/end pair of SourcePositions describing a span of source
// characters. End is one past the last character matched, so
// End.CharIndex - Start.CharIndex equals the matched length in chars.
//
// Both endpoints carry the source string they point into (via
// SourcePosition.Input), so SubstringOfInput can produce the matched
// text without the consumer having to know which input the range came
// from.
public readonly struct SourceRange
{
    public SourcePosition Start { get; }
    public SourcePosition End { get; }

    internal SourceRange(SourcePosition start, SourcePosition end)
    {
        Start = start;
        End = end;
    }

    // The substring of Start.Input covered by this range. Equivalent
    // to Start.Input.Substring(Start.CharIndex, End.CharIndex - Start.CharIndex)
    // but lets the caller skip remembering which string the range
    // came from.
    public string SubstringOfInput() =>
        Start.Input.Substring(Start.CharIndex, End.CharIndex - Start.CharIndex);
}
