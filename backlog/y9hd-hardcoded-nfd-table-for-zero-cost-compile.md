# Hardcoded NFD table for zero-cost Compile under non-null normalization

Follow-on to the global NFD probe table backlog item. Once that ships, the first non-null Compile in a process still pays a 1-3 second one-time cost to build the table by walking the BCL across all valid runes. For applications that Compile many grammars (a regex/PEG playground, a language server, a code formatter), that first-call latency is visible.

The fix is to move the table from runtime-built to compile-time-baked: ship a generated static array (rune key, NFD value) as part of `InductorParser.dll`. The first Compile reads the table from data section; no BCL walk, no runtime allocation. Latency is sub-millisecond.

The maintenance cost is tracking the deployed BCL's Unicode version. A new Unicode revision (e.g., 16.0 → 17.0) introduces new code points whose NFD mapping the hardcoded table doesn't know about. The mitigation: ship a CI check that compares the hardcoded table against the BCL's `string.Normalize` on every supported framework target (net8.0, net9.0, net10.0, ...) and fails the build if they diverge. When .NET upgrades its Unicode version, the CI check fires and a new table generation pass is needed.

## Verify the cost

The y9pt backlog item will already have a `Compile_with_universe_set_finishes_within_two_seconds_first_call` test that pins the runtime-probe-table behavior at sub-2-seconds. Tighten this bound to sub-50ms after the hardcoded table is in place, since there's no probe walk:

```csharp
[Test]
public void Compile_first_call_with_hardcoded_table_is_fast()
{
    var rule = OneOf(TokenSet.Universe);
    var watch = System.Diagnostics.Stopwatch.StartNew();
    rule.Compile();
    watch.Stop();
    Assert.That(watch.ElapsedMilliseconds, Is.LessThan(50),
        $"first Compile with hardcoded table took {watch.ElapsedMilliseconds}ms");
}
```

## Fix

Generate the table via a `tools/GenerateNfdTable/` console project that walks every valid rune via `string.IsNormalized(text, NormalizationForm.FormD)` / `text.Normalize(NormalizationForm.FormD)` and emits a C# source file with a `static readonly Dictionary<int, string>` (or a sorted pair-array with binary-search lookup, depending on size and access pattern). Check the generated file into `src/InductorParser/Generated/NfdTable.g.cs`.

CI safety: a build-time test that walks every valid rune through `string.Normalize` and confirms the hardcoded table matches. If a future BCL update changes a single rune's mapping, the CI test fires the alarm before users see a divergence.

## Out of scope

NFC composition is data-dependent on the surrounding context; can't be precomputed per-rune. NFC stays on the BCL path. Same for NFKC / NFKD compatibility folds. Only NFD lookup gets the hardcoded-table treatment.
