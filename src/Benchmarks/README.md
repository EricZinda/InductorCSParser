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

## Rebar regex-engine benchmarks

The optional rebar runner lives in [Rebar/](Rebar/). It's a small `net10.0`
console program that implements rebar's KLV runner protocol for a curated set
of hand-translated regex benchmarks.

Build and local contract-check it with:

```
dotnet build -c Release src/Benchmarks/Rebar/RebarRunner.csproj -m:1 -p:BuildInParallel=false
dotnet run -c Release --project src/Benchmarks/Rebar/RebarRunner.csproj -- --self-test
```

See [Rebar/README.md](Rebar/README.md) for the supported rebar cases and the
engine TOML snippet to copy into a local rebar checkout.

Spot-check (round-trips every parser's output back to the exact input bytes across all four shapes, runs in milliseconds, not a bench):

```
dotnet run -c Release --project src/Benchmarks/Benchmarks.csproj -- --spot-check
```

Without this the bench numbers could be meaningless. A parser that silently stopped at the opening bracket would look blazing fast but be wrong. The spot-check makes sure every row in the table is timing a parse that actually consumed the full input and produced a tree that reconstructs it.

BenchmarkDotNet writes full reports under `BenchmarkDotNet.Artifacts/results/` (gitignored).

## Changes from upstream Parlot's benchmark

These numbers are **not directly comparable** to Parlot's published benchmark results. We forked the harness and made deliberate changes for fairness and coverage. If you're reconciling against upstream Parlot, apply the deltas below.

**New rows added to the table**
- `*_InductorParserToken`, `*_InductorParserTyped`: two variants of the InductorParser adapter, same grammar ([Json/InductorJsonParser.cs](Json/InductorJsonParser.cs)) but different output choices. Both use the same UAX #29 grapheme-cluster lexer (the library has only one lexer; combining marks and emoji ZWJ sequences get joined into a single read, which is strictly more Unicode work than competitors do, even though on the bench's ASCII input the unit size still coincides with one char). `Token` produces the Symbol parse tree the parser builds natively. `Typed` additionally walks that Symbol tree into a typed `IJson` tree just like every competitor does via `.Select(...)` / grammar actions. **`Typed` is the apples-to-apples row for competitor comparisons.** `Token` isolates the parse cost without the typed-output construction.
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
BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8875)
Arm64 RyuJIT armv8.0-a, .NET 10.0.6
Job=ShortRun  IterationCount=3  LaunchCount=1  WarmupCount=3
```

| Method                              | Mean        | Ratio | Allocated  | Alloc Ratio |
|------------------------------------ |------------:|------:|-----------:|------------:|
| BigJson_SystemTextJson              |    20.63 μs |  1.00 |   24.12 KB |        1.00 |
| BigJson_Parlot                      |    52.48 μs |  2.54 |   95.76 KB |        3.97 |
| BigJson_ParlotCompiled              |    53.19 μs |  2.58 |   95.76 KB |        3.97 |
| BigJson_Pidgin                      |   312.52 μs | 15.15 |  200.53 KB |        8.31 |
| BigJson_Superpower                  |   547.67 μs | 26.55 |  914.84 KB |       37.93 |
| BigJson_PegasusOptimized            |   547.84 μs | 26.55 | 2383.25 KB |       98.82 |
| BigJson_InductorParserToken         |   552.54 μs | 26.78 |  382.97 KB |       15.88 |
| BigJson_InductorParserTyped         |   593.38 μs | 28.76 |  476.44 KB |       19.76 |
| BigJson_PegasusWiki                 |   617.71 μs | 29.94 | 2821.03 KB |      116.97 |
| BigJson_Sprache                     | 1,533.70 μs | 74.34 | 6707.96 KB |      278.14 |
|                                     |             |       |            |             |
| DeepJson_ParlotCompiled             |    34.62 μs |  0.45 |   99.33 KB |        4.91 |
| DeepJson_Parlot                     |    35.30 μs |  0.46 |   99.33 KB |        4.91 |
| DeepJson_SystemTextJson             |    77.16 μs |  1.00 |   20.24 KB |        1.00 |
| DeepJson_PegasusOptimized           |   264.57 μs |  3.43 |  966.15 KB |       47.72 |
| DeepJson_PegasusWiki                |   271.30 μs |  3.52 | 1089.93 KB |       53.84 |
| DeepJson_Pidgin                     |   308.72 μs |  4.00 |  186.23 KB |        9.20 |
| DeepJson_InductorParserToken        |   335.58 μs |  4.35 |  213.23 KB |       10.53 |
| DeepJson_InductorParserTyped        |   364.77 μs |  4.73 |  295.36 KB |       14.59 |
| DeepJson_Sprache                    | 1,520.20 μs | 19.70 | 3300.36 KB |      163.03 |
| DeepJson_Superpower                 |   **CRASH** |     - |          - |           - |
|                                     |             |       |            |             |
| LongJson_SystemTextJson             |    13.70 μs |  1.00 |   24.12 KB |        1.00 |
| LongJson_Parlot                     |    45.01 μs |  3.29 |  121.18 KB |        5.02 |
| LongJson_ParlotCompiled             |    45.11 μs |  3.29 |  121.18 KB |        5.02 |
| LongJson_Pidgin                     |   251.20 μs | 18.34 |  190.25 KB |        7.89 |
| LongJson_PegasusOptimized           |   373.65 μs | 27.28 | 1746.02 KB |       72.40 |
| LongJson_Superpower                 |   417.04 μs | 30.45 |  721.57 KB |       29.92 |
| LongJson_PegasusWiki                |   431.95 μs | 31.54 | 2000.84 KB |       82.96 |
| LongJson_InductorParserToken        |   434.72 μs | 31.74 |  294.94 KB |       12.23 |
| LongJson_InductorParserTyped        |   480.92 μs | 35.11 |     401 KB |       16.63 |
| LongJson_Sprache                    | 1,196.31 μs | 87.34 | 5239.02 KB |      217.23 |
|                                     |             |       |            |             |
| WideJson_SystemTextJson             |    10.06 μs |  1.00 |   16.12 KB |        1.00 |
| WideJson_ParlotCompiled             |    26.15 μs |  2.60 |   43.37 KB |        2.69 |
| WideJson_Parlot                     |    27.15 μs |  2.70 |   43.37 KB |        2.69 |
| WideJson_Pidgin                     |   168.14 μs | 16.71 |  110.55 KB |        6.86 |
| WideJson_InductorParserToken        |   268.70 μs | 26.70 |  205.54 KB |       12.75 |
| WideJson_Superpower                 |   277.47 μs | 27.58 |  463.53 KB |       28.76 |
| WideJson_InductorParserTyped        |   284.11 μs | 28.24 |  247.81 KB |       15.38 |
| WideJson_PegasusOptimized           |   285.26 μs | 28.35 | 1440.35 KB |       89.37 |
| WideJson_PegasusWiki                |   331.72 μs | 32.97 | 1696.25 KB |      105.24 |
| WideJson_Sprache                    |   682.02 μs | 67.78 | 3747.41 KB |      232.51 |

Reading the table: two InductorParser rows, same grammar and same lexer, different output choices.

- `InductorParserToken` is the recursive evaluator producing the native Symbol parse tree: what `Rule.Parse(input)` does today. Lands between 4.4x (Deep) and 31.7x (Long) STJ. The `Token` suffix names the token-based lexer the parser runs on (one token per UAX #29 grapheme cluster).
- `InductorParserTyped` is the apples-to-apples row vs. competitors, sitting at 4.7-35.1x STJ. Same recursive evaluator and same lexer as Token, plus an additional walk over the Symbol tree to build a typed `IJson` tree, the same output shape every competitor library's adapter produces. The measured end-to-end cost is what you'd see writing a "parse and consume" loop against InductorParser.

Typed runs about 1.06-1.11x slower than Token. Typed does everything Token does (parse to a Symbol tree), then walks that Symbol tree a second time and builds an IJson tree out of it: one `JsonString` / `JsonArray` / `JsonObject` class instance per value in the JSON, each with its own backing storage (a decoded C# string for JsonString, a `List<IJson>` for JsonArray, a `Dictionary<string, IJson>` for JsonObject). String values also get their escape sequences decoded into real characters during this pass (the parse left them as raw source text like the two chars `\` and `n` rather than the single newline). The narrow gap between Token and Typed shows that the additional tree walk and per-value class allocation is small relative to the parse itself. It's also the exact work every competitor is already doing during their parse via `.Select` or grammar actions.

Recent history note: these numbers reflect the recursive evaluator after the LL(1) lookahead shortcut and the BetweenInclusive scanner-skip were removed (Testing branch, May 2026). On the same hardware, the same rows previously sat at 1.7-10.7x STJ. The recursive evaluator is now positioned as the readable reference implementation, not the speed path. The experimental state-machine evaluator, which lives in its own repo, keeps those optimizations and isn't measured here. See `docs/CodeArchitecture.md` for the two-engine split. The July 2026 rerun moved the host to .NET 10 (and BenchmarkDotNet 0.15.8). Nearly every parser got faster in absolute terms, SystemTextJson most of all, so several ratios rose even where the times dropped: LongJson_InductorParserToken went from 481 μs to 435 μs while its ratio went from 25.9x to 31.7x, because STJ went from 18.6 μs to 13.7 μs on the same shape.

The escape-handling story is instructive even at 3% escape density. Three of the parsers in the table bake string-body scanning into a specialized bulk primitive: Parlot's `Terms.String`, InductorParser's `ScanUntil`, and Pidgin with `Token(pred).AtLeastOnceString()` wrapped in a chunk-level `.Or(escape).Many()`. Parlot lands in the fast band (2.5-3.3x STJ, and ahead of STJ outright on Deep) and Pidgin in the next tier (4.0-18.3x). Sprache, dispatching per character, stays in the slow band (68-87x outside Deep). Superpower, the other per-character parser, sped up enough on .NET 10 to join the middle band (27-30x), where InductorParser's recursive evaluator and Pegasus also sit (3.4-35x), Pegasus because its per-action machinery dominates regardless of its `[^"\\]+` bulk primitive, and InductorParser-recursive because its Or-dispatch no longer prunes alternatives via the lookahead shortcut. The bulk-string-primitive story is still real (Parlot, Pidgin, and ScanUntil all do it), it just isn't enough on its own to keep a rule-attempt-per-alternative engine in the fast band.

Allocations: InductorParserToken allocates 10.5-15.9x STJ depending on shape (383 KB / 213 KB / 295 KB / 206 KB for Big / Deep / Long / Wide). The recursive evaluator's per-rule transaction now runs on every Or alternative that previously would have been shortcut-skipped, and each attempt drives its own temporary symbol list, which lifts both the latency and the allocation count. Typed adds another 1.2-1.4x for the IJson tree itself (one `JsonString`/`JsonArray`/`JsonObject` plus backing array/dictionary per JSON value). At the Typed rate InductorParser sits at 14.6-19.8x STJ for allocations, still ahead of Pegasus (48-117x) and Sprache (163-278x) but no longer comparable to Pidgin (6.9-9.2x). STJ is the floor at 1x because it doesn't produce a tree at all: it stores offset pointers into the input.

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
| Big  | 617.71 | 547.84 | 1.13x | -16% |
| Long | 431.95 | 373.65 | 1.16x | -13% |
| Wide | 331.72 | 285.26 | 1.16x | -15% |
| Deep | 271.30 | 264.57 | 1.03x | -11% |

So the optimized grammar buys about 3-16% across the board. That's real but modest, not the order-of-magnitude swing you might expect from eliminating per-character rule dispatch.

## Are all the parsers doing the same work?

The short answer: **the core parsing work is comparable, but each library materializes a different kind of output tree, and that's where most of the timing differences come from.** The sections below walk through every place the grammars could legitimately be called unequal and evaluate whether it matters.

### What gets accepted inside a string

Every grammar handles the same JSON escape set: `\"`, `\\`, `\/`, `\b`, `\f`, `\n`, `\r`, `\t`, and `\uXXXX`. The implementations differ stylistically but do equivalent work:

| Parser | Literal-char rule | Escape handling |
|---|---|---|
| InductorParser | `ScanUntil(TokenSet.Runes("\""), '\\', escapeEnd)` (one rule, inline scan loop; stop at ") | Backslash is spotted inside the same scan loop and hands off to a small escape sub-rule, then the loop continues |
| Pegasus (optimized) | `[^"\\]+` bulk char-class run | When a bulk run stops, the next step picks between "escape" and "another literal run". Runs once per escape |
| Pegasus (wiki) | `[^"\\]` matched one char at a time via a `literal` rule | Every character picks between "escape" and "literal". Runs once per char |
| Pidgin | `Token(c => c != '"' && c != '\\').AtLeastOnceString()` bulk run (via `ManyString` StringBuilder primitive) | When a bulk run stops, the outer loop checks for a backslash and handles one escape. Runs once per escape |
| Sprache | `Char(c => c != '"' && c != '\\', ...)` matched one char at a time | Every character tries "escape" first and falls back to "literal" if that fails. Runs once per char. (Bulk-run variant measured slower, see below) |
| Superpower | `Character.Matching(c => c != '"' && c != '\\', ...)` matched one char at a time | Same as Sprache |
| Parlot | built into `Terms.String(Double)` (specialized inline scanner) | Handled inside the specialized `Terms.String` scanner, no separate rule involved |
| STJ | full JSON spec | Full JSON spec |

### Whitespace handling

All six grammar-based parsers skip whitespace between structural tokens. The style differs:

- **InductorParser**, **Pegasus**, **Pidgin**, **Sprache**, **Superpower**: explicit whitespace rules (`Optional(AnyWhitespace())` / `_ = [ \t\r\n]*` / `SkipWhitespaces` / `WhiteSpace.Many()`).
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

The shape of the output a parser hands back makes a big difference to timing, because the work done to build that output is baked into the parse time. Each library produces something a little different, and the two InductorParser rows let you see where each chunk of cost lives.

| Parser | Output | What it takes to get useful JSON from it |
|---|---|---|
| InductorParserToken | **Symbol parse tree** (one node per matched structural rule) | walk the tree, interpret rule IDs, reconstruct JSON |
| InductorParserTyped | IJson tree via a Symbol-tree walker | done |
| Pegasus | IJson tree via grammar actions | done |
| Parlot | IJson tree via `.Then` | done |
| Pidgin | IJson tree via `.Select` | done |
| Sprache | IJson tree via LINQ | done |
| Superpower | IJson tree via LINQ | done |
| STJ | offset pointers into input (no nodes) | point into the input via indices |

InductorParser builds a bigger data structure than the IJson-producing parsers. Every structural rule in the grammar produces a Symbol, and each Symbol carries the source position it came from (offset plus length in the input) and the rule ID that produced it. That turns the parse tree into something you can actually use for IDE-class work: syntax highlighting, error-squiggle placement, jump-to-definition, formatting, refactoring, language-server integrations, anything that needs to map tree nodes back to locations in the source. A plain IJson tree throws all of that away at parse time and can't drive any of those uses.

The parse-time optimizations keep the overhead as low as it can be. The Delete filter removes `Optional(AnyWhitespace())` / delimiter nodes, the Or-wrapper removal collapses every `Or(...)` whose FlattenType is Flatten, the `ScanUntil` leaf produces one leaf Symbol for each string body instead of one per character, and the per-invocation `SuccessMode` routes each composite into either "merge my children into the caller's list" (no wrapper needed) or "wrap into a new Symbol" depending on what the FlattenType implies. What Typed adds on top is one IJson + backing per value, matching competitor libraries' output shape exactly.

This is inherent to what InductorParser is for. The library trades some speed for a parse tree that keeps the position and rule-id metadata you need for things like syntax highlighting, error recovery, and Language Server Protocol integrations, the same kind of output a compiler frontend wants. The Typed row shows that even on top of that richer tree, walking to an IJson output costs only about 1.1x extra over the Token row. The Symbol tree isn't "wasted" work, it's a superset that keeps metadata competitors throw away.

STJ is at the other extreme: it allocates nothing per JSON value, just stores byte offsets into the input. Every other parser has to justify itself against that.

### What's the same

- Every grammar is recursive-descent with the same five productions (value / string / object / array / member).
- Every parser consumes the full input and produces a tree that round-trips to the exact input bytes (`--spot-check` verifies this across all four shapes).
- Whitespace is handled by all parsers, explicitly or implicitly, for the same cost.
- The string-char inner loop does one comparison per byte for Sprache and Superpower. Parlot (`Terms.String`), InductorParser (`ScanUntil`), and Pidgin (`Token(pred).AtLeastOnceString()`) collapse the whole string body into one specialized scanner instead.

### Design choices that show up in the numbers

- **InductorParser builds a bigger tree** than IJson-producing grammars. Every Symbol carries source position and rule-ID metadata, which is what lets the tree drive IDE-class uses (syntax highlighting, error recovery, Language Server Protocol integrations). Competitors throw that information away at parse time, which lets them stay smaller but gives up those downstream uses.
- **STJ allocates nothing.** It stores offset pointers into the input instead of building a tree of objects. No grammar-based parser can match that without making the same design choice, and giving up the tree means giving up everything you'd otherwise do with a tree.

### Bottom line

The grammars are directly comparable on the question they're answering: **"given the same input, a JSON document that actually contains escape sequences at a realistic ~3% rate, and a recursive-descent shape, how fast is each parsing strategy?"**

Two InductorParser rows let you pick the right comparison:

- **`InductorParserToken`** is the recursive evaluator on the parser's native grammar and lexer: what `Rule.Parse` does today, producing the Symbol parse tree. Lands at 4.4-31.7x STJ. Cite this row if you're reading the Symbol tree directly.
- **`InductorParserTyped`** is the apples-to-apples row for comparing against IJson-building libraries. It does everything Token does plus walks the Symbol tree into a typed `IJson` tree just like every competitor. Adds 1.06-1.11x over Token.

Both rows measure the recursive evaluator. The state-machine evaluator, which lives in its own repo, keeps the optimizations the recursive engine shed (LL(1) lookahead, scanner-skip) and is the speed path for InductorParser today. It isn't represented in this JSON bench yet.

