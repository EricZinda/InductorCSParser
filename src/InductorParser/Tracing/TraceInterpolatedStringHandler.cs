using System;
using System.Runtime.CompilerServices;
using System.Text;
using InductorParser.Lexing;

namespace InductorParser.Tracing;

// Lets rule code write:
//
//     TraceSuccess(lexer, $"found {count}")
//
// or, at the lexer-internal layer:
//
//     lexer.Trace(TraceLevel.Diagnostic, label, outcome, $"found {count}")
//
// instead of a hand-written check:
//
//     if (lexer.IsTracing(TraceLevel.Diagnostic))
//         lexer.WriteTraceLine(label, outcome, $"found {count}");
//
// When the compiler sees a $"..." passed to a TraceInterpolatedStringHandler
// parameter, it builds the handler first and only runs the
// AppendLiteral / AppendFormatted calls when the constructor reports
// shouldAppend. shouldAppend is true only when tracing has somewhere to write
// (a TraceSink is configured) and the configured level is verbose enough for
// this message. When it's false, the int never gets boxed, no ToString runs,
// and no StringBuilder is allocated.
//
// The "" and nameof(level) in InterpolatedStringHandlerArgument on Lexer.Trace
// tell the compiler which arguments to pass to the constructor: "" is the
// receiver (the Lexer), "level" is the level parameter.
//
// ref struct keeps this stack-only. The handler never outlives the Trace
// call, so stack allocation is safe and free.
//
// TracingTests.Off_path_does_not_evaluate_interpolated_arguments and
// On_path_evaluates_interpolated_arguments_exactly_once prove the deferral
// happens. If you touch this file, run those two first: build success alone
// won't catch a broken handler rewrite.

/// <summary>
/// Interpolated string handler for <c>Lexer.Trace</c> and the
/// <c>Rule.TraceSuccess</c> / <c>Rule.TraceFailure</c> helpers. Defers the
/// <c>$"..."</c> formatting until tracing has decided the message will be
/// emitted, so callers pay nothing for the formatting when tracing is off.
/// </summary>
[InterpolatedStringHandler]
public ref struct TraceInterpolatedStringHandler
{
    // Null when tracing is off for this (level, lexer) pair. The
    // compiler-generated AppendLiteral/AppendFormatted calls are
    // skipped in that case, so the builder is never touched and
    // stays null. Trace reads this via GetFormattedOrNull to decide
    // whether the call is a no-op.
    private StringBuilder? _builder;

    /// <summary>
    /// Constructor the compiler resolves for <c>Lexer.Trace</c>. Allocates a
    /// StringBuilder and sets <paramref name="shouldAppend"/> to true only
    /// when <paramref name="lexer"/> is tracing at <paramref name="level"/>.
    /// </summary>
    public TraceInterpolatedStringHandler(
        int literalLength,
        int formattedCount,
        Lexer lexer,
        TraceLevel level,
        out bool shouldAppend)
    {
        if (lexer.IsTracing(level))
        {
            _builder = new StringBuilder();
            shouldAppend = true;
        }
        else
        {
            _builder = null;
            shouldAppend = false;
        }
    }

    /// <summary>
    /// Constructor the compiler resolves for the short-form
    /// Rule.TraceSuccess / Rule.TraceFailure helpers, whose handler attribute
    /// only adds the lexer argument. Defaults the level to
    /// <see cref="TraceLevel.Diagnostic"/>.
    /// </summary>
    public TraceInterpolatedStringHandler(
        int literalLength,
        int formattedCount,
        Lexer lexer,
        out bool shouldAppend)
        : this(literalLength, formattedCount, lexer, TraceLevel.Diagnostic, out shouldAppend)
    {
    }

    // Nothing calls these directly. The compiler emits them when it rewrites a
    // $"..." passed to a TraceInterpolatedStringHandler parameter, and skips
    // them entirely when shouldAppend was false, so the null checks are
    // defense in depth (and appease nullable analysis). Routing every
    // formatted value through DisplayEscape enforces "one trace event renders
    // as one physical line": a Token('\n') match or a CRLF cluster can't split
    // a trace line no matter which rule quoted it.

    /// <summary>Appends the static text between interpolation holes verbatim.</summary>
    public void AppendLiteral(string value) => _builder?.Append(value);

    /// <summary>
    /// Appends an interpolation hole's value, escaping control and line-separator
    /// chars to <c>U+XXXX</c> so the trace stays on one line.
    /// </summary>
    public void AppendFormatted<T>(T value)
    {
        if (_builder == null) return;
        string? text = value?.ToString();
        if (text != null)
            DisplayEscape.AppendEscaped(_builder, text.AsSpan());
    }

    /// <summary>
    /// Appends a string interpolation hole, escaping control and line-separator
    /// chars to <c>U+XXXX</c> so the trace stays on one line.
    /// </summary>
    public void AppendFormatted(string? value)
    {
        if (_builder != null && value != null)
            DisplayEscape.AppendEscaped(_builder, value.AsSpan());
    }

    /// <summary>
    /// Appends a span interpolation hole, escaping control and line-separator
    /// chars to <c>U+XXXX</c> so the trace stays on one line.
    /// </summary>
    public void AppendFormatted(ReadOnlySpan<char> value)
    {
        if (_builder != null)
            DisplayEscape.AppendEscaped(_builder, value);
    }

    // Called by Lexer.Trace and the Rule.TraceSuccess /
    // Rule.TraceFailure helpers to pull out the formatted message.
    // Null means the handler was constructed in the "don't emit"
    // state. The caller uses that as its short-circuit signal.
    // Clearing the builder reference defends against accidental
    // double-use if the handler were ever kept alive past the call
    // (it isn't, because ref struct rules, but cheap insurance).
    internal string? GetFormattedOrNull()
    {
        var result = _builder?.ToString();
        _builder = null;
        return result;
    }
}
