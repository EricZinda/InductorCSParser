namespace InductorParser.Tracing;

// Controls how verbose the parser's trace output is when a TraceSink is
// wired up on ParseOptions. Names match the TraceDetail enum in the C++
// library so captured traces from either side read the same.
//
// Normal and Detailed are placeholders today: every built-in rule emits
// its Succ/FAIL line at Diagnostic, and Lexer.Read and deepest-failure
// updates also emit at Diagnostic.
public enum TraceLevel
{
    Normal,
    Detailed,
    Diagnostic
}
