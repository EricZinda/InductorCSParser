using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;

namespace InductorParser;

/// <summary>
/// Internal invariant checks for rule writers: "this can't happen, but if it does, stop right
/// here" conditions such as a helper's postcondition, a loop bound that depends on an earlier
/// computation, or an agreement between two pieces of code about what each may hand the other.
/// A failed check throws <see cref="InductorParserBugException"/>. The checks run in Release as
/// well as Debug, because a never-taken branch costs less than silently producing a wrong parse
/// when an invariant does break. The built-in rules use this, and a user-defined
/// <see cref="Rule"/> can assert its own invariants the same way.
/// </summary>
/// <remarks>
/// This isn't for errors a grammar author can cause by using the API wrong. Calling
/// <see cref="Rule.As(string)">Rule.As</see> twice or parsing with an unbound
/// <see cref="LateBoundRule"/> are bugs in the grammar, and they throw
/// InvalidOperationException with a message that points at the fix. Invariant is for the other
/// direction: a condition that should never fail no matter how the API is used, meaning a bug in
/// the code that declared the invariant. The two exception types are how you tell the cases
/// apart.
/// <para>
/// The message is an interpolated string that costs nothing when the condition holds. Write
/// <c>Invariant.That(length &gt; 0, $"NextTokenLength {length} at {position}")</c> and the
/// formatting, boxing, and string building only happen on the failure path. See
/// <see cref="InvariantInterpolatedStringHandler"/> for how.
/// </para>
/// </remarks>
public static class Invariant
{
    /// <summary>
    /// Throws <see cref="InductorParserBugException"/> with <paramref name="message"/> when
    /// <paramref name="condition"/> is false. The message is built only on that path, so put
    /// whatever state would help debug the failure into the interpolation holes freely.
    /// </summary>
    /// <param name="condition">The invariant. True means everything is fine.</param>
    /// <param name="message">An interpolated string describing what broke, formatted only if the check fails.</param>
    // AggressiveInlining on the check, NoInlining on the throw. Same
    // pattern as ParseBudget.ThrowBudgetExceeded: a method that throws is
    // poison to the JIT's inliner, and pulling the throw into its own
    // method keeps the caller's hot path branch-only. The check
    // disappears into the caller when the condition holds, which is
    // every call. (RyuJIT won't inline a throw-only method anyway and
    // moves the call to a cold region: see dotnet/runtime#4381.)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void That(
        bool condition,
        [InterpolatedStringHandlerArgument(nameof(condition))]
        InvariantInterpolatedStringHandler message)
    {
        if (!condition)
            Throw(message.GetFormattedOrEmpty());
    }

    /// <summary>
    /// Throws <see cref="InductorParserBugException"/> with <paramref name="message"/> when
    /// <paramref name="condition"/> is false. This overload takes a plain string, for a message
    /// with nothing to format. Overload resolution picks it for any argument that isn't an
    /// interpolated string.
    /// </summary>
    /// <param name="condition">The invariant. True means everything is fine.</param>
    /// <param name="message">What broke.</param>
    // A plain string literal or a compile-time-folded constant like
    // "a " + "b " + "c" isn't a $"..." expression, so the C# spec won't
    // route it through the handler. The constant is already a single
    // string object the runtime hands us by reference, so there is
    // nothing to defer.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void That(bool condition, string message)
    {
        if (!condition)
            Throw(message);
    }

    /// <summary>
    /// Builds, without throwing, the <see cref="InductorParserBugException"/> for a branch that
    /// should be unreachable: a switch default that can't be hit, an exhaustive if/else, a case
    /// the type system can't rule out but the logic can. Write <c>throw Invariant.Fail("...")</c>
    /// so the compiler's flow analysis sees the throw and doesn't demand a return after it.
    /// </summary>
    /// <param name="message">What would have to be true for this branch to run.</param>
    // Returning the exception and throwing at the call is what keeps
    // definite-return analysis happy. A [DoesNotReturn] void method
    // wouldn't: C# only uses that attribute for nullable flow, not for
    // definite return, so a switch default would still need a dummy
    // return. Plain string, not the deferred handler That uses, since an
    // unreachable branch never runs and so never pays the formatting cost.
    public static Exception Fail(string message) =>
        new InductorParserBugException(message);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Throw(string message)
    {
        throw new InductorParserBugException(message);
    }
}

