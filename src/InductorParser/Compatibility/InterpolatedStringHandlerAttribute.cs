// Polyfill for System.Runtime.CompilerServices.InterpolatedStringHandlerAttribute.
//
// C# 10's interpolated string handler feature requires this marker
// attribute on the handler struct so the C# compiler knows to rewrite
// $"..." call sites into AppendLiteral/AppendFormatted calls. The BCL
// ships this type starting in .NET 6, but netstandard2.1 doesn't
// include it. Without this polyfill, TraceInterpolatedStringHandler
// fails to compile on the netstandard2.1 target with CS0246.
//
// The attribute is a pure compile-time marker: the CLR never inspects
// it, so polyfilling is byte-for-byte equivalent to the BCL's type as
// far as the C# compiler is concerned. Marking it `internal` keeps it
// out of the public surface so callers don't see two
// InterpolatedStringHandlerAttribute types (ours plus the BCL's on
// net6+).
//
// The #if gate means this compiles to nothing on the net8.0 target,
// where the BCL provides the real one.

#if !NET6_0_OR_GREATER

namespace System.Runtime.CompilerServices;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
internal sealed class InterpolatedStringHandlerAttribute : Attribute
{
    public InterpolatedStringHandlerAttribute() { }
}

#endif
