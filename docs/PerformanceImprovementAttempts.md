# Performance Improvement Attempts

Log of performance experiments on InductorParser. Each section is a self-contained record of one attempt: what was tried, how it was measured, what the numbers came back as, and whether the change shipped or was reverted. Append new attempts to the bottom over time.

Shipped optimizations live in the code, in the p-series commit history, and in the running commentary in [ChordGrammarTests.cs](../src/InductorParser.Tests/E2EExamples/ChordGrammarTests.cs) (the "History on this box" comment above the timing test). This file is for attempts worth remembering even when the implementation didn't land, so the next contributor working on the same target doesn't redo the same experiment.

---

## P700: Discard Propagation + Empty-Flatten Elision (reverted)

Engineering record of an attempt at backlog item p700 ("Reduce per-iteration allocations on the matching path"). The changes were written, measured, and then reverted.

### What p700 asked for

From [backlog/p700-reduce-per-iteration-allocations-on-the-matching-p.md](../backlog/p700-reduce-per-iteration-allocations-on-the-matching-p.md): three sub-levers were proposed.

1. Leaf Symbol interning on TokenRule / OneOfRule.
2. A `SymbolChildren` struct to avoid the `List<Symbol>` / `Symbol[]` dichotomy.
3. Compile-time rewrite of `FirstOf(TokenRule, TokenRule, ...)` → `OneOfRule`.

None of those three were implemented. The attempt below is a fourth approach that emerged from reading the hot path.

### What was actually tried

Two linked changes, both targeting wrapper allocations rather than leaf allocations.

**Discard propagation.** The existing parse-time Delete filter (p100) lets a rule whose `FlattenType` is Delete return `Symbol.Discarded` without allocating. But when the parent is discarding, the child still allocates: the existing flow computes `discard` from the child's own `FlattenType` only.

Added `Rule.TryParseDiscarded(Lexer)` as a second entry point alongside `TryParse(Lexer)`. The discarded path always runs the subclass with `discard: true` and returns `Symbol.Discarded`. AllOfRule, BetweenInclusiveRule, and FirstOfRule branch at the call based on their own `discard` flag:

```csharp
var symbol = discard ? child.TryParseDiscarded(lexer) : child.TryParse(lexer);
```

NotRule and PeekRule always go through `TryParseDiscarded` because they roll the transaction back and throw the inner's Symbol away regardless. LateBoundRule propagates `discard` through to its target.

The two entry points keeps the !discard path at exactly the same cost as before (one branch at the composite rule, not per-invocation in the hot path). That detail matters. An early version of this change added a `forceDiscard` parameter to the single `TryParse` method and produced a measurable (~2%) regression on JSON because every rule invocation paid the extra `||` even when nothing was discarding.

**Anonymous empty-Flatten wrapper removal.** When `AllOfRule` or `BetweenInclusiveRule` runs to completion with every child returning `Discarded` (so `matched` stays `null`), the default behavior is to allocate `new Symbol(Id, Flatten, Array.Empty<Symbol>())`. Post-hoc `Symbol.FlattenInto` would drop that zero-child wrapper to nothing anyway, so at parse time we can return `Discarded` directly and let the enclosing composite filter us out of its matched list.

The removal only fires for **anonymous** wrappers: `Name == null && ErrorMessage == null && FlattenType == Flatten && !PreserveAllSymbols`. A user who called `.As("object")` on an AllOfRule wants that wrapper findable via `Tree.Find(rule)` even when the matched container is empty (think `{}` in JSON), so named wrappers are preserved. This gate is critical. An earlier, ungated version broke JSON empty-object parsing silently because `JsonObject = AllOf(...).As("object")` collapsed to Discarded on `{}` inputs, and nothing in the benchmark's round-trip path caught it.

### Files touched (in the attempt)

- [src/InductorParser/Rule.cs](../src/InductorParser/Rule.cs): added `TryParseDiscarded`.
- [src/InductorParser/AllOfRule.cs](../src/InductorParser/AllOfRule.cs): discard propagation + empty-wrapper removal.
- [src/InductorParser/BetweenInclusiveRule.cs](../src/InductorParser/BetweenInclusiveRule.cs): same.
- [src/InductorParser/FirstOfRule.cs](../src/InductorParser/FirstOfRule.cs): discard propagation.
- [src/InductorParser/NotRule.cs](../src/InductorParser/NotRule.cs): always force-discard inner.
- [src/InductorParser/PeekRule.cs](../src/InductorParser/PeekRule.cs): same.
- [src/InductorParser/LateBoundRule.cs](../src/InductorParser/LateBoundRule.cs): propagate discard to target.

