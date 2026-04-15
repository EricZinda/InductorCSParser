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
// Replacing this with a vendored UAX #29 implementation is tracked in
// backlog/i001-vendor-uax29.md. Until then this is the best the runtime
// will give us, and grammars that operate on ASCII-only or simple BMP
// content (the Setting example, most config-file grammars) are unaffected.
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
