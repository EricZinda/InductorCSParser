using System;

namespace InductorParser.SyntaxTree;

/// <summary>
/// A start/end pair of <see cref="SourcePosition"/>s describing a span of source characters.
/// <see cref="End"/> is one past the last character matched, so <see cref="InductorParser.SyntaxTree.SourcePosition.CharIndex">End.CharIndex</see> - <see cref="InductorParser.SyntaxTree.SourcePosition.CharIndex">Start.CharIndex</see>
/// equals the matched length in chars.
/// </summary>
/// <remarks>
/// Both endpoints keep the source string they point into (via <see cref="SourcePosition.Input"/>),
/// so <see cref="SubstringOfInput"/> can produce the matched text without the consumer having to know
/// which input the range came from.
/// </remarks>
public readonly struct SourceRange
{
    /// <summary>The start of the range: the first character matched.</summary>
    public SourcePosition Start { get; }

    /// <summary>The end of the range, one past the last character matched.</summary>
    public SourcePosition End { get; }

    /// <summary>
    /// Builds a range from two existing endpoints. The typical use is synthesizing the span of
    /// a compound AST node from its children's spans: <c><see cref="SourceRange.SourceRange(SourcePosition, SourcePosition)">new SourceRange(left.Start, right.End)</see></c>
    /// covers everything from the start of the left child to the end of the right one.
    /// </summary>
    /// <remarks>
    /// Both endpoints must point into the same input text, because <see cref="SubstringOfInput"/>
    /// indexes <see cref="InductorParser.SyntaxTree.SourcePosition.Input">Start.Input</see> with <see cref="InductorParser.SyntaxTree.SourcePosition.CharIndex">End.CharIndex</see>. Positions from the same parse always
    /// do. Two separate string instances with equal content count as the same input too, so
    /// positions built by <see cref="SourcePosition.From"/> over two copies of the same text
    /// also work.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The endpoints point into different input strings, or <paramref name="end"/> comes before
    /// <paramref name="start"/>.
    /// </exception>
    public SourceRange(SourcePosition start, SourcePosition end)
    {
        if (start.Input != end.Input)
            throw new ArgumentException(
                "SourceRange endpoints point into different input strings. Both positions must "
                + "come from the same input (typically the same parse), because SubstringOfInput "
                + "reads the matched text out of Start.Input using End.CharIndex.", nameof(end));
        if (end.CharIndex < start.CharIndex)
            throw new ArgumentException(
                $"SourceRange end (char index {end.CharIndex}) comes before its start (char index "
                + $"{start.CharIndex}). Start is the first character matched and End is one past "
                + "the last, so End must be at or after Start.", nameof(end));
        Start = start;
        End = end;
    }

    /// <summary>
    /// The substring of <see cref="SourcePosition.Input"/> covered by this range.
    /// </summary>
    /// <remarks>
    /// Equivalent to <c><see cref="string.Substring(int, int)">Start.Input.Substring(Start.CharIndex, End.CharIndex - Start.CharIndex)</see></c>, but lets
    /// the caller skip remembering which string the range came from.
    /// </remarks>
    public string SubstringOfInput() =>
        Start.Input.Substring(Start.CharIndex, End.CharIndex - Start.CharIndex);

    /// <summary>
    /// The full text of the line <see cref="Start"/> falls on, terminator excluded.
    /// </summary>
    /// <remarks>
    /// Shorthand for <see cref="InductorParser.SyntaxTree.SourcePosition.SourceLine">Start.SourceLine()</see>. Use it with
    /// <see cref="SourcePosition.CharColumn"/> to draw a compiler-style caret under
    /// this span: the line for context, the column to indent the caret. On a
    /// range that spans more than one line this returns the first line, the one
    /// <see cref="Start"/> is on. See <see cref="SourcePosition.SourceLine"/> for
    /// how the line boundaries are found.
    /// </remarks>
    public string SourceLine() => Start.SourceLine();
}