Test surface: all 458 non-timing tests continued to pass. The round-trip spot-check (`dotnet run --project src/Benchmarks -- --spot-check`) confirmed no tree-shape regressions across Big / Long / Deep / Wide after the removal was gated.

### Measurements

Measurement protocol: stash the working tree, build, run baseline. Pop, build, run after. Each timing test ran three times per side because the short-corpus wall-clock tests swing ~2x between runs.

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

Within noise for most cases. HrRun, HrSpaced, and Paragraph showed modest improvement. The others landed in the same range as baseline.

#### JSON benchmark (BenchmarkDotNet ShortRunJob, 3 iterations)

```
Shape   Baseline Mean → After       Baseline Alloc → After       Δ Alloc
Big     290.98 → 292.36 μs          363.23 → 363.23 KB              0%
Deep    186.17 → 192.74 μs          208.68 → 194.63 KB           -6.7%
Long    216.54 → 221.15 μs          299.09 → 285.09 KB           -4.7%
Wide    143.46 → 145.43 μs          179.77 → 179.72 KB              0%
```

Mean drifted +0.5% to +3.5% across shapes. StdDev on those runs was 0.4-3.0 μs, so the Mean shifts are at the edge of single-run noise, plausibly real, plausibly not. Allocation is the clean signal: Deep and Long dropped ~5-7%, Big and Wide unchanged.

### Why the wins are small

ChordGrammar's hot path has seven `Optional(keyword)` and `ZeroOrMore(keyword)` wrappers per successful chord parse, each of which produced an empty Flatten wrapper under the baseline. Removing them collapses those to Discarded. On paper that should be ~7 Symbol allocations saved per parse × 750,000 parses = ~5.25M Symbols saved. The test numbers say ~3% wall-clock, which suggests those Symbol allocations weren't the dominant cost. The transactions and per-child Rule dispatch are.

JSON's wins are concentrated on Deep and Long because those shapes have more empty-optional wrappers per parse (256 levels of empty `OptionalWhitespace()` and empty-body Optionals for the innermost object). Big and Wide have proportionally more real content, so the savings don't register above noise.

The p700 backlog item predicted that leaf Symbol interning (sub-lever a) or the `FirstOf(Token, Token) → OneOf` rewrite (sub-lever c) would be bigger wins. This attempt targeted a fourth surface (wrapper allocations on composite rules) and the ceiling looks lower than the leaf-allocation ceiling those sub-levers target.

### Why the attempt was reverted

The net gain was under the threshold the user wanted to carry as permanent code complexity. The new `TryParseDiscarded` entry point doubles the Rule-to-Rule dispatch surface (two methods where there was one), and the anonymous-wrapper removal needs a load-bearing gate on `Name == null && ErrorMessage == null` that a future contributor could easily miss when adding a new modifier method or a new wrapper rule.

For ~3% on Chord and ~5% allocation on JSON Deep/Long, the complexity didn't carry its weight. The p700 backlog item is still open. A future attempt should pick one of the three originally-proposed sub-levers (leaf Symbol interning, SymbolChildren struct, or compile-time `FirstOf(Token, ...)` → `OneOf` rewrite) where the allocation ceiling is higher.

### What a future attempt should reuse

The measurement scaffolding in this doc is correct even if the implementation isn't. Specifically:

- Un-ignore both `Timing_grammar_is_within_two_times_compiled_regex` tests temporarily. Run each three times per side. The Grammar absolute time is more stable than the ratio because the compiled regex baseline itself swings ~20% run-to-run.
- For JSON, run `dotnet run --project src/Benchmarks -- --filter "*_InductorParser" "*_SystemTextJson"` to measure just the two rows that matter. The filtered run still takes ~2 minutes but is much cheaper than the full 35-row table.
- Validate with `dotnet run --project src/Benchmarks -- --spot-check` after any change that touches Symbol construction. It catches tree-shape regressions that don't show up in the tests because `ParseForRoundTrip` uses `PreserveAllSymbols=true`.
- Empty-Flatten wrapper removal is a semantic change that needs the anonymous-only gate. Any future change in that direction has to carry the Name / ErrorMessage / pinned-Id checks through, or it'll silently break `Tree.Find(namedRule)` on empty containers.

---

## P700 revisited: Leaf Symbol Interning + Array-Backed Match Buffer (reverted)

