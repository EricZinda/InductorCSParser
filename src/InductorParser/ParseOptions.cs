using System.IO;
using InductorParser.Tracing;

namespace InductorParser;

public sealed class ParseOptions
{
    // Atomic unit the lexer reads. Default is grapheme so user-typed text 
    // behaves the way users expect, even though
    // the underlying StringInfo implementation has known gaps on pre-.NET 5
    // runtimes (see GraphemeLexer.cs and backlog/i001).
    public InputUnit InputUnit { get; set; } = InputUnit.Grapheme;

    // Where trace output goes when the parser is tracing. Null means
    // tracing is off
    public TextWriter? TraceSink { get; set; }

    // Gates how verbose the trace output is.
    public TraceLevel TraceLevel { get; set; } = TraceLevel.Diagnostic;
}
