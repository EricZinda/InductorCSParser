namespace InductorParser.Tracing;

/// <summary>
/// Controls how verbose the parser's trace output is when a <see cref="InductorParser.ParseOptions.TraceSink">ParseOptions.TraceSink</see> is
/// wired up on ParseOptions.
/// </summary>
/// <remarks>
/// Names match the TraceDetail enum in the C++ library so captured traces
/// from either side read the same. <see cref="Normal">TraceLevel.Normal</see> and <see cref="Detailed">TraceLevel.Detailed</see> are placeholders
/// today: every built-in rule emits its Succ/FAIL line at <see cref="Diagnostic">TraceLevel.Diagnostic</see>, as do
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
