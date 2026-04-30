// Polyfill for System.Runtime.CompilerServices.InterpolatedStringHandlerArgumentAttribute.
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
// A call like
//
//     lexer.Trace(TraceLevel.Diagnostic, "AllOf", TraceOutcome.Success,
//                 $"found {count}")
//
// gets rewritten to
//
//     var handler = new TraceInterpolatedStringHandler(
//         6,                       // length of the literal pieces ("found ")
//         1,                       // number of {...} holes
//         lexer,                   // "" -> the receiver (this)
//         TraceLevel.Diagnostic,   // nameof(level) -> the level arg
//         out bool shouldAppend);  // TraceInterpolatedStringHandler sets this to tell the
//                                  //   compiler whether to run the
//                                  //   AppendLiteral/AppendFormatted
//                                  //   calls below
//     if (shouldAppend)
//     {
//         handler.AppendLiteral("found ");
//         handler.AppendFormatted(count);
//     }
//     lexer.Trace(TraceLevel.Diagnostic, "AllOf", TraceOutcome.Success, handler);
//
// The first two arguments (length of the literal pieces and number
// of holes) are computed at compile time from the shape of the
// $"..." expression itself and are always passed. This attribute
// adds the third and fourth: the receiver and the `level` argument.
// Without it, the handler wouldn't have access to the lexer or the
// level and couldn't decide whether to skip the formatting.
//
// Note: the rewritten code still calls lexer.Trace(handler) even
// when tracing is off. The if (shouldAppend) block only gates the
// expensive AppendLiteral/AppendFormatted work, not the method
// invocation. See the cost-when-off comment block on Lexer.Trace
// for why the call itself stays cheap.
//
// Same story as InterpolatedStringHandlerAttribute.cs:
// netstandard2.1 doesn't ship this type, so we polyfill. The #if
// gate keeps the polyfill out of the net8.0 build where the BCL
// provides the real one. Marking it `internal` prevents public
// surface conflicts with the BCL type on downstream consumers.
//
// Both the (string) and (string[]) constructors exist because the
// C# compiler emits whichever one matches the number of arguments
// declared on the target parameter.

#if !NET6_0_OR_GREATER

namespace System.Runtime.CompilerServices;

[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
internal sealed class InterpolatedStringHandlerArgumentAttribute : Attribute
{
    public InterpolatedStringHandlerArgumentAttribute(string argument)
    {
        Arguments = new[] { argument };
    }

    public InterpolatedStringHandlerArgumentAttribute(params string[] arguments)
    {
        Arguments = arguments;
    }

    public string[] Arguments { get; }
}

#endif
