namespace InductorParser.StateMachine;

// (offset, length) pair into the input string for a single match
// fired by a rule. Mirrors what System.Text.RegularExpressions reports
// per Match: a position and an extent, both in input chars (UTF-16
// code units in .NET strings, which is what callers usually want for
// AsSpan / Substring without surprises). Use Encoding.UTF8.GetByteCount
// over the slice if a UTF-8-byte total is needed instead.
public readonly struct MatchSpan
{
    public int Offset { get; }
    public int Length { get; }

    public MatchSpan(int offset, int length)
    {
        Offset = offset;
        Length = length;
    }
}
