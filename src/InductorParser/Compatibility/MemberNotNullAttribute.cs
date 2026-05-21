// Polyfill for System.Diagnostics.CodeAnalysis.MemberNotNullAttribute.
//
// netstandard2.1 doesn't ship this type, so we polyfill. The #if gate keeps
// the polyfill out of the net8.0 build where the BCL provides the real one
// (it ships starting in .NET 5). `internal` prevents a public-surface
// conflict with the BCL type on downstream consumers. Without this polyfill,
// code that uses the attribute fails to compile on the netstandard2.1 target
// with CS0246.
//
// C# 9's nullable-flow-analysis can be told that a method initializes
// non-nullable fields by tagging the method with [MemberNotNull(...)]. The
// compiler reads the attribute by name and shape only. The runtime never
// sees it.

#if !NET5_0_OR_GREATER

namespace System.Diagnostics.CodeAnalysis;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, Inherited = false, AllowMultiple = true)]
internal sealed class MemberNotNullAttribute : Attribute
{
    public string[] Members { get; }

    public MemberNotNullAttribute(string member) { Members = new[] { member }; }
    public MemberNotNullAttribute(params string[] members) { Members = members; }
}

#endif
