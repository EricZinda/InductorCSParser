# InductorParser Benchmarks

BenchmarkDotNet harness comparing InductorParser against other C# JSON parsers.
Forked from [Parlot's benchmark suite](https://github.com/sebastienros/parlot/tree/main/test/Parlot.Benchmarks) (BSD-3-Clause; see [LICENSE-PARLOT.txt](LICENSE-PARLOT.txt)) and extended with adapters for InductorParser and Pegasus (the only other PEG generator we could find in the C# ecosystem).

## Why it's a separate solution

`Benchmarks.sln` is deliberately split from `InductorParser.sln` at the repo root. The benchmark project pulls in six external packages (BenchmarkDotNet, Parlot, Pidgin, Sprache, Superpower, Pegasus). The parser library and its test project don't need any of that, and forcing every contributor and CI run to restore those packages just to build InductorParser would be wasteful. Open `Benchmarks.sln` only when you want to run benchmarks.

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
- `*_InductorParserRune`, `*_InductorParserStateMachine`, `*_InductorParserGrapheme`, `*_InductorParserTyped`: four variants of the InductorParser adapter, same grammar ([Json/InductorJsonParser.cs](Json/InductorJsonParser.cs)) but different evaluator, lexer, and output choices. `Rune` and `StateMachine` both use the rune lexer (one Unicode code point per step, one-to-one with `char` on the bench's ASCII-only input), but `Rune` runs the recursive virtual-dispatch evaluator that's the library's default and `StateMachine` runs an alternate evaluator that lowers the rule tree to a flat opcode array and dispatches through a single switch (see [src/InductorParser/StateMachine/](../InductorParser/StateMachine/)). `Grapheme` uses the UAX #29 grapheme-cluster lexer, which is strictly more Unicode work than competitors do (combining marks and emoji ZWJ sequences get joined into a single read), even though on this ASCII input the unit size still coincides. `Typed` uses the grapheme lexer and also walks the Symbol tree into a typed `IJson` tree just like every competitor does via `.Select(...)` / grammar actions. **`Typed` is the apples-to-apples row for competitor comparisons.** The other three rows isolate where cost comes from: evaluator (StateMachine / Rune), lexer choice (Grapheme / Rune), and typed-output construction (Typed / Grapheme).
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
- For Sprache and Superpower, we tried the same bulk-run pattern and measured it *slower* across all shapes. Their `.AtLeastOnce().Text()` / `.AtLeastOnce().Select(new string(...))` paths have no specialized accumulator, so every bulk run allocates a `char[]` plus a string, and the outer `.Many()` adds chunk-array overhead that exceeds the per-char `.Or()` dispatch savings. We kept the per-character pattern for those two libraries. The finding is worth stating explicitly: "use the bulk-run fast path" is library-specific advice, not universal. It wins when the library has a specialized bulk-string primitive (Parlot's `Terms.String`, InductorParser's `ScanUntil`, Pidgin's `ManyString`) and loses otherwise.

**Input generator produces escape sequences**
- Upstream `RandomString` emits only `[A-Za-z0-9]`, so the escape path above never fires.
- We changed it to emit ~3% special chars (`"`, `\`, `\n`, `\r`, `\t`, `\b`, `\f`, sampled uniformly) so escape handling is actually on the hot path without dominating it. See [Json/JsonBench.cs](Json/JsonBench.cs).
- **Why 3%?** Representative JSON carrying natural text (log messages, product descriptions, user names with the occasional quoted phrase) typically contains 1-5% escape-worthy characters; clean data payloads (numerical or ID-heavy API responses) sit at roughly 0%. 3% is the middle of the realistic range. Higher rates (the 20% we tried first) start measuring escape-decoding throughput specifically, which is interesting but not "overall JSON parse speed on realistic input." Lower rates let the escape code go cold between iterations, measuring branch-prediction artifacts rather than steady-state cost.
- Implementation: `_random.Next(32) == 0` flips a coin per char, so ~1/32 characters are specials. With 5-6 char strings, about 17% of individual string values contain any escape, realistic for "some strings have escapes, most don't."
- `\uXXXX` is excluded from the generator. The original reason was Newtonsoft canonicalizing `\u000A` back to `\n` and breaking byte-for-byte round-trip verification. Newtonsoft has since been removed from the bench, but the constraint stays: the input generator hasn't been re-evaluated since the removal, and changing what the bench measures is a separate decision.
- `JsonString.ToString()` and `JsonObject.ToString()` were updated to re-emit escapes via a new `JsonString.Escape()` helper (upstream's versions emitted raw string content, which would break the round-trip now that inputs contain special chars).

**Superpower is absent from the Deep category**
- Upstream excludes it because the 256-deep shape overflows the stack inside Superpower's combinator pipeline. We kept that exclusion but added a `**CRASH**` row at the bottom of the Deep table so the failure is visible. Excluding it entirely would hide a real limitation.

## Parsed shape

The harness generates four JSON shapes (see `JsonBench.BuildJson`):

* **Big**: `BuildJson(4, 4, 3)`: 4 elements, depth 4, width 3 (balanced).
* **Long**: `BuildJson(256, 1, 1)`: 256 top-level elements (wide at the root).
* **Deep**: `BuildJson(1, 256, 1)`: 256 levels of nesting.
* **Wide**: `BuildJson(1, 1, 256)`: one object with 256 members.

Leaves are a mix of alphanumeric characters, quotes, backslashes, and five C-style control chars (`\n`, `\r`, `\t`, `\b`, `\f`). Roughly one character in 32 (≈3%) is a special that requires escape encoding in the JSON text, the realistic middle of the 1-5% range you see in JSON carrying natural text. See the "Changes from upstream" section above for why 3% specifically. No numbers, booleans, or nulls, because those would change which parser features get exercised. The `\uXXXX` form is excluded because of a historical Newtonsoft canonicalization issue (Newtonsoft re-emitted `\u000A` back to `\n` as `\n`); Newtonsoft has been removed from the bench but the input generator hasn't been re-evaluated since.

Every grammar-based parser in the benchmark (InductorParser, Pegasus, Pidgin, Sprache, Superpower) has been updated to decode JSON escape sequences. Parlot was already doing so via `Terms.String(Double)`. STJ handles the full JSON spec. So every row in the table below is timing an apples-to-apples "parse a JSON string containing escapes, produce the decoded value." No parser is skipping work the others do.

## Baseline

Numbers below are a regression baseline from one machine, not marketing claims. What matters is the shape of the table and whether a future change moves a row dramatically. 

Each category is sorted fastest-to-slowest. `Ratio` is relative to `SystemTextJson` (the BenchmarkDotNet baseline for the category). STJ is the hand-written, allocation-aware JSON parser in the .NET BCL, so it's the reasonable "how fast can a .NET programmer actually get" reference point. A ratio of 12 means "12x slower than the best purpose-built JSON parser in the ecosystem."

Every number in this table was produced by a parse that consumed the full input and round-tripped its tree back to the exact input bytes. The `--spot-check` mode in [Program.cs](Program.cs) runs that verification across all four shapes; the benchmark itself would otherwise happily time a parser that silently stopped at the opening bracket.

A visual view of the same data is in [performance-chart.html](performance-chart.html) (open in a browser): four lines, one per shape, showing Mean μs per parser. The chart is regenerated on every benchmark run via [PerformanceChart.cs](PerformanceChart.cs), so it always reflects the latest numbers even when the table below drifts from them.

```
BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8246)
Arm64 RyuJIT AdvSIMD, .NET 8.0.25
Job=ShortRun  IterationCount=3  LaunchCount=1  WarmupCount=3
```

| Method                              | Mean        | Ratio | Allocated  | Alloc Ratio |
|------------------------------------ |------------:|------:|-----------:|------------:|
| BigJson_SystemTextJson              |    25.40 μs |  1.00 |   24.12 KB |        1.00 |
| BigJson_ParlotCompiled              |    59.95 μs |  2.36 |   95.76 KB |        3.97 |
| BigJson_Parlot                      |    62.08 μs |  2.44 |   95.76 KB |        3.97 |
| BigJson_InductorParserStateMachine  |   178.80 μs |  7.04 |  193.67 KB |        8.03 |
| BigJson_InductorParserRune          |   242.22 μs |  9.53 |  197.49 KB |        8.19 |
| BigJson_Pidgin                      |   317.68 μs | 12.51 |  200.53 KB |        8.31 |
| BigJson_InductorParserGrapheme      |   358.41 μs | 14.11 |  369.85 KB |       15.34 |
| BigJson_InductorParserTyped         |   398.39 μs | 15.68 |  457.07 KB |       18.95 |
| BigJson_PegasusOptimized            |   689.38 μs | 27.14 | 3060.02 KB |      126.88 |
| BigJson_PegasusWiki                 |   731.31 μs | 28.79 | 3384.39 KB |      140.33 |
| BigJson_Superpower                  |   860.44 μs | 33.87 |  944.84 KB |       39.18 |
| BigJson_Sprache                     | 1,679.83 μs | 66.12 | 6927.52 KB |      287.24 |
|                                     |             |       |            |             |
| DeepJson_Parlot                     |    41.09 μs |  0.50 |   99.33 KB |        4.91 |
| DeepJson_ParlotCompiled             |    41.64 μs |  0.51 |   99.33 KB |        4.91 |
| DeepJson_SystemTextJson             |    81.53 μs |  1.00 |   20.24 KB |        1.00 |
| DeepJson_InductorParserStateMachine |    82.65 μs |  1.01 |   86.59 KB |        4.28 |
| DeepJson_InductorParserRune         |   130.45 μs |  1.60 |   88.45 KB |        4.37 |
| DeepJson_InductorParserGrapheme     |   184.66 μs |  2.27 |  149.48 KB |        7.38 |
| DeepJson_InductorParserTyped        |   208.35 μs |  2.56 |  221.61 KB |       10.95 |
| DeepJson_Pidgin                     |   304.28 μs |  3.73 |  186.23 KB |        9.20 |
| DeepJson_PegasusOptimized           |   310.94 μs |  3.81 | 1297.48 KB |       64.10 |
| DeepJson_PegasusWiki                |   322.39 μs |  3.95 | 1386.41 KB |       68.49 |
| DeepJson_Sprache                    | 1,485.01 μs | 18.22 |  3412.1 KB |      168.56 |
| DeepJson_Superpower                 |   **CRASH** |     - |          - |           - |
|                                     |             |       |            |             |
| LongJson_SystemTextJson             |    18.93 μs |  1.00 |   24.12 KB |        1.00 |
| LongJson_ParlotCompiled             |    51.82 μs |  2.74 |  121.18 KB |        5.02 |
| LongJson_Parlot                     |    51.87 μs |  2.74 |  121.18 KB |        5.02 |
| LongJson_InductorParserStateMachine |   124.59 μs |  6.58 |  140.47 KB |        5.82 |
| LongJson_InductorParserRune         |   158.70 μs |  8.39 |  143.89 KB |        5.97 |
| LongJson_InductorParserGrapheme     |   237.14 μs | 12.53 |  259.84 KB |       10.77 |
| LongJson_Pidgin                     |   244.85 μs | 12.94 |  190.25 KB |        7.89 |
| LongJson_InductorParserTyped        |   269.27 μs | 14.23 |   355.9 KB |       14.76 |
| LongJson_PegasusOptimized           |   448.42 μs | 23.70 | 2254.15 KB |       93.47 |
| LongJson_PegasusWiki                |   468.79 μs | 24.77 |  2433.8 KB |      100.92 |
| LongJson_Superpower                 |   672.91 μs | 35.56 |  753.57 KB |       31.25 |
| LongJson_Sprache                    | 1,339.97 μs | 70.81 | 5334.57 KB |      221.19 |
|                                     |             |       |            |             |
| WideJson_SystemTextJson             |    11.62 μs |  1.00 |   16.12 KB |        1.00 |
| WideJson_ParlotCompiled             |    29.47 μs |  2.54 |   43.37 KB |        2.69 |
| WideJson_Parlot                     |    29.80 μs |  2.56 |   43.37 KB |        2.69 |
| WideJson_InductorParserStateMachine |    87.56 μs |  7.53 |  108.63 KB |        6.74 |
| WideJson_InductorParserRune         |   117.60 μs | 10.12 |  111.28 KB |        6.90 |
| WideJson_InductorParserGrapheme     |   186.29 μs | 16.03 |  215.37 KB |       13.36 |
| WideJson_Pidgin                     |   196.50 μs | 16.91 |  110.55 KB |        6.86 |
| WideJson_InductorParserTyped        |   223.78 μs | 19.26 |   257.6 KB |       15.98 |
| WideJson_PegasusOptimized           |   403.89 μs | 34.75 | 1816.38 KB |      112.70 |
| WideJson_Superpower                 |   443.11 μs | 38.13 |  479.72 KB |       29.76 |
| WideJson_PegasusWiki                |   465.59 μs | 40.06 | 2023.66 KB |      125.56 |
| WideJson_Sprache                    |   841.91 μs | 72.45 | 3842.66 KB |      238.42 |

Reading the table: four InductorParser rows, same grammar, different evaluator + lexer + output choices.

- `InductorParserStateMachine` is the fastest Inductor variant. It lands at 1.0x STJ on Deep and 6.6-7.5x STJ on Big/Long/Wide, ahead of every grammar-based parser in the table except Parlot. Same Rune lexer and same Symbol-tree output as `InductorParserRune`. The only difference is the evaluator: `StateMachine` lowers the grammar to a flat opcode array once and walks it through a single switch-dispatch loop, while `Rune` runs the library's default recursive evaluator that calls each rule's `TryParseRule` virtually. On every shape the state machine runs 1.27x to 1.58x faster than the recursive evaluator on the same grammar, with about 2% lower allocations. Deep is the dramatic case: at 83 μs vs STJ's 82 μs, a grammar-based parser is within noise of the hand-written BCL JSON reference. See [src/InductorParser/StateMachine/](../InductorParser/StateMachine/) for the lowered-program design.
- `InductorParserRune` is the recursive evaluator with the same Rune lexer. Lands between 1.6x and 10.1x STJ depending on shape. This is what `Rule.Parse(input)` does today and is the right row to cite when comparing the recursive evaluator's cost against either the state-machine row above or the lexer-and-output rows below.
- `InductorParserGrapheme` swaps in the UAX #29 grapheme-cluster lexer. Same recursive evaluator and same Symbol-tree output as Rune, different lexer. About 1.4-1.5x slower than Rune for grapheme-cluster assembly. Grapheme is strictly more Unicode work than competitors do (combining marks and emoji ZWJ sequences would join into a single read, and surrogate pairs never split), even though on ASCII input the unit boundaries happen to line up. Including this row shows the cost of the extra Unicode correctness, and isolates the lexer-cost delta vs. the Typed row.
- `InductorParserTyped` is the apples-to-apples row vs. competitors, sitting at 2.6-19.3x STJ. It uses the grapheme lexer and walks the Symbol tree into a typed `IJson` tree, the same output shape every competitor library's adapter produces. Measured end-to-end cost is what a user writing a "parse and consume" loop against InductorParser would see.

**Evaluator, lexer, and output costs split nicely across the four Inductor rows.** StateMachine runs 1.27x to 1.58x faster than Rune at the same Symbol-tree output and same Rune lexer. The win comes entirely from the evaluator: lowering the grammar once to a flat opcode array means the inner loop is one indirect dispatch (a switch on a small enum that the JIT lowers to a jump table) per state transition, against the recursive evaluator's per-rule virtual `TryParseRule` call plus the per-rule transaction setup `Rule.TryParse` does on top. The state machine also pools its backtrack and output buffers across parses via thread-static slots, so back-to-back parses on the same thread allocate nothing for the evaluator's own bookkeeping. Per-iteration backtrack frames are dropped when the loop's inner rule is one of the always-advancing single-state matches (`Literal`, `Token`, `OneOf`), and `Or` alternatives skip themselves on a peeked-rune mismatch the same way the recursive `OrRule` does at runtime. See [src/InductorParser/StateMachine/Lowerer.cs](../InductorParser/StateMachine/Lowerer.cs) for what each rule lowers to.

Grapheme runs about 1.4-1.55x slower than Rune on every shape (still measuring the recursive evaluator). Both lexers decode the input bytes into Unicode code points the same way. What Grapheme adds is a per-read cluster-boundary check: after decoding each code point, consult the UAX #29 segmentation tables to see whether the next code point is a combining mark or a zero-width joiner that should fuse onto the current grapheme. If it should, keep reading and merge. On the bench's ASCII input nothing ever fuses, so every check returns "single-codepoint grapheme, done," but the check still runs on every read. The Rune lexer skips it entirely. That per-read check is the 1.4-1.55x.

Typed runs another 1.1-1.15x slower than Grapheme. Typed does everything Grapheme does (parse to a Symbol tree), then walks that Symbol tree a second time and builds an IJson tree out of it: one `JsonString` / `JsonArray` / `JsonObject` class instance per value in the JSON, each with its own backing storage (a decoded C# string for JsonString, a `List<IJson>` for JsonArray, a `Dictionary<string, IJson>` for JsonObject). String values also get their escape sequences decoded into real characters during this pass (the parse left them as raw source text like the two chars `\` and `n` rather than the single newline). That second tree-walk plus the per-value class allocation is the 1.1-1.15x. It's also the exact work every competitor is already doing during their parse via `.Select` or grammar actions.

The escape-handling story is instructive even at 3% escape density. Three of the parsers in the table bake string-body scanning into a specialized bulk primitive: Parlot's `Terms.String`, InductorParser's `ScanUntil`, and Pidgin with `Token(pred).AtLeastOnceString()` wrapped in a chunk-level `.Or(escape).Many()`. Those three plus Parlot sit in the 2-15x STJ band. The two parsers still dispatching per character (Sprache at 63-73x, Superpower at 35-38x) land in the slow band. Pegasus sits in the middle (24-33x); its `[^"\\]+` char-class *is* a bulk primitive but its per-action machinery dominates regardless, as discussed in the Pegasus section below. The split isn't "combinators vs. generators" or "PEG vs. combinator", it's "does the library give you a specialized bulk-string primitive or not."

Allocations: InductorParserRune allocates about 4-8x STJ depending on shape (197 KB / 88 KB / 144 KB / 111 KB for Big / Deep / Long / Wide). That's already more than Parlot, because every value rule keeps a `Preserve`-typed Symbol wrapper in the tree so consumers can dispatch on rule id. StateMachine pools its evaluator-internal buffers (the backtrack stack, call stack, and output list) across parses on the same thread, which trims about 2% off Rune's allocations on every shape (194 / 87 / 140 / 109 KB). Grapheme adds ~1.8x on top of Rune, mostly because grapheme-cluster iteration allocates an enumerator state per read. Typed adds another ~1.2-1.4x for the IJson tree itself (one `JsonString`/`JsonArray`/`JsonObject` plus backing array/dictionary per JSON value). At the Typed rate InductorParser sits at 11-19x STJ for allocations, comparable to Pidgin (7-9x) and far ahead of Pegasus (64-140x) and Sprache (170-290x). STJ is the floor at 1x because it doesn't produce a tree at all: it stores offset pointers into the input. Pidgin's bulk-run pattern trades memory for speed: its allocations rose from ~4x to 7-9x STJ when we added the pattern, but its Mean dropped from ~25x to 12-15x STJ.

## Pegasus: optimized vs. wiki-style grammar

Two rows, `*_PegasusOptimized` and `*_PegasusWiki`, use the exact same Pegasus library (v4.1.0) and parse the exact same input. They differ only in grammar style.

The wiki-style grammar ([JsonWiki.peg](Json/PegasusParsers/JsonWiki.peg)) is a direct port of the Pegasus wiki JSON example. It matches string contents one character at a time through an ordered choice:

```
stringChar = escape / literal
literal    = c:[^"\\] { c }
```

and builds object/array collections with a recursive `first + rest*` pair glued together with LINQ `new[] { first }.Concat(rest)`.

The optimized grammar ([JsonOptimized.peg](Json/PegasusParsers/JsonOptimized.peg)) uses three patterns visible in Pegasus's own self-hosted `PegParser.peg`:

- `[^"\\]+` as a single bulk char-class run for maximal literal runs inside a string body (the Pegasus analogue of Parlot's `Terms.String` and InductorParser's `ScanUntil`).
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

## Are all the parsers doing the same work?

The short answer: **the core parsing work is comparable, but each library materializes a different kind of output tree, and that's where most of the timing differences come from.** The sections below walk through every place the grammars could legitimately be called unequal and evaluate whether it matters.

### What gets accepted inside a string

Every grammar handles the same JSON escape set: `\"`, `\\`, `\/`, `\b`, `\f`, `\n`, `\r`, `\t`, and `\uXXXX`. The implementations differ stylistically but do equivalent work:

| Parser | Literal-char rule | Escape handling |
|---|---|---|
| InductorParser | `ScanUntil(RuneSet.Runes("\""), '\\', escapeEnd)` (one rule, inline scan loop; stop at ") | Backslash is spotted inside the same scan loop and hands off to a small escape sub-rule, then the loop continues |
| Pegasus (optimized) | `[^"\\]+` bulk char-class run | When a bulk run stops, the next step picks between "escape" and "another literal run". Runs once per escape |
| Pegasus (wiki) | `[^"\\]` matched one char at a time via a `literal` rule | Every character picks between "escape" and "literal". Runs once per char |
| Pidgin | `Token(c => c != '"' && c != '\\').AtLeastOnceString()` bulk run (via `ManyString` StringBuilder primitive) | When a bulk run stops, the outer loop checks for a backslash and handles one escape. Runs once per escape |
| Sprache | `Char(c => c != '"' && c != '\\', ...)` matched one char at a time | Every character tries "escape" first and falls back to "literal" if that fails. Runs once per char. (Bulk-run variant measured slower, see below) |
| Superpower | `Character.Matching(c => c != '"' && c != '\\', ...)` matched one char at a time | Same as Sprache |
| Parlot | built into `Terms.String(Double)` (specialized inline scanner) | Handled inside the specialized `Terms.String` scanner, no separate rule involved |
| STJ | full JSON spec | Full JSON spec |

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
| System.Text.Json | **yes**, validates trailing content |
| Parlot | **no**, `TryParse` happily matches a prefix |
| Sprache | **no**, must check `Remainder.AtEnd` manually |

For Parlot and Sprache, `--spot-check` confirms they happen to consume the whole input on these specific benchmark shapes, so the numbers are apples-to-apples even though the libraries wouldn't catch trailing garbage. On the existing inputs it's not a correctness issue, but it's an asymmetry worth knowing about if the input generator ever changes.

### What gets returned

The shape of the output a parser hands back makes a big difference to timing, because the work done to build that output is baked into the parse time. Each library produces something a little different, and the four InductorParser rows let you see where each chunk of cost lives.

| Parser | Output | What it takes to get useful JSON from it |
|---|---|---|
| InductorParserStateMachine | **Symbol parse tree** (same as Rune, alternate evaluator) | walk the tree, interpret rule IDs, reconstruct JSON |
| InductorParserRune | **Symbol parse tree** (one node per matched structural rule) | walk the tree, interpret rule IDs, reconstruct JSON |
| InductorParserGrapheme | **Symbol parse tree** (same as Rune, different lexer) | walk the tree, interpret rule IDs, reconstruct JSON |
| InductorParserTyped | IJson tree via a Symbol-tree walker | done |
| Pegasus | IJson tree via grammar actions | done |
| Parlot | IJson tree via `.Then` | done |
| Pidgin | IJson tree via `.Select` | done |
| Sprache | IJson tree via LINQ | done |
| Superpower | IJson tree via LINQ | done |
| STJ | offset pointers into input (no nodes) | point into the input via indices |

InductorParser builds a bigger data structure than the IJson-producing parsers. Every structural rule in the grammar produces a Symbol, and each Symbol carries the source position it came from (offset plus length in the input) and the rule ID that produced it. That turns the parse tree into something you can actually use for IDE-class work: syntax highlighting, error-squiggle placement, jump-to-definition, formatting, refactoring, language-server integrations, anything that needs to map tree nodes back to locations in the source. A plain IJson tree throws all of that away at parse time and can't drive any of those uses.

The parse-time optimizations keep the overhead as low as it can be. The Delete filter removes `OptionalWhitespace()` / delimiter nodes, the FirstOf-wrapper removal collapses every `FirstOf(...)` whose FlattenType is Flatten, the `ScanUntil` leaf produces one leaf Symbol for each string body instead of one per character, and the per-invocation `SuccessMode` routes each composite into either "merge my children into the caller's list" (no wrapper needed) or "wrap into a new Symbol" depending on what the FlattenType implies. What Typed adds on top is one IJson + backing per value, matching competitor libraries' output shape exactly.

This is inherent to what InductorParser is for. The library trades some speed for a parse tree that carries position and rule-id metadata you need for things like syntax highlighting, error recovery, and LSP integrations, the same kind of output a compiler frontend wants. The Typed row shows that even on top of that richer tree, walking to an IJson output costs only about 1.1x extra over the Grapheme row; the Symbol tree isn't "wasted" work, it's a superset that carriers metadata competitors throw away.

STJ is at the other extreme: it allocates nothing per JSON value, just stores byte offsets into the input. Every other parser has to justify itself against that.

### What's the same

- Every grammar is recursive-descent with the same five productions (value / string / object / array / member).
- Every parser consumes the full input and produces a tree that round-trips to the exact input bytes (`--spot-check` verifies this across all four shapes).
- Whitespace is handled by all parsers, explicitly or implicitly, for the same cost.
- The string-char inner loop does one comparison per byte for Sprache and Superpower. Parlot (`Terms.String`), InductorParser (`ScanUntil`), and Pidgin (`Token(pred).AtLeastOnceString()`) collapse the whole string body into one specialized scanner instead.

### Design choices that show up in the numbers

- **InductorParser builds a bigger tree** than IJson-producing grammars. Every Symbol carries source position and rule-ID metadata, which is what lets the tree drive IDE-class uses (syntax highlighting, error recovery, LSP integrations). Competitors throw that information away at parse time, which lets them stay smaller but gives up those downstream uses.
- **STJ allocates nothing.** It stores offset pointers into the input instead of building a tree of objects. No grammar-based parser can match that without making the same design choice, and giving up the tree means giving up everything a consumer does with a tree.

### Bottom line

The grammars are directly comparable on the question they're answering: **"given the same input, a JSON document that actually contains escape sequences at a realistic ~3% rate, and a recursive-descent shape, how fast is each parsing strategy?"**

Four InductorParser rows let readers pick the right comparison:

- **`InductorParserStateMachine`** is the fastest of the four Inductor rows (7.0x STJ on Big, 1.0x on Deep). Same grammar, same Rune lexer, and same Symbol-tree output as `InductorParserRune`, but routes through an alternate evaluator that lowers the rule tree to a flat opcode array and dispatches through one switch per state transition. About 1.27-1.58x faster than the recursive evaluator on every shape with about 2% lower allocations. This is the right row to cite when your consumer uses the Symbol tree directly and you want the fastest path the library currently offers.
- **`InductorParserRune`** is the recursive evaluator on the same grammar and lexer. Lands at 1.6-10.1x STJ. This is what `Rule.Parse` does today. Cite this row when comparing the recursive evaluator's per-rule cost. The state machine handles every rule type the recursive evaluator does (the rare ones — `WithinGrapheme`, multi-rune StringBody stoppers/openers, custom Rule subclasses — bridge back through `Rule.TryParse`); parse budgets (`RuleCountLimit`, `MaxDepth`, `Timeout`, `Cancellation`) are not enforced on the state-machine path and are the one remaining feature gap.
- **`InductorParserGrapheme`** swaps in the UAX #29 grapheme-cluster lexer. Same Symbol-tree output as Rune, different lexer. About 1.45x slower than Rune, entirely from grapheme-cluster assembly. This is strictly more Unicode work than competitors do, so the row measures the cost of that extra correctness rather than a fair lexer-vs-lexer comparison.
- **`InductorParserTyped`** is the apples-to-apples row for comparing against IJson-building libraries. It does everything Grapheme does plus walks the Symbol tree into a typed `IJson` tree just like every competitor. Adds ~1.1-1.15x over Grapheme.

