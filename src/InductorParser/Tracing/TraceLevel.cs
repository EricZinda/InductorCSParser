namespace InductorParser.Tracing;

/// <summary>
/// Controls how verbose the parser's trace output is when a <see cref="InductorParser.ParseOptions.TraceSink">TraceSink</see> is
/// wired up on ParseOptions.
/// </summary>
/// <remarks>
/// Names match the TraceDetail enum in the C++ library so captured traces
/// from either side read the same. Normal and Detailed are placeholders
/// today: every built-in rule emits its Succ/FAIL line at Diagnostic, as do
/// <see cref="InductorParser.Lexing.Lexer.Read">Lexer.Read</see> and deepest-failure updates.
/// </remarks>
public enum TraceLevel
{
    /// <summary>Least verbose. Unused by the library.</summary>
    Normal,

    /// <summary>Unused by the library.</summary>
    Detailed,

    /// <summary>
    /// Most verbose. Every built-in rule emits its trace line at this level.
    /// </summary>
    Diagnostic
}
