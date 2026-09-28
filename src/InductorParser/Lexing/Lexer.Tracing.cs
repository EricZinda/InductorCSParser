using System.IO;
using System.Runtime.CompilerServices;
using InductorParser.Tracing;

namespace InductorParser.Lexing;

// Tracing surface. Rules call IsTracing / Trace on every parse step.
// When the sink is off both collapse to a few inlined null checks
// (see Trace's comment for the cost story). When the sink is on,
// each rule entry emits one indented line through WriteTraceLine.
public sealed partial class Lexer
{
    // Trace destination and verbosity. Null _traceSink means tracing is off.
    // When set, every rule, Lexer.Read, and deepest-failure update writes
    // one line per event.
    private TextWriter? _traceSink;
    private TraceLevel _traceLevel;

    /// <summary>
    /// True when a trace sink is attached and its level is at or above
    /// <paramref name="level"/>, so a trace line at this level would be written.
    /// </summary>
    /// <remarks>
    /// A rule that wants to emit a trace line should call the protected
    /// <see cref="InductorParser.Rule.TraceSuccess(InductorParser.Lexing.Lexer,InductorParser.Tracing.TraceInterpolatedStringHandler)">Rule.TraceSuccess</see> / TraceFailure helpers on <see cref="Rule"/> instead. Those
    /// already cost nothing when the level is gated out. Check this overload
    /// only to skip expensive work that builds the arguments for such a message
    /// at <paramref name="level"/>, which the helpers can't gate for you.
    /// </remarks>
    public bool IsTracing(TraceLevel level) =>
        _traceSink != null && _traceLevel >= level;

    /// <summary>
    /// True when a trace sink is attached at all, whatever its level.
    /// </summary>
    /// <remarks>
    /// Use this when behavior should change because someone is watching the
    /// parse (turning off an optimization that would create gaps in the trace,
    /// for example), as opposed to <see cref="IsTracing(TraceLevel)">Lexer.IsTracing(TraceLevel)</see>, which
    /// asks whether to emit a line at a specific verbosity.
    /// </remarks>
    public bool IsTracing() => _traceSink != null;

    // The `message` parameter is a TraceInterpolatedStringHandler,
    // which means callers can write `lexer.Trace(level, label, outcome, $"...")`
    // and the C# compiler will skip building the string when the sink is off or the level
    // means the trace won't be emitted. No "if" needed at the caller. See
    // TraceInterpolatedStringHandler for how the compiler rewrite
    // actually works.
    //
    // Cost when tracing is off (canonical reference for trace perf):
    // Note that lexer.Trace(...) still gets called even when tracing
    // is off. The TraceInterpolatedStringHandler argument only gates the
    // expensive string-building work, not the method invocation
    // itself. Trace stays cheap because:
    //
    //   1. The first thing inside Trace is a null check, so Trace
    //      returns immediately without doing any indent/write work.
    //      The Rule.TraceSuccess / TraceFailure helpers do the same
    //      thing one layer up.
    //   2. Trace has [MethodImpl(MethodImplOptions.AggressiveInlining)],
    //      so in Release builds the JIT folds the body into the caller.
    //      What looks like a method call in the IL becomes a handful
    //      of inline machine instructions.
    //
    // Net cost when tracing is off: about the same as the hand written
    // alternative:
    //
    //     if (lexer.IsTracing(level))
    //         lexer.WriteTraceLine(label, outcome, $"...");
    //
    // In Debug builds where AggressiveInlining
    // is sometimes ignored, you do pay one real call frame per trace site.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Trace(
        TraceLevel level,
        string label,
        TraceOutcome outcome,
        [InterpolatedStringHandlerArgument("", nameof(level))]
        TraceInterpolatedStringHandler message)
    {
        string? formatted = message.GetFormattedOrNull();
        if (formatted == null) return;
        WriteTraceLine(label, outcome, formatted);
    }

    // Write one trace line
    // Fields are written incrementally rather than pre-concatenated to
    // avoid allocating an intermediate string for every line.
    internal void WriteTraceLine(string label, TraceOutcome outcome, string message)
    {
        for (int i = 0; i < _transactionDepth * 3; i++)
            _traceSink!.Write(' ');
        switch (outcome)
        {
            case TraceOutcome.Success: 
                _traceSink!.Write("SUCC | "); 
                break;
            case TraceOutcome.Failure: 
                _traceSink!.Write("FAIL | "); 
                break;
        }
        // The label is "{Name}:{ruleClassName}" (see Rule.BuildTraceLabel).
        // The class-name half is always a safe literal, but the Name half
        // is the user's .As("...") string, which .As doesn't validate, so
        // a control / line-separator char in a rule name would otherwise
        // splice raw into the line and split the one-event-per-line layout.
        // DisplayEscape rewrites Cc / Zl / Zp to U+XXXX, the same treatment
        // matched text gets through TraceInterpolatedStringHandler and a
        // .WithError message gets through Rule.AppendErrorMessage. The
        // fast path returns the label unchanged (no allocation) when it
        // holds no such char, which is every built-in label.
        _traceSink!.Write(DisplayEscape.Escape(label, 0, label.Length));
        if (message.Length > 0)
        {
            _traceSink.Write(": ");
            _traceSink.Write(message);
        }
        _traceSink.WriteLine();
    }
}
