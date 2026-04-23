using System.Globalization;
using System.IO;
using InductorParser.Tracing;

namespace InductorParser.Lexing;

// One token per Unicode grapheme cluster.
//
// Implementation uses System.Globalization.StringInfo.GetNextTextElement, which
// is UAX #29 rev. 35 compliant on .NET 5 and later but uses pre-UAX29 custom
// logic on .NET Framework, .NET Core 3.x, and the Mono runtimes Unity ships.
// On those older runtimes some real grapheme clusters split incorrectly
// (Thai "kam", multi-codepoint emoji like the woman-shrugging sequence).
//
// Replacing this class with a UAX #29 implementation that behaves better
// will fix the issue.  Until then, this class will use the implementation
// provided by the .NetFramework version it is complied with.
// Grammars that operate on ASCII-only or single-UTF-16-char content
// (the Setting example, most config-file grammars) are unaffected.
public sealed class GraphemeLexer : Lexer
{
    public GraphemeLexer(string input) : base(input) { }

    public GraphemeLexer(string input, TextWriter? traceSink, TraceLevel traceLevel)
        : base(input, traceSink, traceLevel) { }

    protected override int NextTokenLength(int startOffset)
    {
        string element = StringInfo.GetNextTextElement(Input, startOffset);
        return element.Length;
    }
}