Engineering record of the two p700 sub-levers the earlier attempt (logged above) didn't get to: leaf Symbol interning (sub-lever a) and replacing `List<Symbol>` with a plain `Symbol[]` in composite rules (a lighter-weight version of sub-lever b's "SymbolChildren struct"). Both written, measured, and reverted.

### What p700 sub-levers a and b asked for

From the retired p700 backlog item, three sub-levers were proposed originally. The earlier attempt logged above targeted a fourth surface (discard propagation + empty-wrapper removal) and didn't move the needle. This attempt returns to the first two originals:

1. (a) Leaf Symbol interning on `OneOfRule` / `NoneOfRule` / `AnyTokenRule`. Cache by (FlattenType, rune) so repeated matches of the same rune reuse one Symbol instance instead of allocating a fresh one per match.
2. (b) A replacement for the `List<Symbol>` that `AllOfRule` and `BetweenInclusiveRule` use to accumulate matched children. The backlog item sketched this as an inline-buffer struct. This attempt tried the lighter-weight version first: a plain `Symbol[]` with doubling growth.

Sub-lever (c) (compile-time `FirstOf(Token, Token, ...)` → `OneOf` rewrite) is still unimplemented. The JSON grammar already uses `OneOf` directly everywhere so there was no hot path to target in the current benchmark.

### What was actually tried

**Sub-lever (a): leaf Symbol interning.** Added a per-Lexer cache on `Lexer` with a direct-indexed `Symbol?[128]` fast path for ASCII and a `Dictionary<long, Symbol>` fallback keyed on `(FlattenType << 32) | rune`. `OneOfRule`, `NoneOfRule`, and `AnyTokenRule` route single-rune success symbols through the cache instead of allocating per match. Cached Symbols carry a fresh 1- or 2-char string as their backing memory, not a reference into the current input, so the cache does not keep the input string alive past the parse.

Scope choice: cache lives on the Lexer, not on the Rule. Rules are shared across threads and parses. A per-Rule cache would need a lock and would hold Memory references across parses. Per-Lexer keeps it single-threaded by contract (one Lexer per parse) and disposable with the parse.

**Sub-lever (b): array-backed match buffer.** Replaced `List<Symbol>?` with `Symbol[]?` in `AllOfRule` and `BetweenInclusiveRule`. AllOfRule sizes the buffer at `Children.Count` upfront (tight upper bound: each child produces at most one non-Discarded Symbol, so no growth is ever needed). BetweenInclusiveRule starts at 4 and doubles via `Array.Resize`. Both trim to exact size via `Array.Copy` at the end when the filled count is short of the buffer length. The saving is the `List<T>` header (~24 B per populated wrapper) that the old code paid for on top of its internal array.

Did not implement the full "SymbolChildren struct with 4 inline slots + overflow" design the p700 backlog sketched. An inline-slot struct would let the common case (2-3 matched children) skip the intermediate buffer allocation entirely, but it also makes caller code uglier and needs `Symbol.Children` to accept the struct instead of `IReadOnlyList<Symbol>`. Elected to measure the plain-array version first and only go bigger if the signal said yes.

### Files touched (in the attempt)

- [src/InductorParser/Lexing/Lexer.cs](../src/InductorParser/Lexing/Lexer.cs): added per-Lexer leaf Symbol cache and `GetOrCreateRuneLeaf`.
- [src/InductorParser/OneOfRule.cs](../src/InductorParser/OneOfRule.cs), [NoneOfRule.cs](../src/InductorParser/NoneOfRule.cs), [AnyTokenRule.cs](../src/InductorParser/AnyTokenRule.cs): route single-rune success through the cache.
- [src/InductorParser/AllOfRule.cs](../src/InductorParser/AllOfRule.cs): `List<Symbol>?` to `Symbol[]?` with trim-on-short.
- [src/InductorParser/BetweenInclusiveRule.cs](../src/InductorParser/BetweenInclusiveRule.cs): `List<Symbol>?` to `Symbol[]?` with doubling growth and trim-on-short.

Test surface: all 458 non-timing tests continued to pass. The round-trip spot-check (`dotnet run --project src/Benchmarks -- --spot-check`) confirmed byte-exact round-trip on all four JSON shapes.

### Measurements

BenchmarkDotNet ShortRunJob, 3 iterations per shape, on the same box as the P700 and P600 numbers above. Baseline was measured fresh on HEAD immediately before the code changes so the after-numbers compare directly rather than against README numbers from a different session. STJ row drifted less than 1% across the three runs, so the ratios are comparable.

