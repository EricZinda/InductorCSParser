// Polyfill for System.Runtime.CompilerServices.InterpolatedStringHandlerAttribute.
//
// netstandard2.1 doesn't ship this type, so we polyfill. The #if gate keeps
// the polyfill out of the net8.0 build where the BCL provides the real one
// (it ships starting in .NET 6). `internal` prevents a public-surface
// conflict with the BCL type on downstream consumers. Without this polyfill,
// TraceInterpolatedStringHandler fails to compile on the netstandard2.1
// target with CS0246.
//
// C# 10's interpolated string handler feature requires this marker attribute
// on the handler struct so the compiler knows to rewrite $"..." calls into
// AppendLiteral/AppendFormatted calls. It's a pure compile-time marker:
// the CLR never inspects it, so polyfilling behaves the same as the BCL's
// type as far as the C# compiler is concerned.

#if !NET6_0_OR_GREATER

namespace System.Runtime.CompilerServices;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
internal sealed class InterpolatedStringHandlerAttribute : Attribute
{
    public InterpolatedStringHandlerAttribute() { }
}

#endif
