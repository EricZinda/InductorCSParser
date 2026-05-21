// Polyfill for System.Runtime.CompilerServices.InterpolatedStringHandlerArgumentAttribute.
//
// Same story as InterpolatedStringHandlerAttribute.cs: netstandard2.1
// doesn't ship this type, so we polyfill. The #if gate keeps the polyfill
// out of the net8.0 build where the BCL provides the real one. `internal`
// prevents a public-surface conflict with the BCL type on downstream
// consumers.
//
// Placed on a method parameter that's a struct marked with
// [InterpolatedStringHandler], this attribute names other parameters
// (or "" for the receiver) whose values get forwarded into the
// handler's constructor when the C# compiler rewrites a $"..."
// value passed to that argument.
//
// Concrete example from this library. Lexer.Trace declares:
//
//     void Trace(TraceLevel level, string label, TraceOutcome outcome,
//         [InterpolatedStringHandlerArgument("", nameof(level))]
//         TraceInterpolatedStringHandler message)
//
// A call like:
//
//     lexer.Trace(TraceLevel.Diagnostic, "And", TraceOutcome.Success,
//                 $"found {count}")
//
// gets rewritten to:
//
//     var handler = new TraceInterpolatedStringHandler(
//         6,                       // length of the literal pieces ("found ")
//         1,                       // number of {...} holes
//         lexer,                   // "" -> the receiver (this)
//         TraceLevel.Diagnostic,   // nameof(level) -> the level arg
//         out bool shouldAppend);
//     if (shouldAppend)
//     {
//         handler.AppendLiteral("found ");
//         handler.AppendFormatted(count);
//     }
//     lexer.Trace(TraceLevel.Diagnostic, "And", TraceOutcome.Success, handler);
//
// The literal length and hole count are computed at compile time from the
// shape of the $"..." expression and are always passed. This attribute
// adds the rest: the receiver and the level arg. Without it, the handler
// couldn't see the lexer or the level and couldn't decide whether to skip
// the formatting.
//
// Both the (string) and (params string[]) constructors exist because the
// C# compiler emits whichever one matches the number of arguments
// declared on the target parameter.

#if !NET6_0_OR_GREATER

namespace System.Runtime.CompilerServices;

[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
internal sealed class InterpolatedStringHandlerArgumentAttribute : Attribute
{
    public string[] Arguments { get; }

    public InterpolatedStringHandlerArgumentAttribute(string argument)
    {
        Arguments = new[] { argument };
    }

    public InterpolatedStringHandlerArgumentAttribute(params string[] arguments)
    {
        Arguments = arguments;
    }
}

#endif
