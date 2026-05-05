// Polyfill for System.Diagnostics.CodeAnalysis.MemberNotNullAttribute.
//
// C# 9's nullable-flow-analysis can be told that a method initializes
// non-nullable fields by tagging the method with [MemberNotNull(...)].
// The BCL ships this attribute starting in .NET 5, but netstandard2.1
// doesn't include it. Without this polyfill, code that uses the
// attribute fails to compile on the netstandard2.1 target with CS0246.
//
// The compiler reads the attribute by name and shape only; the
// runtime never sees it. Marking it `internal` keeps it out of the
// public surface so callers don't see two MemberNotNullAttributes
// (ours plus the BCL's on net5+).
//
// The `#if !NET5_0_OR_GREATER` gate means this compiles to nothing on
// the net8.0 target, where the BCL provides the real one.

#if !NET5_0_OR_GREATER

namespace System.Diagnostics.CodeAnalysis;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, Inherited = false, AllowMultiple = true)]
internal sealed class MemberNotNullAttribute : Attribute
{
    public MemberNotNullAttribute(string member) { Members = new[] { member }; }
    public MemberNotNullAttribute(params string[] members) { Members = members; }
    public string[] Members { get; }
}

#endif
