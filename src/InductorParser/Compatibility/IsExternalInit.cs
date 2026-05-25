// Polyfill for System.Runtime.CompilerServices.IsExternalInit.
//
// netstandard2.1 doesn't ship this type, so we polyfill. The #if gate keeps
// the polyfill out of the net8.0 build where the BCL provides the real one
// (it ships starting in .NET 5). `internal` prevents a public-surface
// conflict with the BCL type on downstream consumers. Without this polyfill,
// any file that uses `init` or a record struct fails to compile on the
// netstandard2.1 target with CS0518.
//
// C# 9's init-only setters (including the ones generated for record structs)
// emit a reference to IsExternalInit as a marker type. The type has no
// members. The compiler only checks that it exists.

#if !NET5_0_OR_GREATER

namespace System.Runtime.CompilerServices;

internal static class IsExternalInit { }

#endif
