using System.IO;
using InductorParser.Tracing;

namespace InductorParser.Lexing;

// One token per Unicode code point (rune). Runes above U+FFFF encode as
// two UTF-16 chars in .NET (a surrogate pair); the lexer reads both as
// one 2-char token rather than splitting them. Every other rune fits in
// a single char, so those tokens are 1 char long.
public sealed class RuneLexer : Lexer
{
    public RuneLexer(string input) : base(input) { }

    public RuneLexer(string input, TextWriter? traceSink, TraceLevel traceLevel)
        : base(input, traceSink, traceLevel) { }

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
