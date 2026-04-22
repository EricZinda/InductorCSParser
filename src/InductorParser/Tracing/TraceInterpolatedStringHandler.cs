using System;
using System.Runtime.CompilerServices;
using System.Text;
using InductorParser.Lexing;

namespace InductorParser.Tracing;

// C# has a feature called "Interpolated string handler" and this is one
// for Lexer.Trace and the Rule
// TraceSuccess / TraceFailure helpers. Lets rule code write:
//
//     TraceSuccess(lexer, $"found {count}")
//
// or, at the lexer-internal layer:
//
//     lexer.Trace(TraceLevel.Diagnostic, label, outcome, $"found {count}")
//
// instead of having to wrap every call in a hand-written guard like:
//
//     if (lexer.IsTracing(TraceLevel.Diagnostic))
//         lexer.WriteTraceLine(label, outcome, $"found {count}");
//
// The handler defers the
// $"..." formatting until after IsTracing has decided the message
// will actually be emitted, so callers pay nothing for the
// formatting work when tracing is off.
//
// When the C# compiler sees a $"..."
// expression passed to a parameter typed as
// TraceInterpolatedStringHandler, it rewrites the call to:
//
//     var handler = new TraceInterpolatedStringHandler(literalLen, formattedCount, lexer, level, out bool shouldAppend);
//     if (shouldAppend)
//     {
//         handler.AppendLiteral("found ");
//         handler.AppendFormatted(count);
//     }
//     lexer.Trace(level, label, outcome, handler);
//
// The constructor sets shouldAppend=true only when this message
// will actually be emitted: tracing has somewhere to write to
// (a TraceSink is configured) AND the configured trace level is
// verbose enough to include this message's level. When shouldAppend
// is false, the compiler skips every AppendLiteral / AppendFormatted
// call, so the int never gets boxed, no ToString() runs, and no
// StringBuilder gets allocated.
//
// The "" and nameof(level) in InterpolatedStringHandlerArgument on
// Lexer.Trace tell the C# compiler which arguments to include in
// the constructor: "" means the receiver (the Lexer instance), and
// "level" means the level parameter. Both are available at the call
// site before the handler is built.
//
// ref struct keeps this stack-only for the duration of the call.
// Since the handler never outlives the Trace invocation, stack
// allocation is both safe and free.
//
// Proof the rewrite is actually happening (not silently falling back
// to eager $"..." formatting):
// InductorParser.Tests/Core/TracingTests.cs has two side-effect tests
// to make sure:
//
//   * Off_path_does_not_evaluate_interpolated_arguments
//     places Interlocked.Increment inside the interpolation hole and
//     asserts the counter stays at zero with tracing off. If the
//     compiler stopped honoring the handler attribute (polyfill
//     broken on a target framework, LangVersion regression, etc.),
//     the increment fires and this test fails immediately.
//
//   * On_path_evaluates_interpolated_arguments_exactly_once
//     is the complement: with tracing on, the same expression must
//     evaluate exactly once. Catches the opposite regression where
//     someone "optimizes" the handler in a way that drops or doubles
//     up the AppendFormatted calls.
//
// If you're touching this file, run those two tests first. Build
// success alone isn't enough — see the test comments for why.
[InterpolatedStringHandler]
public ref struct TraceInterpolatedStringHandler
{
    // Null when tracing is off for this (level, lexer) pair. The
    // compiler-generated AppendLiteral/AppendFormatted calls are
    // skipped in that case, so the builder is never touched and
    // stays null. Trace reads this via GetFormattedOrNull to decide
    // whether the call is a no-op.
    private StringBuilder? _builder;

    public TraceInterpolatedStringHandler(
        int literalLength,
        int formattedCount,
        Lexer lexer,
        TraceLevel level,
        out bool shouldAppend)
    {
        if (lexer.IsTracing(level))
        {
            // Pre-size to at least the literal length. The formatted
            // pieces tend to be short (an int, a char, a short string),
            // so overshooting is more expensive than growing once.
            _builder = new StringBuilder(literalLength);
            shouldAppend = true;
        }
        else
        {
            _builder = null;
            shouldAppend = false;
        }
    }

    // Overload used by the short-form Rule helpers
    // (Rule.TraceSuccess(lexer, $"...") / Rule.TraceFailure(lexer, $"...")).
    // The handler attribute on those helpers only adds the lexer argument,
    // so the compiler resolves this 4-arg constructor instead of the
    // 5-arg one above.
    public TraceInterpolatedStringHandler(
        int literalLength,
        int formattedCount,
        Lexer lexer,
        out bool shouldAppend)
        : this(literalLength, formattedCount, lexer, TraceLevel.Diagnostic, out shouldAppend)
    {
    }

    // The compiler emits calls to these for each chunk of the
    // interpolated string. They're no-ops when _builder is null,
    // but the compiler also skips the calls entirely when
    // shouldAppend was false, so the no-op guards here are mostly
    // defense in depth (and appease nullable analysis).
    public void AppendLiteral(string value) => _builder?.Append(value);

    public void AppendFormatted<T>(T value) => _builder?.Append(value?.ToString());

    public void AppendFormatted(string? value) => _builder?.Append(value);

    public void AppendFormatted(ReadOnlySpan<char> value) => _builder?.Append(value);

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
