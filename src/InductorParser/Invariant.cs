using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace InductorParser;

// Internal invariant check. Use for "this can't happen, but if it does,
// stop predictably right here" conditions: postconditions of a helper
// the caller can't see inside, loop bounds that depend on a property
// of an upstream computation, agreements between two internal
// collaborators about what each end is allowed to hand the other.
// Fires in Release as well as Debug. The cost
// of a never-taken branch on the parse hot path is less than the cost
// of silently producing wrong output when the invariant ever does
// break.
//
// Not for user-API errors. ".As(...) called twice" or "rule was never
// bound" are bugs in the user's grammar, not in InductorParser, and
// keep their own InvalidOperationException with a helpful message
// pointing at the fix. Invariant.That is for the other direction: a
// condition the user could never trigger by misusing the public API,
// only by hitting a bug in this assembly. The exception type
// (InductorParserBugException) is what tells the difference.
//
// Named Invariant rather than Assert to avoid colliding with NUnit's
// Assert in the test assembly, which has InternalsVisibleTo on this
// one and would otherwise see two Assert types in scope.
//
// The message parameter is an [InterpolatedStringHandler], the same
// pattern Lexer.Trace uses. When the condition holds, the compiler
// skips every AppendLiteral / AppendFormatted call inside the
// $"..." expression, so a call like:
//
//     Invariant.That(len > 0, $"NextTokenLength {len} at {_position}")
//
// pays zero formatting cost on the success path: no boxing of len, no
// ToString on _position, no StringBuilder allocation. The check
// collapses to a branch on `condition` and a ref-struct construction
// that the JIT folds into the caller. See InvariantInterpolatedStringHandler
// below for the rewrite mechanics.
internal static class Invariant
{
    // AggressiveInlining on the check, NoInlining on the throw. Same
    // pattern as Lexer.ThrowBudgetExceeded: a method that throws is
    // poison to the JIT's inliner, and pulling the throw into its own
    // method keeps the caller's hot path branch-only. The check
    // disappears into the caller when the condition holds, which is
    // every call.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void That(
        bool condition,
        [InterpolatedStringHandlerArgument(nameof(condition))]
        InvariantInterpolatedStringHandler message)
    {
        if (!condition)
            Throw(message.GetFormattedOrEmpty());
    }

    // Overload for callers whose message is a plain string literal or
    // a compile-time-folded constant like "a " + "b " + "c". Those
    // aren't $"..." interpolated string expressions, so the C# spec
    // won't route them through the handler. The constant is already a
    // single string object the runtime hands us by reference, so there
    // is nothing to defer. Resolution picks the handler overload for
    // any $"..." expression and this overload for everything else.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void That(bool condition, string message)
    {
        if (!condition)
            Throw(message);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Throw(string message)
    {
        throw new InductorParserBugException(message);
    }
}

// Interpolated-string handler for Invariant.That. The compiler sees
// $"..." passed to a parameter typed as this handler and rewrites the
// call to:
//
//     var handler = new InvariantInterpolatedStringHandler(literalLen, formattedCount, condition, out bool shouldAppend);
//     if (shouldAppend)
//     {
//         handler.AppendLiteral("NextTokenLength ");
//         handler.AppendFormatted(len);
//         handler.AppendLiteral(" at ");
//         handler.AppendFormatted(_position);
//     }
//     Invariant.That(condition, handler);
//
// The constructor sets shouldAppend=true only when condition is
// FALSE, i.e. when That is about to throw and needs the formatted
// message. Every other call shouldAppend=false, so the compiler skips
// every Append call: no boxing, no ToString, no StringBuilder. A
// plain string literal "foo" passed in lowers to AppendLiteral("foo")
// inside the same if-shouldAppend block and pays the same nothing.
//
// ref struct keeps the handler stack-only for the duration of the
// call, so the struct construction itself is free.
[InterpolatedStringHandler]
internal ref struct InvariantInterpolatedStringHandler
{
    private StringBuilder? _builder;

    public InvariantInterpolatedStringHandler(
        int literalLength,
        int formattedCount,
        bool condition,
        out bool shouldAppend)
    {
        if (condition)
        {
            // Common case: invariant holds, the message will never be
            // read, every Append is skipped.
            _builder = null;
            shouldAppend = false;
        }
        else
        {
            _builder = new StringBuilder(literalLength);
            shouldAppend = true;
        }
    }

    public void AppendLiteral(string value) => _builder?.Append(value);

    public void AppendFormatted<T>(T value) => _builder?.Append(value?.ToString());

    public void AppendFormatted(string? value) => _builder?.Append(value);

    public void AppendFormatted(ReadOnlySpan<char> value) => _builder?.Append(value);

    internal string GetFormattedOrEmpty()
    {
        var result = _builder?.ToString() ?? "";
        _builder = null;
        return result;
    }
}

// Thrown when an Invariant.That condition fails. Derives from
// Exception directly rather than from InvalidOperationException so a
// stray `catch (InvalidOperationException)` somewhere up the stack
// can't quietly swallow a bug report from this library. The
// user-API rejections (".As called twice", etc.) are still
// InvalidOperationException; this one is exclusively "InductorParser
// hit its own bug" and the type name says so.
public sealed class InductorParserBugException : Exception
{
    internal InductorParserBugException(string message)
        : base("InductorParser internal invariant violated: " + message
            + " This is a bug in InductorParser; please report it at "
            + "https://github.com/EricZinda/InductorCSParser/issues.")
    {
    }
}
