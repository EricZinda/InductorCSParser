using System.IO;
using InductorParser.Tracing;

namespace InductorParser.Lexing;

// One token per Unicode code point (rune). Runes above U+FFFF encode as
// two UTF-16 chars in .NET (a surrogate pair). The lexer reads both as
// one 2-char token rather than splitting them. Every other rune fits in
// a single char, so those tokens are 1 char long.
public sealed class RuneLexer : Lexer
{
    public RuneLexer(string input) : base(input) { }

    public RuneLexer(string input, TextWriter? traceSink, TraceLevel traceLevel)
        : base(input, traceSink, traceLevel) { }

    // Bounded constructor used by WithinGraphemeRule to build a
    // sub-lexer over a sub-range of a shared input string without
    // copying it. Internal because it's tied to the sub-lexer use
    // case; callers building a normal lexer should pass the full
    // string to the public constructors above. Trace is disabled on
    // the sub-lexer (passes a null sink); inner-rule tracing isn't
    // plumbed through to the outer trace today.
    internal RuneLexer(string input, int startPosition, int endPosition)
        : base(input, startPosition, endPosition, traceSink: null, traceLevel: TraceLevel.Normal) { }

    protected override int NextTokenLength(int startOffset)
    {
        char c = Input[startOffset];
        if (char.IsHighSurrogate(c)
            && startOffset + 1 < Input.Length
            && char.IsLowSurrogate(Input[startOffset + 1]))
        {
            return 2;
        }
        return 1;
    }
}
