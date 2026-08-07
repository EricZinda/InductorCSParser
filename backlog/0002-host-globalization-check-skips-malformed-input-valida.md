# Host globalization check skips malformed-input validation after first acceptance on invariant hosts

`HostGlobalizationCheck.EnsureRuntimeNormalizationIsTrustworthy` (src/InductorParser/Lexing/Unicode/HostGlobalizationCheck.cs line 99) returns at `if (_accepted) return;` without running `UnicodeNormalization.FindFirstUnnormalizableIndex`. The doc comment above it (lines 92-94) justifies the skip with "the runtime normalizer itself rejects bad input." That's true on ICU and NLS hosts, but under invariant globalization .NET's `string.Normalize` performs no validation at all (dotnet/runtime Normalization.cs: `if (GlobalizationMode.Invariant || Ascii.IsValid(strInput)) { return strInput; }`), and `IsNormalized` returns true unconditionally.

So in the configuration this class exists to police (Runtime implementation, invariant globalization host, `UnicodeEnvironment.AcceptHostGlobalization = true`), malformed-input behavior is call-order dependent: a lone surrogate or U+FFFE in the *first* normalizing parse throws the library's ArgumentException (the cold path still scans), but once any successful call has set `_accepted`, the same bad input passes through `string.Normalize` unchanged and reaches the lexer with no `MalformedInput` result. That contradicts the same doc block's "argument validation wins over host state" (lines 86-88), and Lexer.cs lines 31-36 ("under a normalizing grammar malformed input never reaches the lexer at all"). The commit message of c0b3553 repeats the false sentence (leave the commit alone, fix the live docs). Unity's Mono behaves the same way if a netstandard2.1 host explicitly selects Runtime, per the note in UnicodeNormalization.cs lines 100-102.

Severity is low because it needs the explicit opt-in, but the nondeterminism is real: the same input either throws or silently parses depending on what parsed before it.

Found during the 2026-07-28 Unicode claims audit.

## Verify the Bug (Write Test First)

This needs an invariant-globalization process, which is what the GlobalizationChild harness exists for. Add a scenario to the child project (src/InductorParser.Tests, HostGlobalizationChildProcessTests plus the GlobalizationChild program) that runs with `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` and, inside the child:

1. sets `UnicodeEnvironment.Implementation = UnicodeImplementation.Runtime` and `UnicodeEnvironment.AcceptHostGlobalization = true`
2. parses a clean input with a normalizing grammar (this sets `_accepted`)
3. parses `"a" + '\uD800' + "b"` with the same grammar and reports the outcome

Expected: `MalformedInput` at index 1. Today: the stray surrogate reaches the lexer and the parse reports an ordinary result. Also assert the mirror ordering (bad input first throws/reports MalformedInput) so the order dependence itself is documented by the pair.

Run: `./test.sh --filter "FullyQualifiedName~HostGlobalizationChildProcess"`.

## Fix

Decide between two shapes before coding:

- Keep the one-read fast path only for hosts where the claim is true: set `_accepted` on the RuntimeNormalizes verdict as today, but when acceptance came from the `AcceptHostGlobalization` opt-in on a degraded host, keep running `FindFirstUnnormalizableIndex` on every call (a second flag, or fold the status into the fast-path check). The scan is O(n) on a path that's about to run a full normalize pass anyway.
- Or always scan before the fast return, dropping the "one volatile bool read" property entirely.

The first shape preserves the hot-path promise for every normal configuration and is probably right. Either way, update the doc comment at lines 92-94, the "argument validation wins over host state" sentence stays true once the scan always runs, and soften Lexer.cs lines 31-36 only if the first shape is rejected.

## Verify the Fix

Rerun the child-process filter, both orderings report `MalformedInput` at the surrogate. Full suite green: `./test.sh`.
