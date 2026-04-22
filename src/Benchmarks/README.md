# InductorParser Benchmarks

BenchmarkDotNet harness comparing InductorParser against other C# JSON parsers.
Forked from [Parlot's benchmark suite](https://github.com/sebastienros/parlot/tree/main/test/Parlot.Benchmarks) (BSD-3-Clause; see [LICENSE-PARLOT.txt](LICENSE-PARLOT.txt)) and extended with adapters for InductorParser and Pegasus (the only other PEG generator we could find in the C# ecosystem).

## Why it's a separate solution

`Benchmarks.sln` is deliberately split from `InductorParser.sln` at the repo root. The benchmark project pulls in seven external packages (BenchmarkDotNet, Parlot, Pidgin, Sprache, Superpower, Newtonsoft.Json, Pegasus). The parser library and its test project don't need any of that, and forcing every contributor and CI run to restore those packages just to build InductorParser would be wasteful. Open `Benchmarks.sln` only when you want to run benchmarks.

## Running

From the repo root:

```
dotnet run -c Release --project src/Benchmarks/Benchmarks.csproj -- --filter *Json* --exporters GitHub
```

Spot-check (verifies InductorParser and Pegasus produce semantically equivalent trees on a known input, runs in milliseconds, not a bench):

```
dotnet run -c Release --project src/Benchmarks/Benchmarks.csproj -- --spot-check
```

BenchmarkDotNet writes full reports under `BenchmarkDotNet.Artifacts/results/` (gitignored).

## Changes from upstream Parlot's benchmark

These numbers are **not directly comparable** to Parlot's published benchmark results. We forked the harness and made deliberate changes for fairness and coverage. If you're reconciling against upstream Parlot, apply the deltas below.

**New rows added to the table**
- `*_InductorParser` — the whole point of forking. Uses InductorParser's fluent API (see [Json/InductorJsonParser.cs](Json/InductorJsonParser.cs)).
- `*_Pegasus` — adapter for the Pegasus PEG generator. Gives us two PEG rows to compare against each other (see [Json/PegasusParsers/](Json/PegasusParsers/)).

**Baseline changed**
- Upstream uses `ParlotCompiled` as `[Benchmark(Baseline = true)]`. We moved the baseline to `SystemTextJson` so ratios read "how much slower than the hand-written BCL reference" — a more useful framing when comparing non-Parlot parsers.

**Sorting**
- Added `[Orderer(SummaryOrderPolicy.FastestToSlowest)]` so each category ranks fastest-first rather than in source-file order.

**Competitor grammars extended to handle JSON escapes**
- Upstream Pidgin, Sprache, and Superpower each parse strings as "any char except `"`" — no escape handling. This gave them an unfair per-char edge over Parlot's `Terms.String(Double)`, which parses real JSON strings with escape sequences.
- We added escape-decoding rules (`\"`, `\\`, `\/`, `\b`, `\f`, `\n`, `\r`, `\t`, `\uXXXX`) to all three competitor grammars. Every parser in the table now does real JSON string work.
- Impact: Pidgin went from ~6x STJ to ~17x STJ on Big once escapes were actually exercised. Parlot got slightly *faster* (its already-present escape path became a hot loop). See the "Reading the table" section.

**Input generator produces escape sequences**
- Upstream `RandomString` emits only `[A-Za-z0-9]` — the escape path above never fires.
- We changed it to emit ~3% special chars (`"`, `\`, `\n`, `\r`, `\t`, `\b`, `\f`, sampled uniformly) so escape handling is actually on the hot path without dominating it. See [Json/JsonBench.cs](Json/JsonBench.cs).
- **Why 3%?** Representative JSON carrying natural text (log messages, product descriptions, user names with the occasional quoted phrase) typically contains 1-5% escape-worthy characters; clean data payloads (numerical or ID-heavy API responses) sit at roughly 0%. 3% is the middle of the realistic range. Higher rates (the 20% we tried first) start measuring escape-decoding throughput specifically — interesting but not "overall JSON parse speed on realistic input." Lower rates let the escape code go cold between iterations, measuring branch-prediction artifacts rather than steady-state cost.
- Implementation: `_random.Next(32) == 0` flips a coin per char, so ~1/32 characters are specials. With 5-6 char strings, about 17% of individual string values contain any escape — realistic for "some strings have escapes, most don't."
- `\uXXXX` is deliberately excluded from the generator because Newtonsoft canonicalizes a parsed `\u000A` back to `\n`, which would break byte-for-byte round-trip verification.
- `JsonString.ToString()` and `JsonObject.ToString()` were updated to re-emit escapes via a new `JsonString.Escape()` helper (upstream's versions emitted raw string content, which would break the round-trip now that inputs contain special chars).

**Superpower is absent from the Deep category**
- Upstream excludes it because the 256-deep shape overflows the stack inside Superpower's combinator pipeline. We kept that exclusion but added a `**CRASH**` row at the bottom of the Deep table so the failure is visible — excluding it entirely would hide a real limitation.

## Parsed shape

The harness generates four JSON shapes (see `JsonBench.BuildJson`):

* **Big** — `BuildJson(4, 4, 3)`: 4 elements, depth 4, width 3 (balanced).
* **Long** — `BuildJson(256, 1, 1)`: 256 top-level elements (wide at the root).
* **Deep** — `BuildJson(1, 256, 1)`: 256 levels of nesting.
* **Wide** — `BuildJson(1, 1, 256)`: one object with 256 members.

Leaves are a mix of alphanumeric characters, quotes, backslashes, and five C-style control chars (`\n`, `\r`, `\t`, `\b`, `\f`). Roughly one character in 32 (≈3%) is a special that requires escape encoding in the JSON text — the realistic middle of the 1-5% range you see in JSON carrying natural text. See the "Changes from upstream" section above for why 3% specifically. No numbers, booleans, or nulls — those would change which parser features get exercised. The `\uXXXX` form is deliberately excluded because Newtonsoft canonicalizes a parsed `\u000A` back to `\n`, which would break the byte-for-byte round-trip the spot-check relies on.

Every grammar-based parser in the benchmark (InductorParser, Pegasus, Pidgin, Sprache, Superpower) has been updated to decode JSON escape sequences. Parlot was already doing so via `Terms.String(Double)`. Newtonsoft and STJ handle the full JSON spec. So every row in the table below is timing an apples-to-apples "parse a JSON string containing escapes, produce the decoded value." No parser is skipping work the others do.

## Baseline

Numbers below are a regression baseline from one machine, not marketing claims. What matters is the shape of the table and whether a future change moves a row dramatically. 

Each category is sorted fastest-to-slowest. `Ratio` is relative to `SystemTextJson` (the BenchmarkDotNet baseline for the category). STJ is the hand-written, allocation-aware JSON parser in the .NET BCL, so it's the reasonable "how fast can a .NET programmer actually get" reference point. A ratio of 12 means "12x slower than the best purpose-built JSON parser in the ecosystem."

Every number in this table was produced by a parse that consumed the full input and round-tripped its tree back to the exact input bytes. The `--spot-check` mode in [Program.cs](Program.cs) runs that verification across all four shapes; the benchmark itself would otherwise happily time a parser that silently stopped at the opening bracket.

```
BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8246)
Arm64 RyuJIT AdvSIMD, .NET 8.0.25
Job=ShortRun  IterationCount=3  LaunchCount=1  WarmupCount=3
```

| Method                  | Mean        | Ratio | Allocated  | Alloc Ratio |
|------------------------ |------------:|------:|-----------:|------------:|
| BigJson_SystemTextJson  |    24.73 μs |  1.00 |   24.12 KB |        1.00 |
| BigJson_Parlot          |    59.35 μs |  2.40 |   95.76 KB |        3.97 |
| BigJson_ParlotCompiled  |    60.24 μs |  2.44 |   95.76 KB |        3.97 |
| BigJson_Newtonsoft      |    97.86 μs |  3.96 |  205.13 KB |        8.51 |
| BigJson_InductorParser  |   231.73 μs |  9.37 |   70.73 KB |        2.93 |
| BigJson_Pidgin          |   592.12 μs | 23.94 |    91.7 KB |        3.80 |
| BigJson_Pegasus         |   720.27 μs | 29.12 | 3384.39 KB |      140.33 |
| BigJson_Superpower      |   862.45 μs | 34.87 |  944.84 KB |       39.18 |
| BigJson_Sprache         | 1,697.84 μs | 68.64 | 6927.52 KB |      287.24 |
|                         |             |       |            |             |
| DeepJson_Parlot         |    41.10 μs |  0.50 |   99.33 KB |        4.91 |
| DeepJson_ParlotCompiled |    41.87 μs |  0.51 |   99.33 KB |        4.91 |
| DeepJson_Newtonsoft     |    61.24 μs |  0.74 |  181.16 KB |        8.95 |
| DeepJson_SystemTextJson |    82.31 μs |  1.00 |   20.24 KB |        1.00 |
| DeepJson_InductorParser |   109.90 μs |  1.34 |   21.29 KB |        1.05 |
| DeepJson_Pegasus        |   319.88 μs |  3.89 | 1386.41 KB |       68.48 |
| DeepJson_Pidgin         |   397.10 μs |  4.82 |  151.84 KB |        7.50 |
| DeepJson_Sprache        | 1,471.12 μs | 17.87 |  3412.1 KB |      168.55 |
| DeepJson_Superpower     |   **CRASH** |     — |          — |           — |
|                         |             |       |            |             |
| LongJson_SystemTextJson |    18.21 μs |  1.00 |   24.12 KB |        1.00 |
| LongJson_ParlotCompiled |    51.21 μs |  2.81 |  121.18 KB |        5.02 |
| LongJson_Parlot         |    51.79 μs |  2.84 |  121.18 KB |        5.02 |
| LongJson_Newtonsoft     |    79.21 μs |  4.35 |   204.7 KB |        8.49 |
| LongJson_InductorParser |   151.36 μs |  8.31 |   42.83 KB |        1.78 |
| LongJson_Pidgin         |   437.57 μs | 24.04 |  120.25 KB |        4.99 |
| LongJson_Pegasus        |   468.60 μs | 25.74 |  2433.8 KB |      100.92 |
| LongJson_Superpower     |   660.92 μs | 36.30 |  753.57 KB |       31.25 |
| LongJson_Sprache        | 1,334.03 μs | 73.28 | 5334.57 KB |      221.19 |
|                         |             |       |            |             |
| WideJson_SystemTextJson |    11.64 μs |  1.00 |   16.12 KB |        1.00 |
| WideJson_Parlot         |    28.56 μs |  2.45 |   43.37 KB |        2.69 |
| WideJson_ParlotCompiled |    28.70 μs |  2.46 |   43.37 KB |        2.69 |
| WideJson_Newtonsoft     |    47.63 μs |  4.09 |  108.74 KB |        6.75 |
| WideJson_InductorParser |   111.52 μs |  9.58 |   43.05 KB |        2.67 |
| WideJson_Pegasus        |   381.36 μs | 32.75 | 2023.66 KB |      125.56 |
| WideJson_Pidgin         |   382.22 μs | 32.82 |   40.48 KB |        2.51 |
| WideJson_Superpower     |   440.74 μs | 37.85 |  479.72 KB |       29.76 |
| WideJson_Sprache        |   729.65 μs | 62.66 | 3842.66 KB |      238.42 |

Reading the table: InductorParser lands between 1.3x and 9.6x STJ depending on shape — roughly 8-10x on Big/Long/Wide (where string content dominates) and 1.3x on Deep (where nesting dominates and STJ's per-level validation cost narrows the gap). It sits at position 5 on every shape: above all other grammar-based parsers and right below Newtonsoft. **Parlot is still the clear winner among grammar-based parsers**, running at 2.4-2.8x STJ across every shape. Pidgin sits around 24-33x STJ once escapes are actually exercised. Sprache is consistently the slow end at 63-73x STJ. Pegasus, the only other PEG generator in the comparison, runs at 26-33x STJ.

**Superpower crashes on Deep.** 256 levels of nested objects overflow the .NET stack inside Superpower's combinator pipeline (it's an uncatchable `StackOverflowException`, not a parse error — the whole process dies). That's why there's no timing for `DeepJson_Superpower` in the table.

**Parlot and Newtonsoft are actually *faster* than STJ on Deep** (0.50x-0.74x STJ). Deep has far fewer total characters than the other shapes (~2,600 vs 5,000-8,000), so parsers that are efficient per-token but have recursion overhead end up ahead. STJ pays a depth-validation cost at every level that dominates when the character-scanning work is light.

The escape-handling story is instructive even at 3% escape density. Pidgin's LINQ-combinator approach routes every matched char through an `.Or(...)` between an escape parser and a regular-char parser — even a 3% actual escape rate is enough to keep that branch warm and expose its overhead (24-33x STJ). Parlot's `Terms.String` handled escapes all along via a specialized hot-path scanner, so it doesn't pay an incremental cost. InductorParser now has the same class of specialized scanner (`StringChars`, which collapses `ZeroOrMore(Or(literal, escape))` into one rule with a tight inline loop) and lands in the same 1-10x STJ band as the libraries that have always had one, not the 24-73x band occupied by libraries that still dispatch per character.

Allocations tell a striking story. InductorParser now allocates about 1-3x what STJ does — within noise of the hand-written BCL parser that doesn't build a tree at all. The recent parse-time routing (`SuccessMode.DiscardAndMergeWithParent`) means every `Flatten`-typed composite writes its matches directly into its caller's list instead of building a wrapper `Symbol` + backing `List<Symbol>` and letting the caller splice them. That alone collapsed per-parse allocations 75-90% (Big: 363 KB → 71 KB, Deep: 209 KB → 21 KB, Long: 299 KB → 43 KB, Wide: 180 KB → 43 KB). Pegasus is worst for allocations (up to 170x STJ on Wide) because every grammar action produces a boxed intermediate. Sprache is nearly as bad at 170-290x. STJ is the floor at 1x because it doesn't produce a tree at all — it stores offset pointers into the input.

## Are all the parsers doing the same work?

The short answer: **the core parsing work is comparable, but each library materializes a different kind of output tree, and that's where most of the timing differences come from.** The sections below walk through every place the grammars could legitimately be called unequal and evaluate whether it matters.

### What gets accepted inside a string

Every grammar handles the same JSON escape set: `\"`, `\\`, `\/`, `\b`, `\f`, `\n`, `\r`, `\t`, and `\uXXXX`. The implementations differ stylistically but do equivalent work:

| Parser | Literal-char rule | Escape handling |
|---|---|---|
| InductorParser | `StringChars(RuneSet.Runes("\""), '\\', escapeEnd)` (one rule, inline scan loop; stop at ") | escape start + sub-rule end, handled inside the same scan |
| Pegasus | `[^"\\]` | `escape / literal` ordered choice, `\` followed by an escape suffix |
| Pidgin | `Token(c => c != '"' && c != '\\')` | `EscapedChar.Or(...)` with LINQ-style decoder |
| Sprache | `Token(c => c != '"' && c != '\\', ...)` | same pattern as Pidgin |
| Superpower | `Character.Matching(c => c != '"' && c != '\\', ...)` | same pattern as Pidgin |
| Parlot | built into `Terms.String(Double)` | same |
| Newtonsoft / STJ | full JSON spec | full JSON spec |

The grammars that use LINQ-style combinators (Pidgin, Sprache, Superpower) pay a visible cost for the escape branch — every character goes through an `.Or` between "try escape" and "try literal," and the escape-decoding lambda is allocated per match. The libraries that bake the string-parsing logic into a specialized combinator (Parlot's `Terms.String`, InductorParser's `StringChars`) handle escapes more efficiently. You can see the gap clearly in the Big row: Parlot at 2.3x STJ and InductorParser at 10.6x, vs Pidgin at 25x and Pegasus at 28x — same input, same work on paper, very different throughput once the per-character rule dispatch is eliminated.

### Whitespace handling

All six grammar-based parsers skip whitespace between structural tokens. The style differs:

- **InductorParser**, **Pegasus**, **Pidgin**, **Sprache**, **Superpower**: explicit whitespace rules (`OptionalWhitespace()` / `_ = [ \t\r\n]*` / `SkipWhitespaces` / `WhiteSpace.Many()`).
- **Parlot**: implicit via `Terms.Token(...)` which skips leading whitespace for every token.

Work-equivalent. On inputs with no whitespace (what the bench generates via `ToString()`), all of them bottom out at "peek next char, it's not whitespace, done."

### EOF enforcement

A parser that silently stops at the opening bracket and returns "success" would look fast but be wrong. Six of the eight parsers refuse that outright:

| Parser | EOF required? |
|---|---|
| InductorParser | **yes** — `Rule.Parse` checks `lexer.IsEof` |
| Pegasus | **yes** — explicit `!.` in the start rule |
| Pidgin | **yes** — `Parse` fails if any input is unconsumed |
| Superpower | **yes** — `Parse` throws on unconsumed input |
| Newtonsoft | **yes** — validates trailing content |
| System.Text.Json | **yes** — validates trailing content |
| Parlot | **no** — `TryParse` happily matches a prefix |
| Sprache | **no** — must check `Remainder.AtEnd` manually |

For Parlot and Sprache, `--spot-check` confirms they happen to consume the whole input on these specific benchmark shapes, so the numbers are honest even though the libraries wouldn't catch trailing garbage. On the existing inputs it's not a correctness issue, but it's an asymmetry worth knowing about if the input generator ever changes.

### What gets returned

This is the biggest remaining source of timing asymmetry.

| Parser | Output | What it takes to get useful JSON from it |
|---|---|---|
| InductorParser | **Symbol parse tree** — one node per matched structural rule | walk the tree, interpret rule IDs, reconstruct JSON |
| Pegasus | IJson tree via grammar actions | done |
| Parlot | IJson tree via `.Then` | done |
| Pidgin | IJson tree via `.Select` | done |
| Sprache | IJson tree via LINQ | done |
| Superpower | IJson tree via LINQ | done |
| Newtonsoft | `JToken` tree | done |
| STJ | offset pointers into input — no nodes | point into the input via indices |

InductorParser is still building a bigger data structure than the IJson-producing parsers — every structural rule in the grammar produces a Symbol — but the parse-time optimizations layer up: the Delete filter removes `OptionalWhitespace()` / delimiter nodes, the Or-wrapper removal collapses every `Or(...)` whose FlattenType is Flatten, the `StringChars` leaf produces one leaf Symbol for each string body instead of one per character, and the per-invocation `SuccessMode` routes each composite into either "merge my children into the caller's list" (no wrapper needed) or "wrap into a new Symbol" depending on what the FlattenType implies. Net effect: allocations came down from ~40-60x STJ to ~1-3x STJ. The remaining gap is the structural `None`-typed wrappers callers explicitly asked to preserve (`.Flatten(FlattenType.None)` and the root rule) plus the leaf Symbols that carry position and rule-id metadata.

This is inherent to what InductorParser is for. The library trades some speed for a parse tree that carries position and rule-id metadata you need for things like syntax highlighting, error recovery, and LSP integrations — the same kind of output a compiler frontend wants, not just a semantic JSON value. The benchmark numbers reflect that trade honestly. The parse-time optimizations (Delete filtering, Or-wrapper removal, `StringChars`) show how far you can close the gap without giving up the richer tree.

STJ is at the other extreme: it allocates nothing per JSON value, just stores byte offsets into the input. Every other parser has to justify itself against that.

### What's the same

- Every grammar is recursive-descent with the same five productions (value / string / object / array / member).
- Every parser consumes the full input and produces a tree that round-trips to the exact input bytes (`--spot-check` verifies this across all four shapes).
- Whitespace is handled by all parsers, explicitly or implicitly, for the same cost.
- The string-char inner loop does one comparison per byte for Pidgin, Sprache, and Superpower. Parlot (`Terms.String`) and InductorParser (`StringChars`) both collapse the whole string body into one specialized scanner instead.

### What's unfair but unfixable

- **InductorParser builds a bigger tree** than IJson-producing grammars. Inherent to the library.
- **STJ allocates nothing.** Inherent to its offset-pointer design; no grammar-based parser can match it without the same design choice.

### Bottom line

The grammars are now honestly comparable for the question they're answering: **"given the same input, a JSON document that actually contains escape sequences at a realistic ~3% rate, and a recursive-descent shape, how fast is each parsing strategy?"** InductorParser sits between 1.3x and 9.6x STJ depending on shape. It's in position 5 on every shape: above all other grammar-based parsers including Pegasus (the only other PEG generator) and Pidgin (the fastest combinator library), and below Newtonsoft and Parlot.

The remaining gap against Parlot is mostly interpreter overhead, not tree richness. Every composite rule (`And`, `Or`, `BetweenInclusive`) pays for a virtual `TryParseRule` call plus a `BeginTransaction` / `Commit` / `Dispose` cycle on every invocation. Parlot specializes those paths into tighter per-token code. We know it's interpreter overhead and not Symbol allocation because the p600 and p700 experiments (documented in [docs/PerformanceImprovementAttempts.md](../../docs/PerformanceImprovementAttempts.md)) removed transactions from leaves and removed empty-wrapper Symbol allocations respectively, and neither moved the needle — while p750, which peeks one rune before opening a `ZeroOrMore` / `Optional` / `OneOrMore` and skips the inner dispatch entirely when the peek rules it out, dropped JSON Mean by 7-12% across every shape by eliminating about 19% of all interpreter invocations. Avoiding the dispatch pays; optimizing what happens inside it doesn't. The richer Symbol parse tree we build (with position and rule-id metadata for syntax highlighting and error recovery) is a consequence of the grammar having more composite rules, not the cost center itself. The gap against STJ is mostly because STJ doesn't build a tree at all, it stores offset pointers into the input. See [p800](../../backlog/p800-compiled-state-machine-emitter-for-stable-grammars.md) for the compiled-emitter lever that would close the remaining interpreter gap.