#### JSON benchmark

```
Shape   Stage          Mean (us)   Δ Mean     Alloc (KB)    Δ Alloc
Big     baseline       290.33                 363.23
        sub-a          293.36      +1.0%      358.48        -1.3%
        sub-a+b        292.37      +0.7%      358.46        -1.3%
Deep    baseline       188.39                 208.68
        sub-a          186.87      -0.8%      208.41        -0.1%
        sub-a+b        191.23      +1.5%      202.38        -3.0%
Long    baseline       218.96                 299.09
        sub-a          216.73      -1.0%      296.24        -1.0%
        sub-a+b        220.37      +0.6%      292.20        -2.3%
Wide    baseline       144.15                 179.77
        sub-a          145.76      +1.1%      176.71        -1.7%
        sub-a+b        147.72      +2.5%      180.63        +0.5%
```

StdDev on the mean numbers typically 0.3-1.8 us (around 1% of mean), so most deltas above are inside single-run noise. Wide's +2.5% with sub-a+b is outside that band but still small, and is probably the "allocate `Symbol[Children.Count]` upfront then trim" pattern paying slightly more than the `List` header it saved when 3 of 5 AllOfRule children get Delete-filtered on that shape.

### Why the wins are small

Sub-lever (a): the JSON grammar's hot leaf allocators aren't where this cache applies. `simpleEscape = OneOf(...)` fires at roughly 3% of in-string characters (the escape-density the harness generates), `hexDigit` never fires because the generator excludes `\uXXXX`, and the OneOf inside `OptionalWhitespace` hits the discard path because the wrapper has `FlattenType.Delete`. `StringBody` produces one leaf per string body, not per character, and doesn't go through the OneOf allocation path at all. That leaves simpleEscape as essentially the only non-trivial OneOf allocation in the hot loop, and at 3% density there just isn't much to cache. The ~1% allocation drop we actually got is consistent with that scope.

Sub-lever (b): trading `new List<Symbol>(capacity)` for `new Symbol[capacity]` saves roughly the `List<T>` header (~24 B) per populated wrapper. But for AllOfRule wrappers where most children are filtered via `FlattenType.Delete` (JsonMember has 5 children, 2 kept), the new code allocates `Symbol[5]` upfront and then a trimmed `Symbol[2]`, which is two allocations for around 88 B total. The old code allocated the List header plus a `Symbol[5]` internal array, also two allocations for about the same total. Same count, same size. The win lands only on BetweenInclusiveRule paths where the List used to grow its internal array (Deep and Long shapes see the biggest allocation drops, 2-3%), and even that win is modest because `List<T>` was already doing doubling growth too.

Both results are consistent with what the earlier P700 attempt (discard propagation + empty-wrapper removal) and the P600 attempt (lazy transactions on leaves) already found: Symbol and list allocations are not the dominant cost on the JSON benchmark. The cost is the interpreter dispatch and Transaction cycle on every composite rule invocation, which only p800 (compiled state-machine emitter) addresses.

### Why the attempt was reverted

Neither sub-lever clears the "is this worth the permanent complexity" bar. Sub-lever (a) adds a per-Lexer cache field, a helper method on Lexer, and a critical "use a fresh rune-only string, not a pointer into the input" invariant that a future contributor could easily miss when hooking another leaf into the cache. Sub-lever (b) adds two growth paths (AllOfRule fixed-size, BetweenInclusive doubling) and a trim-on-short branch in two places where a single `List<T>` line used to be.

For at most ~3% allocation on Deep/Long and essentially zero wall-clock change on any shape, neither carries its weight. The p800 compiled-emitter lever is the next real place to spend effort. Everything allocation-side on the interpreter path has now been tried.

The README paragraph this experiment set out to fact-check was updated at [src/Benchmarks/README.md](../src/Benchmarks/README.md) before the code attempt. It now frames the remaining gap against Parlot as interpreter overhead plus per-composite-rule Transaction bookkeeping, and points at p600/p700/p800 as the supporting evidence rather than claiming tree richness is the cost center.

### What a future attempt should know