/// <summary>
/// The interpolated-string handler behind <see cref="Invariant.That(bool, InvariantInterpolatedStringHandler)">Invariant.That</see>.
/// You never use it directly: passing a <c>$"..."</c> string to That makes the compiler build one
/// of these and route each literal and each hole through it. The handler formats nothing while the
/// condition holds, which is what lets an invariant message include positions, lengths, and rule
/// names at no cost on the success path.
/// </summary>
/// <remarks>
/// The compiler rewrites <c>Invariant.That(condition, $"at {position}")</c> to construct the
/// handler with the condition, check the <c>shouldAppend</c> flag it returns, and only then call
/// <see cref="AppendLiteral">AppendLiteral</see> and <see cref="AppendFormatted{T}(T)">AppendFormatted</see>.
/// The constructor sets that flag only when the condition is false, so on every passing check
/// the appends are skipped entirely: no boxing, no ToString, no string builder. It's a ref struct
/// so it lives on the stack for the duration of the call.
/// </remarks>
[InterpolatedStringHandler]
public ref struct InvariantInterpolatedStringHandler
{
    private StringBuilder? _builder;

    /// <summary>
    /// Called by the compiler, not by user code. Sets <paramref name="shouldAppend"/> to true only
    /// when <paramref name="condition"/> is false, so the message is formatted only when the check
    /// is about to fail.
    /// </summary>
    /// <param name="literalLength">The total length of the literal parts, supplied by the compiler.</param>
    /// <param name="formattedCount">The number of interpolation holes, supplied by the compiler.</param>
    /// <param name="condition">The invariant being checked.</param>
    /// <param name="shouldAppend">Whether the compiler should run the append calls.</param>
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

    /// <summary>Called by the compiler for each literal piece of the message.</summary>
    public void AppendLiteral(string value) => _builder?.Append(value);

    /// <summary>Called by the compiler for each interpolation hole of the message.</summary>
    public void AppendFormatted<T>(T value) => _builder?.Append(value?.ToString());

    /// <summary>Called by the compiler for a string-typed interpolation hole.</summary>
    public void AppendFormatted(string? value) => _builder?.Append(value);

    /// <summary>Called by the compiler for a span-typed interpolation hole.</summary>
    public void AppendFormatted(ReadOnlySpan<char> value) => _builder?.Append(value);

    internal string GetFormattedOrEmpty()
    {
        var result = _builder?.ToString() ?? "";
        _builder = null;
        return result;
    }
}

/// <summary>
/// Thrown when an <see cref="Invariant.That(bool, string)">Invariant.That</see> check fails or
/// <see cref="Invariant.Fail">Invariant.Fail</see> is thrown. It means an invariant that should never
/// fail did, whether InductorParser itself or a user-defined rule declared it, so it's a bug in that
/// code rather than in the grammar or the input. Its message starts with "Invariant violated:".
/// </summary>
/// <remarks>
/// Derives from Exception directly rather than from InvalidOperationException, so a
/// <c>catch (InvalidOperationException)</c> somewhere up the stack can't quietly swallow it. The
/// errors a grammar author can cause, such as calling <see cref="Rule.As(string)">Rule.As</see>
/// twice, stay InvalidOperationException. Catching this type is how a test tells the two apart.
/// </remarks>
public sealed class InductorParserBugException : Exception
{
    internal InductorParserBugException(string message)
        : base("Invariant violated: " + message
            + " This is an invariant assertion that should never happen.")
    {
    }
}
