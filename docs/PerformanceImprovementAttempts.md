# Performance Improvement Attempts

Log of performance experiments on InductorParser. Each section is a self-contained record of one attempt: what was tried, how it was measured, what the numbers came back as, and whether the change shipped or was reverted. Append new attempts to the bottom over time.

Shipped optimizations live in the code, in the p-series commit history, and in the running commentary in [ChordGrammarTests.cs](../src/InductorParser.Tests/E2EExamples/ChordGrammarTests.cs) (the "History on this box" comment above the timing test). This file is for attempts worth remembering even when the implementation didn't land — so the next contributor working on the same target doesn't redo the same experiment.

---

## P700: Discard Propagation + Empty-Flatten Elision (reverted)

Engineering record of an attempt at backlog item p700 ("Reduce per-iteration allocations on the matching path"). The changes were written, measured, and then reverted.

### What p700 asked for

From [backlog/p700-reduce-per-iteration-allocations-on-the-matching-p.md](../backlog/p700-reduce-per-iteration-allocations-on-the-matching-p.md): three sub-levers were proposed.

1. Leaf Symbol interning on CharRule / RuneInRule.
2. A `SymbolChildren` struct to avoid the `List<Symbol>` / `Symbol[]` dichotomy.
3. Compile-time rewrite of `Or(CharRule, CharRule, ...)` → `RuneInRule`.

None of those three were implemented. The attempt below is a fourth approach that emerged from reading the hot path.

### What was actually tried

Two linked changes, both targeting wrapper allocations rather than leaf allocations.

**Discard propagation.** The existing parse-time Delete filter (p100) lets a rule whose `FlattenType` is Delete return `Symbol.Discarded` without allocating. But when the parent is discarding, the child still allocates: the existing flow computes `discard` from the child's own `FlattenType` only.

Added `Rule.TryParseDiscarded(Lexer)` as a second entry point alongside `TryParse(Lexer)`. The discarded path always runs the subclass with `discard: true` and returns `Symbol.Discarded`. AndRule, BetweenInclusiveRule, and OrRule branch at the call site based on their own `discard` flag:

```csharp
var symbol = discard ? child.TryParseDiscarded(lexer) : child.TryParse(lexer);
```

NotRule and PeekRule always go through `TryParseDiscarded` because they roll the transaction back and throw the inner's Symbol away regardless. LateBoundRule propagates `discard` through to its target.

The two entry points keeps the !discard path at exactly the same cost as before (one branch at the composite rule, not per-invocation in the hot path). That detail matters — an early version of this change added a `forceDiscard` parameter to the single `TryParse` method and produced a measurable (~2%) regression on JSON because every rule invocation paid the extra `||` even when nothing was discarding.

**Anonymous empty-Flatten wrapper elision.** When `AndRule` or `BetweenInclusiveRule` runs to completion with every child returning `Discarded` (so `matched` stays `null`), the default behavior is to allocate `new Symbol(Id, Flatten, Array.Empty<Symbol>())`. Post-hoc `Symbol.FlattenInto` would splice that zero-child wrapper to nothing anyway, so at parse time we can return `Discarded` directly and let the enclosing composite filter us out of its matched list.

The elision only fires for **anonymous** wrappers: `Name == null && ErrorMessage == null && FlattenType == Flatten && !PreserveFlattenWrappers`. A user who called `.As("object")` on an AndRule wants that wrapper findable via `Tree.Find(rule)` even when the matched container is empty (think `{}` in JSON), so named wrappers are preserved. This gate is load-bearing — an earlier, ungated version broke JSON empty-object parsing silently because `JsonObject = And(...).As("object")` collapsed to Discarded on `{}` inputs, and nothing in the benchmark's round-trip path caught it.

### Files touched (in the attempt)

- [src/InductorParser/Rule.cs](../src/InductorParser/Rule.cs) — added `TryParseDiscarded`.
- [src/InductorParser/AndRule.cs](../src/InductorParser/AndRule.cs) — discard propagation + empty-wrapper elision.
- [src/InductorParser/BetweenInclusiveRule.cs](../src/InductorParser/BetweenInclusiveRule.cs) — same.
- [src/InductorParser/OrRule.cs](../src/InductorParser/OrRule.cs) — discard propagation.
- [src/InductorParser/NotRule.cs](../src/InductorParser/NotRule.cs) — always force-discard inner.
- [src/InductorParser/PeekRule.cs](../src/InductorParser/PeekRule.cs) — same.
- [src/InductorParser/LateBoundRule.cs](../src/InductorParser/LateBoundRule.cs) — propagate discard to target.

Test surface: all 458 non-timing tests continued to pass. The round-trip spot-check (`dotnet run --project src/Benchmarks -- --spot-check`) confirmed no tree-shape regressions across Big / Long / Deep / Wide after the elision was gated.