- The "just swap `List<T>` for `Symbol[]`" refactor is a wash. If anyone returns to sub-lever (b), the version worth trying is the full inline-buffer struct (4 `Symbol` fields inline in a struct, plus optional overflow, with `Symbol.Children` accepting the struct). That's the only flavor of (b) with a meaningful allocation ceiling to hit, because it removes the intermediate buffer entirely for the common ≤4 case. It's also invasive to `Symbol`'s public surface.
- Sub-lever (a) is structurally fine. It just has nothing to do at 3% escape density. A grammar with a hot `OneOf` loop outside a `FlattenType.Delete` wrapper (a tokenizer for keyword-heavy text, or ChordGrammar's `accidental` at larger corpus scale) would exercise it. The cache scaffolding is easy to reinstate from this attempt's git history if that need comes up.
- Measurement harness from the earlier P700 and P600 entries still applies: JSON with `--filter "*_InductorParser" "*_SystemTextJson"` and `--spot-check` after any change that touches Symbol construction or composite-rule plumbing.

---

## P600: Lazy Transaction Opening on Primitives and Optional/ZeroOrMore (reverted)

Engineering record of an attempt at the (since-deleted) p600 backlog item. The changes were written, measured, and reverted.

### What p600 asked for

Every non-trivial rule opens a `Transaction` on entry via `Lexer.BeginTransaction()`. The Transaction is a struct (two int writes, two bool writes) plus a `_transactionDepth++` on the lexer for trace indentation plus a `Dispose` on every exit path. Two categories of rule don't need that full machinery:

- Rules that always roll back (`PeekRule`, `NotRule`): they never commit, so the only job of the Transaction is to restore position. A saved `int` does the same work with less bookkeeping.
- Primitive rules that read at most one token before deciding (`TokenRule`, `OneOfRule`, `NoneOfRule`, `AnyTokenRule`): on failure the rule hasn't advanced past one rune, so rollback is trivially "restore saved position."
- `BetweenInclusiveRule` with `AtLeast==0` (Optional, ZeroOrMore): the rule can't fail in that configuration, so the outer rollback has nothing to roll back.

Three tiers proposed, smallest to biggest, with the expectation that ChordGrammar's ~10x-compiled-regex ratio would drop to ~5-7x.

### What was actually tried

All three tiers implemented.

Added `internal void Lexer.SetPosition(int)` (AggressiveInlining) so rules can rewind without opening a full Transaction.

`BetweenInclusiveRule` split into two code paths: the existing `AtLeast > 0` path keeps the outer `using var transaction = lexer.BeginTransaction()`, and a new `AtLeast == 0` path runs the same iteration loop without the outer transaction and skips the now-dead `count < AtLeast` failure branch.

`PeekRule` and `NotRule` swapped the Transaction for:

```csharp
int savedPosition = lexer.Position;
var innerResult = Inner.TryParse(lexer);
lexer.SetPosition(savedPosition);
```

The four leaves saved the position on entry and called `SetPosition` on every failure path before returning `null`. The success path just leaves the advanced position in place.

Updated the Rule.TryParseRule contract comment to describe the saved-position pattern alongside Transaction.

### Files touched (in the attempt)

- [src/InductorParser/Lexing/Lexer.cs](../src/InductorParser/Lexing/Lexer.cs): added `SetPosition`.
- [src/InductorParser/BetweenInclusiveRule.cs](../src/InductorParser/BetweenInclusiveRule.cs): AtLeast==0 path without outer Transaction.
- [src/InductorParser/PeekRule.cs](../src/InductorParser/PeekRule.cs), [NotRule.cs](../src/InductorParser/NotRule.cs): saved-position int (always restore).
- [src/InductorParser/TokenRule.cs](../src/InductorParser/TokenRule.cs), [OneOfRule.cs](../src/InductorParser/OneOfRule.cs), [NoneOfRule.cs](../src/InductorParser/NoneOfRule.cs), [AnyTokenRule.cs](../src/InductorParser/AnyTokenRule.cs): saved-position int (restore on failure).
- [src/InductorParser/Rule.cs](../src/InductorParser/Rule.cs): updated subclass contract comment.
- ~15 test files: trace-output expected indentation shifted shallower because the leaves no longer bump `_transactionDepth`.

Test surface: all 458 non-timing tests passed after updating trace expectations. No semantic change to parse results, only rollback mechanics and trace indentation.

### Measurements

Measurement protocol: stash production-rule changes, rebuild, run baseline. Pop, rebuild, run after. For ChordGrammar and BacklogGrammar, both sides were measured fresh in the same session. For JSON the "after" was a fresh BenchmarkDotNet run and the baseline was the existing README numbers (also post-p500 master), so the JSON baseline / after comparison straddles separate BDN sessions and picks up additional run-to-run noise.

#### ChordGrammar timing (5000 iters × 151 inputs, ratio vs compiled regex)

```
                 run 1     run 2
Baseline         10.50x     9.78x
After            10.33x     9.67x
```

Flat within noise. The regex baseline itself drifted 15-20% run-to-run (Chord regex time swung 5.8ms to 7.3ms observed), so the ratio is the noisier number.

#### BacklogGrammar per-case ratios (range over two runs per side)

```
              Baseline       After
H1            37-48x         30-43x
H2            26-29x         24-26x
Bullet        12-16x          6-25x
HrRun         5-6x            3-6x
HrSpaced      9x              7-12x
Paragraph    12-21x          19-37x
```

Every cell lands in the observed noise range of the other side. No consistent delta.

#### JSON benchmark (BenchmarkDotNet ShortRunJob, 3 iterations)

```
Shape   Baseline Mean → After       Δ Mean       Baseline Alloc → After      Δ Alloc
Big     292.86 → 294.61 μs          +0.6%        363.23 → 363.23 KB             0%
Deep    190.17 → 181.39 μs          -4.6%        208.68 → 208.68 KB             0%
Long    219.01 → 210.75 μs          -3.8%        299.09 → 299.09 KB             0%
Wide    143.79 → 140.83 μs          -2.1%        179.77 → 179.77 KB             0%
```

STJ baseline drifted 11% on Big between the two BenchmarkDotNet sessions (24.66 → 27.36 μs), which is larger than any of the Mean deltas above. Read these as "within noise." Allocations unchanged, which is expected. p600 targeted transaction bookkeeping, not allocation sites.

### Why the wins were small

`Transaction` is already cheap in absolute terms. It's a struct, so stack-allocated, no GC pressure. Construction is two int writes plus two bool writes. `Dispose` is a flag read, maybe one int write, and one int decrement. Shaving that still leaves the rule body (`Lexer.Read`, the comparison against the expected rune or grapheme, the Symbol allocation on success) doing most of the work.

The hot paths on the grammars measured aren't leaf-bound:

- **ChordGrammar** spends its time in `Literal` / `FirstOf` dispatch (already helped by p500's required-runes filter). The Token / OneOf leaves aren't the bottleneck.
- **JSON** spends its time in `StringBody` (already a specialized scanner that doesn't dispatch per character) and in the structural `AllOf` / `ZeroOrMore` wrappers that build the output tree. Those still open Transactions and still allocate `List<Symbol>` wrappers. p600 didn't touch either.

The backlog item's "ChordGrammar probably drops to ~5-7x" estimate was optimistic because it assumed leaf-rule overhead was a bigger portion of the hot path than it actually is.

### Why the attempt was reverted

Net neutral to slightly positive against the noise floor, against a permanent increase in API surface (new `Lexer.SetPosition` internal method) and a load-bearing invariant a future contributor could miss: leaves must call `SetPosition` on every failure path, while wrapper rules still use `BeginTransaction` / `Commit` / `Dispose`. Two parallel rollback patterns in the codebase, with the correctness burden on the implementer of each new rule to pick the right one.

For essentially flat wall-clock and zero allocation change, the split pattern didn't carry its weight. The p600 backlog item was deleted along with the revert.

### What future work should know

The remaining hot-path cost isn't on the leaf-rule rollback surface. It's on the composites (AllOf, FirstOf, BetweenInclusive with AtLeast≥1) that actually use rollback, and on the allocations they produce. See p700 (per-iteration wrapper-allocation work) and p800 (compiled state-machine emitter for stable grammars) for the higher-ceiling levers.

Don't re-attempt lazy transactions on leaves unless it's part of a compile-time specialization that also eliminates the Rule-to-Rule dispatch itself. Shaving the Transaction struct alone doesn't move the needle on the grammars we care about.

One thing worth reusing if this ever gets revisited: the `AtLeast == 0` removal in `BetweenInclusiveRule` is the cleanest of the three tiers. No new API surface, no two-pattern problem, just dead-code removal. If a future attempt can measure a real win from that alone (it didn't stand out in isolation here because Chord doesn't hit it often on the hot path), it might ship standalone.

---

## P750: First-Rune Lookahead Skip on BetweenInclusiveRule (shipped)

Engineering record of backlog item p750 ("First-rune lookahead skip on BetweenInclusiveRule"). Landed after measuring a clear, consistent 7-12% Mean drop across all four JSON benchmark shapes.

### What p750 asked for

From [backlog/p750-first-rune-lookahead-skip-on-betweeninclusiverule.md](../backlog/p750-first-rune-lookahead-skip-on-betweeninclusiverule.md): `FirstOfRule` has a first-rune lookahead (the p500 required-runes filter) that skips any child whose `Advance` is `Always` and whose `RequiredInitialRuneSet` rules the peek out. `BetweenInclusiveRule` (the composite that `ZeroOrMore` / `Optional` / `OneOrMore` wrap) had no equivalent, so every invocation ran at least one full `Inner.TryParse` even when the lookahead could have proved Inner can't match. On the JSON benchmark's Big shape, that amounted to 2089 `OneOf FAIL` outcomes (19% of all rule invocations), almost entirely from `OptionalWhitespace()` scanning for whitespace that isn't there. Deep was similarly lopsided at 1283 out of 6482 invocations (also ~20%).

### What was actually tried

At the top of `BetweenInclusiveRule.TryParseRule`, before opening the iteration loop, three gates are checked:

- `Inner.Advance == Advance.Always`: Inner must consume a rune to match, so the peek is decisive.
- `Inner.ErrorMessage == null`: if the author set `.WithError(...)` on Inner, run it anyway so that message can surface via deepest-failure-wins (mirrors `FirstOfRule`'s same gate).
- `!lexer.PreserveAllSymbols`: debug-tree mode still sees the same Inner invocations the grammar declares.

If all three pass, `pos < input.Length`, and the next rune isn't in `Inner.RequiredInitialRuneSet`, Inner definitely can't match the first iteration:

- `AtLeast == 0` (`Optional`, `ZeroOrMore`): commit with zero iterations, return the empty-wrapper Symbol (or `Discarded` for `FlattenType.Delete` wrappers).
- `AtLeast > 0` (`OneOrMore`, `BetweenInclusive(n, m)` with n≥1): record failure at the start position and return null.

The three hints consulted (`Advance`, `RequiredInitialRuneSet`, `ErrorMessage`) are the exact three `FirstOfRule` already reads per child. No new compile-time analysis. Self-recursive grammars where `RequiredInitialRuneSet` falls back to `Universe` (via the cycle-detection path in `Rule.ComputeRuleStartAll`) never fire the skip because `Universe.Contains` is always true, a safe fallback.

### Files touched

- [src/InductorParser/BetweenInclusiveRule.cs](../src/InductorParser/BetweenInclusiveRule.cs): the lookahead-and-skip block before the existing iteration loop.
- [src/InductorParser.Tests/Rules/OneOrMoreRuleTests.cs](../src/InductorParser.Tests/Rules/OneOrMoreRuleTests.cs): `OneOrMore_trace_failure_produces_expected_output` no longer has the inner `Token FAIL` line.
- [src/InductorParser.Tests/Rules/OptionalRuleTests.cs](../src/InductorParser.Tests/Rules/OptionalRuleTests.cs): `Optional_trace_without_match_produces_expected_output` same story.

Test surface: all 458 non-timing tests pass. Only two trace expectations shifted (much smaller than p600's ~15, because most trace tests already use `PreserveAllSymbols=true` for `Tree.ToString()` assertions, which gates the skip off). Spot-check (`dotnet run --project src/Benchmarks -- --spot-check`) confirms byte-exact round-trip across all four JSON shapes.

### Rule invocation counts (via `--rule-counts`)

```
Big shape (7193 chars):
Rule               Baseline    After       Δ
Total outcomes     11005        8916    -2089
OneOf FAIL         2089           0    -2089
(all other rows unchanged)

Deep shape (2601 chars):
Rule               Baseline    After       Δ
Total outcomes      6482        4428    -2054
OneOf FAIL         1283           0    -1283
Token FAIL            257           0     -257
AllOf FAIL           257           0     -257
ZeroOrMore SUCC     1540        1283     -257
```

On Big, every eliminated invocation is a `OneOf FAIL` from `OptionalWhitespace()`. The grammar's trailing-comma branches don't fail on Big because the benchmark's JSON always has at least one member. On Deep, the same skip also short-circuits the `AllOf` rule inside the outer `ZeroOrMore(AllOf(OptionalWhitespace, Token(','), ...))`, so the `Token FAIL` and `AllOf FAIL` rows drop to zero too. The ZeroOrMore SUCC drop is bookkeeping: the trailing-ZeroOrMore still emits one SUCC trace per call, but it no longer runs nested OptionalWhitespace ZeroOrMores inside the AllOf that got skipped.

### Measurements

Measurement protocol: stash the change, rebuild, run baseline 3x. Pop, rebuild, run after 3x. Same box, same BenchmarkDotNet ShortRunJob, same 8-row filter (`*_InductorParser` + `*_SystemTextJson`). Baseline's Big row was captured on 2 of 3 runs because a concurrent source edit during run 2 invalidated that row, which is acceptable since the other shapes have 3 valid runs each and the "after" deltas are much larger than the single-run spread.

#### JSON benchmark (BenchmarkDotNet ShortRunJob, median of 3 runs)

```
Shape   Baseline Mean → After       Δ Mean       Baseline Alloc → After      Δ Alloc
Big     291.81 → 269.30 μs          -7.7%        363.23 → 363.23 KB             0%
Deep    187.10 → 166.61 μs         -11.0%        208.68 → 208.68 KB             0%
Long    215.72 → 190.00 μs         -11.9%        299.09 → 299.09 KB             0%
Wide    143.58 → 128.82 μs         -10.3%        179.77 → 179.77 KB             0%
```

StdDev on the per-run Mean numbers was 0.2-1.7 us (0.1-1.0% of Mean), so every Δ above is several standard deviations outside the noise floor. Allocations are unchanged because the skip doesn't alter what `BetweenInclusiveRule` returns, the same empty-wrapper Symbol (or `Discarded`) as the full-loop path would produce.

### Why the win lands here when p600 and p700 didn't

`Transaction` (p600's target) is a struct the JIT had already largely inlined. Empty-Flatten wrapper Symbols (p700's target) are cheap to allocate and short-lived. This skip is different in kind: it eliminates the entire `Inner.TryParse` *dispatch*, which is the virtual call through `Rule.TryParse`, the `EnterRule` / `ExitRule` pair, Inner's own `BeginTransaction`, the `Read` that actually reads the input buffer, the `_set.Contains` comparison against the expected rune set, the `RecordFailure` on the failure path, and the `Dispose` cycle at the end. For every invocation of a `ZeroOrMore` / `Optional` / `OneOrMore` whose lookahead rules Inner out, the cost drops from all of that to three comparisons and a return.

On Big that's 2089 skipped dispatches per parse. The measured -22 us Mean drop spread across those calls works out to ~10 ns per skipped dispatch, which is within the right order of magnitude for "virtual call + Read + set comparison + transaction open/dispose" on this platform.

This result reinforces the thesis in [src/Benchmarks/README.md](../src/Benchmarks/README.md): the remaining gap against Parlot is *interpreter overhead*, not allocation, and the fix is to avoid the dispatch rather than optimize what happens inside it. Every Inner invocation skipped is an interpreter frame that never has to pay its cost.

### Why this ships

Small code footprint: one gated block at the top of `TryParseRule` that reads properties already computed at rule-construction time. No new API surface, no new compile-time analysis pass, no load-bearing invariant that a future contributor could miss when adding a new composite rule. Semantic transparency: when Inner would fail and cause the outer BetweenInclusive to succeed-with-zero-iterations or fail-as-unreached-lower-bound, the skip produces the same output tree and the same failure record. The `Inner.ErrorMessage == null` gate ensures user-supplied error messages still surface by running the inner path that would emit them.

The only externally observable behavior change is trace output: diagnostic-level traces for calls the skip caught no longer include the inner `Lexer.Read` / FAIL lines. Two existing trace-expectation tests were updated. The rest already use `PreserveAllSymbols=true` (which gates the skip off) for their `Tree.ToString()` assertions.

### What future work should know

- `FirstOfRule` (p500) and now `BetweenInclusiveRule` (p750) are the two composite wrappers that open transactions on entry and can tolerate their child failing with zero advance. FirstOf's "try next branch" and BetweenInclusive's "zero-iteration success for AtLeast==0" both have that shape. `AllOfRule` does not, because its child failing is propagating. There's no branch to skip to. So this pattern is applied everywhere it can be on the current interpreter.
- If p800 (compiled state-machine emitter) lands, this skip becomes redundant because the emitter will inline the peek-and-skip directly into the generated code. Until then, p750 makes p800's baseline ~10% faster and slightly harder to beat.
- The verification harness from this attempt is reusable: `--rule-counts --shape=<big|deep|long|wide>` shows per-Rule trace outcome totals, which is the cleanest way to confirm a lookahead-style optimization actually fires on the intended invocations. The `RuneProfiler` plumbing lives in [src/Benchmarks/RuleProfiler.cs](../src/Benchmarks/RuleProfiler.cs) and the CLI hook in [src/Benchmarks/Program.cs](../src/Benchmarks/Program.cs).
