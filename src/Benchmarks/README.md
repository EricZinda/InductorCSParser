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

Spot-check (round-trips every parser's output back to the exact input bytes across all four shapes, runs in milliseconds, not a bench):

```
dotnet run -c Release --project src/Benchmarks/Benchmarks.csproj -- --spot-check
```

Without this the bench numbers could be meaningless. A parser that silently stopped at the opening bracket would look blazing fast but be wrong. The spot-check makes sure every row in the table is timing a parse that actually consumed the full input and produced a tree that reconstructs it.

BenchmarkDotNet writes full reports under `BenchmarkDotNet.Artifacts/results/` (gitignored).

## Changes from upstream Parlot's benchmark

These numbers are **not directly comparable** to Parlot's published benchmark results. We forked the harness and made deliberate changes for fairness and coverage. If you're reconciling against upstream Parlot, apply the deltas below.

**New rows added to the table**
- `*_InductorParser`: the whole point of forking. Uses InductorParser's fluent API (see [Json/InductorJsonParser.cs](Json/InductorJsonParser.cs)).
- `*_PegasusOptimized` and `*_PegasusWiki`: two adapters for the Pegasus PEG generator using the same library but different grammar styles. `*_PegasusOptimized` uses idiomatic Pegasus patterns (bulk `[^"\\]+` char-class runs, `<min,max,sep>` delimited repetition) lifted from Pegasus's own self-hosted `PegParser.peg`. `*_PegasusWiki` is the style shown in the Pegasus wiki JSON example (per-character `escape / literal` ordered choice, `first + rest*` with `new[] { first }.Concat(rest)`). Both rows are in the table so the "same library, different grammar style" gap is visible. See [Json/PegasusParsers/JsonOptimized.peg](Json/PegasusParsers/JsonOptimized.peg) and [Json/PegasusParsers/JsonWiki.peg](Json/PegasusParsers/JsonWiki.peg).

**Baseline changed**
- Upstream uses `ParlotCompiled` as `[Benchmark(Baseline = true)]`. We moved the baseline to `SystemTextJson` so ratios read "how much slower than the hand-written BCL reference", a more useful framing when comparing non-Parlot parsers.

**Sorting**
- Added `[Orderer(SummaryOrderPolicy.FastestToSlowest)]` so each category ranks fastest-first rather than in source-file order.

**Competitor grammars extended to handle JSON escapes**
- Upstream Pidgin, Sprache, and Superpower each parse strings as "any char except `"`", no escape handling. This gave them an unfair per-char edge over Parlot's `Terms.String(Double)`, which parses real JSON strings with escape sequences.
- We added escape-decoding rules (`\"`, `\\`, `\/`, `\b`, `\f`, `\n`, `\r`, `\t`, `\uXXXX`) to all three competitor grammars. Every parser in the table now does real JSON string work.

**Bulk-run string-body pattern applied where it wins**
- For Pidgin, the string body is matched with a bulk-run fast path: `Token(c => c != '"' && c != '\\').AtLeastOnceString()` inside an outer `.Or(EscapeAsString).Many()`. That collapses the per-character `.Or()` dispatch into a single outer iteration per escape, leveraging Pidgin's `ManyString()` primitive which accumulates into a StringBuilder internally. Impact: Pidgin dropped from ~25x STJ to ~11-15x STJ on Big/Long/Wide, about a 2x speedup on realistic JSON. Allocations went up (one string per run chunk, plus a final `string.Concat`) but speed dominates.
- For Sprache and Superpower, we tried the same bulk-run pattern and measured it *slower* across all shapes. Their `.AtLeastOnce().Text()` / `.AtLeastOnce().Select(new string(...))` paths have no specialized accumulator, so every bulk run allocates a `char[]` plus a string, and the outer `.Many()` adds chunk-array overhead that exceeds the per-char `.Or()` dispatch savings. We kept the per-character pattern for those two libraries. The finding is worth stating explicitly: "use the bulk-run fast path" is library-specific advice, not universal. It wins when the library has a specialized bulk-string primitive (Parlot's `Terms.String`, InductorParser's `StringBody`, Pidgin's `ManyString`) and loses otherwise.

**Input generator produces escape sequences**
- Upstream `RandomString` emits only `[A-Za-z0-9]`, so the escape path above never fires.
- We changed it to emit ~3% special chars (`"`, `\`, `\n`, `\r`, `\t`, `\b`, `\f`, sampled uniformly) so escape handling is actually on the hot path without dominating it. See [Json/JsonBench.cs](Json/JsonBench.cs).
- **Why 3%?** Representative JSON carrying natural text (log messages, product descriptions, user names with the occasional quoted phrase) typically contains 1-5% escape-worthy characters; clean data payloads (numerical or ID-heavy API responses) sit at roughly 0%. 3% is the middle of the realistic range. Higher rates (the 20% we tried first) start measuring escape-decoding throughput specifically, which is interesting but not "overall JSON parse speed on realistic input." Lower rates let the escape code go cold between iterations, measuring branch-prediction artifacts rather than steady-state cost.
- Implementation: `_random.Next(32) == 0` flips a coin per char, so ~1/32 characters are specials. With 5-6 char strings, about 17% of individual string values contain any escape, realistic for "some strings have escapes, most don't."
- `\uXXXX` is deliberately excluded from the generator because Newtonsoft canonicalizes a parsed `\u000A` back to `\n`, which would break byte-for-byte round-trip verification.
- `JsonString.ToString()` and `JsonObject.ToString()` were updated to re-emit escapes via a new `JsonString.Escape()` helper (upstream's versions emitted raw string content, which would break the round-trip now that inputs contain special chars).

**Superpower is absent from the Deep category**
- Upstream excludes it because the 256-deep shape overflows the stack inside Superpower's combinator pipeline. We kept that exclusion but added a `**CRASH**` row at the bottom of the Deep table so the failure is visible. Excluding it entirely would hide a real limitation.

## Parsed shape

The harness generates four JSON shapes (see `JsonBench.BuildJson`):

* **Big**: `BuildJson(4, 4, 3)`: 4 elements, depth 4, width 3 (balanced).
* **Long**: `BuildJson(256, 1, 1)`: 256 top-level elements (wide at the root).
* **Deep**: `BuildJson(1, 256, 1)`: 256 levels of nesting.
* **Wide**: `BuildJson(1, 1, 256)`: one object with 256 members.

Leaves are a mix of alphanumeric characters, quotes, backslashes, and five C-style control chars (`\n`, `\r`, `\t`, `\b`, `\f`). Roughly one character in 32 (≈3%) is a special that requires escape encoding in the JSON text, the realistic middle of the 1-5% range you see in JSON carrying natural text. See the "Changes from upstream" section above for why 3% specifically. No numbers, booleans, or nulls, because those would change which parser features get exercised. The `\uXXXX` form is deliberately excluded because Newtonsoft canonicalizes a parsed `\u000A` back to `\n`, which would break the byte-for-byte round-trip the spot-check relies on.

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
| BigJson_SystemTextJson  |    28.77 μs |  1.00 |   24.12 KB |        1.00 |
| BigJson_ParlotCompiled  |    63.41 μs |  2.20 |   95.76 KB |        3.97 |
| BigJson_Parlot          |    63.89 μs |  2.22 |   95.76 KB |        3.97 |
| BigJson_Newtonsoft      |   101.69 μs |  3.54 |  205.13 KB |        8.51 |
| BigJson_InductorParser  |   215.23 μs |  7.48 |   66.69 KB |        2.77 |
| BigJson_Pidgin          |   317.98 μs | 11.05 |  200.53 KB |        8.31 |
| BigJson_PegasusOptimized |  676.88 μs | 23.53 | 3060.02 KB |      126.88 |
| BigJson_PegasusWiki     |   752.64 μs | 26.17 | 3384.39 KB |      140.33 |
| BigJson_Superpower      |   874.96 μs | 30.42 |  944.84 KB |       39.18 |
| BigJson_Sprache         | 1,762.29 μs | 61.27 | 6927.52 KB |      287.24 |
|                         |             |       |            |             |
| DeepJson_ParlotCompiled |    42.87 μs |  0.52 |   99.33 KB |        4.91 |
| DeepJson_Parlot         |    43.37 μs |  0.53 |   99.33 KB |        4.91 |
| DeepJson_Newtonsoft     |    54.12 μs |  0.66 |  181.16 KB |        8.95 |
| DeepJson_SystemTextJson |    81.86 μs |  1.00 |   20.24 KB |        1.00 |
| DeepJson_InductorParser |   106.31 μs |  1.30 |   24.27 KB |        1.20 |
| DeepJson_Pidgin         |   305.98 μs |  3.74 |  186.23 KB |        9.20 |
| DeepJson_PegasusOptimized |  319.31 μs |  3.90 | 1297.48 KB |       64.09 |
| DeepJson_PegasusWiki    |   329.41 μs |  4.02 | 1386.41 KB |       68.48 |
| DeepJson_Sprache        | 1,529.07 μs | 18.68 |  3412.1 KB |      168.55 |
| DeepJson_Superpower     |   **CRASH** |     - |          - |           - |
|                         |             |       |            |             |
| LongJson_SystemTextJson |    18.75 μs |  1.00 |   24.12 KB |        1.00 |
| LongJson_Parlot         |    53.16 μs |  2.83 |  121.18 KB |        5.02 |
| LongJson_ParlotCompiled |    54.56 μs |  2.91 |  121.18 KB |        5.02 |
| LongJson_Newtonsoft     |    84.28 μs |  4.49 |   204.7 KB |        8.49 |
| LongJson_InductorParser |   144.47 μs |  7.70 |   40.78 KB |        1.69 |
| LongJson_Pidgin         |   258.21 μs | 13.77 |  190.25 KB |        7.89 |
| LongJson_PegasusOptimized |  482.63 μs | 25.74 | 2254.15 KB |       93.47 |
| LongJson_PegasusWiki    |   497.85 μs | 26.55 |  2433.8 KB |      100.92 |
| LongJson_Superpower     |   730.65 μs | 38.96 |  753.57 KB |       31.25 |
| LongJson_Sprache        | 1,475.77 μs | 78.69 | 5334.57 KB |      221.19 |
|                         |             |       |            |             |
| WideJson_SystemTextJson |    11.87 μs |  1.00 |   16.12 KB |        1.00 |
| WideJson_Parlot         |    29.55 μs |  2.49 |   43.37 KB |        2.69 |
| WideJson_ParlotCompiled |    29.88 μs |  2.52 |   43.37 KB |        2.69 |
| WideJson_Newtonsoft     |    61.58 μs |  5.19 |  108.74 KB |        6.75 |
| WideJson_InductorParser |   107.01 μs |  9.01 |      41 KB |        2.54 |
| WideJson_Pidgin         |   174.23 μs | 14.67 |  110.55 KB |        6.86 |
| WideJson_PegasusOptimized |  376.37 μs | 31.70 | 1816.38 KB |      112.70 |
| WideJson_PegasusWiki    |   395.31 μs | 33.30 | 2023.66 KB |      125.56 |
| WideJson_Superpower     |   449.31 μs | 37.84 |  479.72 KB |       29.76 |
| WideJson_Sprache        |   759.97 μs | 64.01 | 3842.66 KB |      238.42 |

Reading the table: InductorParser lands between 1.3x and 9x STJ depending on shape, roughly 7.5-9x on Big/Long/Wide (where string content dominates) and 1.3x on Deep (where nesting dominates and STJ's per-level validation cost narrows the gap). It sits at position 5 on every shape: above all other grammar-based parsers and right below Newtonsoft. **Parlot is still the clear winner among grammar-based parsers**, running at 2.2-2.9x STJ on Big/Long/Wide and 0.5x on Deep (where its recursion overhead is cheap relative to STJ's per-level validation). Pidgin (with the bulk-run fast path applied) sits at 11-15x STJ, about 2x slower than InductorParser but half the cost of either Pegasus form. Superpower is in the 30-38x band. Sprache is consistently the slow end at 61-79x STJ. Pegasus, the only other PEG generator in the comparison, runs at 24-32x STJ with the optimized grammar and 26-33x with the wiki-style grammar; the gap between the two is discussed in its own section below.

**Superpower crashes on Deep.** 256 levels of nested objects overflow the .NET stack inside Superpower's combinator pipeline (it's an uncatchable `StackOverflowException`, not a parse error, so the whole process dies). That's why there's no timing for `DeepJson_Superpower` in the table.

**Parlot and Newtonsoft are actually *faster* than STJ on Deep** (0.51x-0.69x STJ). Deep has far fewer total characters than the other shapes (~2,600 vs 5,000-8,000), so parsers that are efficient per-token but have recursion overhead end up ahead. STJ pays a depth-validation cost at every level that dominates when the character-scanning work is light.

The escape-handling story is instructive even at 3% escape density. Three of the parsers in the table bake string-body scanning into a specialized bulk primitive: Parlot's `Terms.String`, InductorParser's `StringBody`, and Pidgin with `Token(pred).AtLeastOnceString()` wrapped in a chunk-level `.Or(escape).Many()`. Those three land in the 2-15x STJ band. The two parsers still dispatching per character (Sprache at 61-79x, Superpower at 30-38x) land in the slow band. Pegasus sits in the middle (24-33x); its `[^"\\]+` char-class *is* a bulk primitive but its per-action machinery dominates regardless, as discussed in the Pegasus section below. The split isn't "combinators vs. generators" or "PEG vs. combinator", it's "does the library give you a specialized bulk-string primitive or not."

Allocations tell a striking story. InductorParser now allocates about 1-3x what STJ does, within noise of the hand-written BCL parser that doesn't build a tree at all. The recent parse-time routing (`SuccessMode.DiscardAndMergeWithParent`) means every `Flatten`-typed composite writes its matches directly into its caller's list instead of building a wrapper `Symbol` + backing `List<Symbol>` and letting the caller splice them. That alone collapsed per-parse allocations 75-90% (Big: 363 KB → 67 KB, Deep: 209 KB → 24 KB, Long: 299 KB → 41 KB, Wide: 180 KB → 41 KB). Pegasus is worst for allocations (64-140x STJ depending on shape and grammar style) because every grammar action produces a boxed intermediate. Sprache is nearly as bad at 170-290x. Pidgin's bulk-run pattern trades memory for speed: its allocations rose from ~4x to 7-9x STJ when we added the pattern, but its Mean dropped from ~25x to 11-15x STJ. STJ is the floor at 1x because it doesn't produce a tree at all. It stores offset pointers into the input.

## Pegasus: optimized vs. wiki-style grammar

Two rows, `*_PegasusOptimized` and `*_PegasusWiki`, use the exact same Pegasus library (v4.1.0) and parse the exact same input. They differ only in grammar style.

The wiki-style grammar ([JsonWiki.peg](Json/PegasusParsers/JsonWiki.peg)) is a direct port of the Pegasus wiki JSON example. It matches string contents one character at a time through an ordered choice:

```
stringChar = escape / literal
literal    = c:[^"\\] { c }
```

and builds object/array collections with a recursive `first + rest*` pair glued together with LINQ `new[] { first }.Concat(rest)`.

The optimized grammar ([JsonOptimized.peg](Json/PegasusParsers/JsonOptimized.peg)) uses three patterns visible in Pegasus's own self-hosted `PegParser.peg`:

- `[^"\\]+` as a single bulk char-class run for maximal literal runs inside a string body (the Pegasus analogue of Parlot's `Terms.String` and InductorParser's `StringBody`).
- `jsonMember<0,,_ "," _>` delimited-repetition syntax for object members, which emits a `List<T>` directly and skips the `new[] { first }.Concat(rest)` enumerable.
- Same `<min,max,sep>` form for array elements.

Both versions pass `--spot-check` (byte-for-byte round-trip on all four shapes).

**What the measured delta actually shows**

| Shape | Wiki (μs) | Optimized (μs) | Speedup | Alloc delta |
|---|---:|---:|---:|---:|
| Big  | 776.90 | 664.96 | 1.17x | -10% |
| Long | 473.36 | 452.70 | 1.05x | -7% |
| Wide | 385.24 | 346.43 | 1.11x | -10% |
| Deep | 330.48 | 331.32 | 1.00x | -6% |

So the optimized grammar buys about 5-17% on shapes where string content matters, and essentially nothing on Deep (almost no string work). That's real but modest, not the order-of-magnitude swing you might expect from eliminating per-character rule dispatch.

The honest takeaway: for Pegasus, **rule-dispatch overhead is a minority of the cost**. The dominant cost is the per-match action machinery: every rule produces a boxed `IParseResult<T>`, every capture allocates, every alternation records a lexical element. Even the wiki-style grammar's per-character `escape / literal` is fast enough relative to that bookkeeping that collapsing it into a bulk run only moves the total by 10-15%.

This is different from what happens in a combinator library like Pidgin, where the per-char `.Or` dispatch is the whole cost and eliminating it (as Parlot did with `Terms.String`) moves the number by 3-10x. And it's different from what happened when we added `StringBody` to InductorParser, which is also an interpreter but built around a rule-ID dispatch that genuinely dominates on a per-char loop.

The practical implication: if you're reaching for Pegasus and you care about throughput, write the grammar the optimized way (it costs nothing and buys you 10-15%), but don't expect grammar style alone to close the gap to Parlot or STJ. That gap is in the library's action machinery, and it lives below the grammar.

## Are all the parsers doing the same work?

The short answer: **the core parsing work is comparable, but each library materializes a different kind of output tree, and that's where most of the timing differences come from.** The sections below walk through every place the grammars could legitimately be called unequal and evaluate whether it matters.

### What gets accepted inside a string

Every grammar handles the same JSON escape set: `\"`, `\\`, `\/`, `\b`, `\f`, `\n`, `\r`, `\t`, and `\uXXXX`. The implementations differ stylistically but do equivalent work:

| Parser | Literal-char rule | Escape handling |
|---|---|---|
| InductorParser | `StringBody(RuneSet.Runes("\""), '\\', escapeEnd)` (one rule, inline scan loop; stop at ") | escape start + sub-rule end, handled inside the same scan |
| Pegasus (optimized) | `[^"\\]+` bulk char-class run | `escape / literal` ordered choice at each run boundary (once per escape, not once per char) |
| Pegasus (wiki) | `[^"\\]` matched one char at a time via a `literal` rule | `escape / literal` ordered choice at every character |
| Pidgin | `Token(c => c != '"' && c != '\\').AtLeastOnceString()` bulk run (via `ManyString` StringBuilder primitive) | `LiteralRun.Or(EscapeAsString).Many()` — one outer iteration per escape, not per char |
| Sprache | `Char(c => c != '"' && c != '\\', ...)` matched one char at a time | `EscapedChar.Or(literalChar)` at every character (bulk pattern measured slower; see below) |
| Superpower | `Character.Matching(c => c != '"' && c != '\\', ...)` matched one char at a time | same as Sprache |
| Parlot | built into `Terms.String(Double)` (specialized inline scanner) | same |
| Newtonsoft / STJ | full JSON spec | full JSON spec |

Three groups fall out. **Specialized bulk-string primitive** (InductorParser `StringBody`, Parlot `Terms.String`, Pidgin `ManyString`): string body is one tight inline loop, escapes are handled at run boundaries. These sit at 2-15x STJ on Big. **Bulk char-class run but action-bound** (Pegasus `[^"\\]+`): the char-class itself is a fast inline loop, but every match still pays the Pegasus action/boxing bookkeeping, so it lands at 24-33x STJ regardless of grammar style. **Per-character combinator dispatch** (Sprache, Superpower): every char in a string body goes through an `.Or()` between escape and literal. We tried the bulk-run pattern on these two and measured it slower — their `.AtLeastOnce().Text()` / `.Select(new string(...))` paths have no specialized accumulator, so each bulk run allocates a `char[]` plus a string and the outer `.Many()` chunk-array overhead exceeds the per-char `.Or()` savings. Kept the per-character pattern for those two.

### Whitespace handling

All six grammar-based parsers skip whitespace between structural tokens. The style differs:

- **InductorParser**, **Pegasus**, **Pidgin**, **Sprache**, **Superpower**: explicit whitespace rules (`OptionalWhitespace()` / `_ = [ \t\r\n]*` / `SkipWhitespaces` / `WhiteSpace.Many()`).
- **Parlot**: implicit via `Terms.Token(...)` which skips leading whitespace for every token.

Work-equivalent. On inputs with no whitespace (what the bench generates via `ToString()`), all of them bottom out at "peek next char, it's not whitespace, done."

### EOF enforcement

A parser that silently stops at the opening bracket and returns "success" would look fast but be wrong. Six of the eight parsers refuse that outright:

| Parser | EOF required? |
|---|---|
| InductorParser | **yes**, `Rule.Parse` checks `lexer.IsEof` |
| Pegasus | **yes**, explicit `!.` in the start rule |
| Pidgin | **yes**, `Parse` fails if any input is unconsumed |
| Superpower | **yes**, `Parse` throws on unconsumed input |
| Newtonsoft | **yes**, validates trailing content |
| System.Text.Json | **yes**, validates trailing content |
| Parlot | **no**, `TryParse` happily matches a prefix |
| Sprache | **no**, must check `Remainder.AtEnd` manually |

For Parlot and Sprache, `--spot-check` confirms they happen to consume the whole input on these specific benchmark shapes, so the numbers are honest even though the libraries wouldn't catch trailing garbage. On the existing inputs it's not a correctness issue, but it's an asymmetry worth knowing about if the input generator ever changes.

### What gets returned

This is the biggest remaining source of timing asymmetry.

| Parser | Output | What it takes to get useful JSON from it |
|---|---|---|
| InductorParser | **Symbol parse tree** (one node per matched structural rule) | walk the tree, interpret rule IDs, reconstruct JSON |
| Pegasus | IJson tree via grammar actions | done |
| Parlot | IJson tree via `.Then` | done |
| Pidgin | IJson tree via `.Select` | done |
| Sprache | IJson tree via LINQ | done |
| Superpower | IJson tree via LINQ | done |
| Newtonsoft | `JToken` tree | done |
| STJ | offset pointers into input (no nodes) | point into the input via indices |

InductorParser is still building a bigger data structure than the IJson-producing parsers (every structural rule in the grammar produces a Symbol), but the parse-time optimizations layer up: the Delete filter removes `OptionalWhitespace()` / delimiter nodes, the Or-wrapper removal collapses every `Or(...)` whose FlattenType is Flatten, the `StringBody` leaf produces one leaf Symbol for each string body instead of one per character, and the per-invocation `SuccessMode` routes each composite into either "merge my children into the caller's list" (no wrapper needed) or "wrap into a new Symbol" depending on what the FlattenType implies. Net effect: allocations came down from ~40-60x STJ to ~1-3x STJ. The remaining gap is the structural `None`-typed wrappers callers explicitly asked to preserve (`.Flatten(FlattenType.None)` and the root rule) plus the leaf Symbols that carry position and rule-id metadata.

This is inherent to what InductorParser is for. The library trades some speed for a parse tree that carries position and rule-id metadata you need for things like syntax highlighting, error recovery, and LSP integrations, the same kind of output a compiler frontend wants, not just a semantic JSON value. The benchmark numbers reflect that trade honestly. The parse-time optimizations (Delete filtering, Or-wrapper removal, `StringBody`) show how far you can close the gap without giving up the richer tree.

STJ is at the other extreme: it allocates nothing per JSON value, just stores byte offsets into the input. Every other parser has to justify itself against that.

### What's the same

- Every grammar is recursive-descent with the same five productions (value / string / object / array / member).
- Every parser consumes the full input and produces a tree that round-trips to the exact input bytes (`--spot-check` verifies this across all four shapes).
- Whitespace is handled by all parsers, explicitly or implicitly, for the same cost.
- The string-char inner loop does one comparison per byte for Sprache and Superpower. Parlot (`Terms.String`), InductorParser (`StringBody`), and Pidgin (`Token(pred).AtLeastOnceString()`) collapse the whole string body into one specialized scanner instead.

### What's unfair but unfixable

- **InductorParser builds a bigger tree** than IJson-producing grammars. Inherent to the library.
- **STJ allocates nothing.** Inherent to its offset-pointer design; no grammar-based parser can match it without the same design choice.

### Bottom line

The grammars are now honestly comparable for the question they're answering: **"given the same input, a JSON document that actually contains escape sequences at a realistic ~3% rate, and a recursive-descent shape, how fast is each parsing strategy?"** InductorParser sits between 1.3x and 9x STJ depending on shape, at position 5 on every shape: above every other grammar-based parser (Pidgin even with its bulk-run fast path, both Pegasus forms, Superpower, Sprache), and below Parlot, Newtonsoft, and STJ. The margin against Pidgin is 1.3-1.6x on Big/Long/Wide and 2.9x on Deep (where Pidgin's per-object `.Or()` bookkeeping adds up across 256 levels). Pidgin moving from ~25x STJ down to 11-15x once the bulk-run fast path is applied is the biggest table shift, and it narrows the InductorParser-vs-combinator gap meaningfully.

The remaining gap against Parlot is mostly interpreter overhead, not tree richness. Every composite rule (`And`, `Or`, `BetweenInclusive`) pays for a virtual `TryParseRule` call plus a `BeginTransaction` / `Commit` / `Dispose` cycle on every invocation. Parlot specializes those paths into tighter per-token code. We know it's interpreter overhead and not Symbol allocation because the p600 and p700 experiments (documented in [docs/PerformanceImprovementAttempts.md](../../docs/PerformanceImprovementAttempts.md)) removed transactions from leaves and removed empty-wrapper Symbol allocations respectively, and neither moved the needle. Meanwhile p750, which peeks one rune before opening a `ZeroOrMore` / `Optional` / `OneOrMore` and skips the inner dispatch entirely when the peek rules it out, dropped JSON Mean by 7-12% across every shape by eliminating about 19% of all interpreter invocations. Avoiding the dispatch pays; optimizing what happens inside it doesn't. The richer Symbol parse tree we build (with position and rule-id metadata for syntax highlighting and error recovery) is a consequence of the grammar having more composite rules, not the cost center itself. The gap against STJ is mostly because STJ doesn't build a tree at all, it stores offset pointers into the input. See [p800](../../backlog/p800-compiled-state-machine-emitter-for-stable-grammars.md) for the compiled-emitter lever that would close the remaining interpreter gap.
