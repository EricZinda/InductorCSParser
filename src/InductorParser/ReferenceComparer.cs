using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace InductorParser;

// IEqualityComparer that compares by object identity (the literal pointer)
// rather than by anything T's Equals or GetHashCode might say. Hand it to
// a HashSet<T> or Dictionary<T,...> when you want "have I seen this exact
// object before?" semantics, regardless of whether T overrides equality
// for some other reason.
//
// We use it in Rule.Compile to walk the rule graph without revisiting
// nodes. The graph can have shared subtrees (the same Rule reachable
// through multiple parents), and the dedupe has to be by reference: if
// someone ever overrides Rule.Equals to do structural comparison, the
// graph walk would silently skip nodes that "look the same" but are
// distinct instances. Forcing reference equality locks in the
// "node identity == object identity" invariant the walk needs.
//
// The static Instance is the stateless-singleton idiom. The comparer
// holds no state (both methods just delegate to runtime helpers), so
// one instance per generic instantiation does all the work. Allocating
// a fresh comparer at each call site would be pure waste.
//
// This is a polyfill for System.Collections.Generic.ReferenceEqualityComparer,
// which the BCL ships in .NET 5.0+ but not in netstandard2.1. When the
// library drops netstandard2.1 this file can be deleted in favor of the
// BCL type.
internal sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
{
    public static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();
    public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
    public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
}