### Measurements

Measurement protocol: stash the working tree, build, run baseline; pop, build, run after. Each timing test ran three times per side because the short-corpus wall-clock tests swing ~2x between runs.

#### ChordGrammar timing (5000 iters × 151 inputs, median of 3 runs)

```
                Grammar (ms)    Ratio vs compiled regex
Baseline        622             10.48x
After           605              9.51x
```

~3% faster on Grammar absolute time. The ratio moved from ~10x to ~9.5x but regex baseline time also drifts run-to-run (59-71ms range observed), so the ratio is the noisier number.

#### BacklogGrammar per-case ratios (noisy short corpora, observed range over 3 runs)

```
              Baseline range    After range
H1            25-44x            41-47x
H2            22-28x            26-28x
Bullet        5-10x             5-16x
HrRun         5-7x              2-6x
HrSpaced      9-10x             7-10x
Paragraph     18-28x            15-19x
```

Within noise for most cases. HrRun, HrSpaced, and Paragraph showed modest improvement; the others landed in the same range as baseline.

#### JSON benchmark (BenchmarkDotNet ShortRunJob, 3 iterations)

```
Shape   Baseline Mean → After       Baseline Alloc → After       Δ Alloc
Big     290.98 → 292.36 μs          363.23 → 363.23 KB              0%
Deep    186.17 → 192.74 μs          208.68 → 194.63 KB           -6.7%
Long    216.54 → 221.15 μs          299.09 → 285.09 KB           -4.7%
Wide    143.46 → 145.43 μs          179.77 → 179.72 KB              0%
```

Mean drifted +0.5% to +3.5% across shapes. StdDev on those runs was 0.4-3.0 μs, so the Mean shifts are at the edge of single-run noise — plausibly real, plausibly not. Allocation is the clean signal: Deep and Long dropped ~5-7%, Big and Wide unchanged.

### Why the wins are small

ChordGrammar's hot path has seven `Optional(keyword)` and `ZeroOrMore(keyword)` wrappers per successful chord parse, each of which produced an empty Flatten wrapper under the baseline. The elision collapses those to Discarded. On paper that should be ~7 Symbol allocations saved per parse × 750,000 parses = ~5.25M Symbols saved. The test numbers say ~3% wall-clock, which suggests those Symbol allocations weren't the dominant cost — the transactions and per-child Rule dispatch are.

JSON's wins are concentrated on Deep and Long because those shapes have more empty-optional wrappers per parse (256 levels of empty `OptionalWhitespace()` and empty-body Optionals for the innermost object). Big and Wide have proportionally more real content, so the savings don't register above noise.

The p700 backlog item predicted that leaf Symbol interning (sub-lever a) or the `Or(Char, Char) → RuneIn` rewrite (sub-lever c) would be bigger wins. This attempt targeted a fourth surface (wrapper allocations on composite rules) and the ceiling looks lower than the leaf-allocation ceiling those sub-levers target.

### Why the attempt was reverted

The net gain was under the threshold the user wanted to carry as permanent code complexity. The new `TryParseDiscarded` entry point doubles the Rule-to-Rule dispatch surface (two methods where there was one), and the anonymous-wrapper elision needs a load-bearing gate on `Name == null && ErrorMessage == null` that a future contributor could easily miss when adding a new modifier method or a new wrapper rule.

For ~3% on Chord and ~5% allocation on JSON Deep/Long, the complexity didn't carry its weight. The p700 backlog item is still open; a future attempt should pick one of the three originally-proposed sub-levers (leaf Symbol interning, SymbolChildren struct, or compile-time `Or(Char, ...)` → `RuneIn` rewrite) where the allocation ceiling is higher.

### What a future attempt should reuse

The measurement scaffolding in this doc is correct even if the implementation isn't. Specifically:

- Un-ignore both `Timing_grammar_is_within_two_times_compiled_regex` tests temporarily. Run each three times per side. The Grammar absolute time is more stable than the ratio because the compiled regex baseline itself swings ~20% run-to-run.
- For JSON, run `dotnet run --project src/Benchmarks -- --filter "*_InductorParser" "*_SystemTextJson"` to measure just the two rows that matter. The filtered run still takes ~2 minutes but is much cheaper than the full 35-row table.
- Validate with `dotnet run --project src/Benchmarks -- --spot-check` after any change that touches Symbol construction — it catches tree-shape regressions that don't show up in the tests because `ParseForRoundTrip` uses `PreserveFlattenWrappers=true`.
- Empty-Flatten wrapper elision is a semantic change that needs the anonymous-only gate. Any future change in that direction has to thread the Name / ErrorMessage / pinned-Id checks through, or it'll silently break `Tree.Find(namedRule)` on empty containers.
