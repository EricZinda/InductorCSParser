// Polyfill for System.Runtime.CompilerServices.IsExternalInit.
//
// C# 9's init-only setters (including the ones generated for record structs)
// emit a reference to IsExternalInit as a marker type. The BCL ships this
// type starting in .NET 5, but netstandard2.1 doesn't include it. Without
// this polyfill, any file that uses `init` or a record struct fails to
// compile on the netstandard2.1 target with CS0518.
//
// The type has no members; the compiler only checks that it exists.
// Marking it `internal` keeps it out of the public surface so callers
// don't see two IsExternalInit types (ours plus the BCL's on net5+).
//
// The `#if !NET5_0_OR_GREATER` gate means this compiles to nothing on
// the net8.0 target, where the BCL provides the real one.

#if !NET5_0_OR_GREATER

namespace System.Runtime.CompilerServices;

internal static class IsExternalInit { }

#endif
