# Tree-free state-machine path (2026-04-28)

Measured 2026-04-28 with rebar `0.1.0 (rev 8e952148cc)` on Windows
`win-arm64`, .NET host `10.0.6`. Lower medians are better. The run used
`--max-time 1s --max-warmup-time 500ms`.

The state-machine evaluator now has tree-free counting entry points
(`StateMachineParser.CountMatches`, `SumMatchByteLengths`,
`HasAnyMatch`, `CountMatchesAndCaptures`). Each one runs the same
Stepper as `Parse` but skips TreeBuilder and the resulting Symbol
allocation, walking `Machine.OutputOps` directly with a small
purpose-built reducer. The rebar runner now uses these for the four
non-`compile` models on the state-machine engine:

- `count` → `CountMatches`
- `count-spans` → `SumMatchByteLengths`
- `grep` → `HasAnyMatch` (per line)
- `grep-captures` → `CountMatchesAndCaptures` (per line)

The recursive evaluator still goes through `Parse` and walks the
`Symbol` tree, so the comparison shows where Symbol allocation is
the bottleneck.

## Where the wins land

The state-machine row across every per-line and small-haystack
benchmark:

| benchmark | model | before | after | speedup |
|---|---|---:|---:|---:|
| `07-unicode-character-data/parse-line` | `grep-captures` | 455.09ms | 54.98ms | **8.3x** |
| `06-cloud-flare-redos/simplified-long` | `count-spans` | 467.80us | 79.20us | **5.9x** |
| `06-cloud-flare-redos/original` | `count-spans` | 5.10us | 1.20us | **4.3x** |
| `06-cloud-flare-redos/simplified-short` | `count-spans` | 5.00us | 1.00us | **5.0x** |
| `11-unstructured-to-json/extract` | `grep-captures` | 1.75ms | 831.20us | **2.1x** |
| `08-words/all-english` | `count-spans` | 7.88ms | 3.74ms | **2.1x** |
| `14-quadratic/1x` | `count` | 27.20us | 18.70us | 1.45x |
| `14-quadratic/2x` | `count` | 75.00us | 58.40us | 1.28x |
| `01-literal/sherlock-en` | `count` | 134.70us | 108.90us | 1.24x |
| `02-literal-alternate/sherlock-en` | `count` | 590.70us | 527.20us | 1.12x |

"Before" = the state-machine numbers from the multi-literal-prefilter
run earlier today (or from the new-benchmarks summary). "After" =
this run.

The biggest moves are on workloads where Symbol allocation
dominated:

- `07-unicode-character-data/parse-line` parses 34,924 lines, each
  matching with 15 capture groups. That's 35K × 16+ Symbols per
  parse = ~600K Symbols allocated then walked then released per
  benchmark iteration. Skipping that allocation drops the row from
  455ms to 55ms.
- `06-cloud-flare-redos/*` are tiny-haystack runs where parse
  fixed cost is everything; the smallest (102 bytes) goes from 5μs
  to 1μs because the actual state-machine work is < 1μs and the
  rest was Symbol/ParseResult overhead.
- `11-unstructured-to-json/extract` is 100 lines × 6 Symbols-per-
  match. 2.1x is what's left after the prefix path stays the same
  (no IndexOf prefilter applies; every line matches).

The grep-captures rows that were already fast (`04-ruff-noqa/real`
at 3.25ms before / 3.25ms after, `04-ruff-noqa/tweaked` at 3.24ms
before / 3.26ms after) stay roughly the same. Those rows spend most
of their time inside the `# noqa` substring search, not in tree
construction.

The bounded-repeat rows (`10-bounded-repeat/context` at ~660ms
before and after) also stay roughly the same. The 101-way FirstOf
chain in the grammar dominates; tree construction is a small
fraction.

## Now versus `.NET compiled`

The state machine now beats `.NET compiled` on more rows:

| benchmark | model | state machine | .NET compiled | ratio |
|---|---|---:|---:|---:|
| `07-unicode-character-data/parse-line` | `grep-captures` | 54.98ms | 15.96ms | 3.4x slower |
| `11-unstructured-to-json/extract` | `grep-captures` | 831.20us | 39.50us | 21x slower |
| `06-cloud-flare-redos/simplified-short` | `count-spans` | 1.00us | 200.00ns | 5x slower |
| `09-aws-keys/quick` | `grep` | 5.10ms | 29.27ms | **5.7x faster** |
| `09-aws-keys/full` | `grep-captures` | 7.21ms | 39.07ms | **5.4x faster** |
| `04-ruff-noqa/real` | `grep-captures` | 3.25ms | 53.53ms | **16.5x faster** |
| `04-ruff-noqa/tweaked` | `grep-captures` | 3.26ms | 40.52ms | **12.4x faster** |

`07-unicode-character-data/parse-line` was 28x slower; now 3.4x
slower. `11-unstructured-to-json/extract` was 44x slower; now 21x.
The remaining gap on those two rows is real per-rune work the state
machine does (capture counting, fail/restore) that `.NET compiled`'s
DFA-flavored engine does in fewer cycles per rune. Closing that
further would mean optimizing the state machine's hot loop, which
is a different kind of work than skipping allocations.

## Implementation notes

`StateMachineParser` adds four public methods that share a single
`RunAndReduce<T>` core. The runner does:

```csharp
var program = GetOrLower(rootRule, ...);
var lexer = RentLexer(input, options);
lexer.ConfigureBudgets(options);
bool ok = Stepper.Run(program, lexer, out Machine machine);
try
{
    return ok ? reducer(machine.OutputOps, ...) : failureValue;
}
finally
{
    machine.Release();
    ReturnLexerToPool(lexer);
}
```

The four reducers are:

- `ReduceCountMatches`: counts `OpenComposite` plus leaf-shaped
  `EmitLeaf` ops whose `SymbolId` matches the rule's id. Leaf
  match rules (Literal, RuneRun, ScanUntil) emit a single
  `EmitLeaf` rather than an `Open/Close` pair, so both shapes need
  to count.
- `ReduceHasAnyMatch`: bails out on first hit. Equivalent to
  `Tree.Find(rule) != null`.
- `ReduceSumMatchByteLengths`: enters depth tracking on a match's
  `OpenComposite` (or counts the leaf directly if the match itself
  is a leaf) and sums the UTF-8 byte length of every leaf inside
  the match's `Open/Close` range.
- `CountMatchesAndCapturesCore`: per match, counts the match plus
  one for each distinct capture-rule id seen inside. Captures can
  be composites (`Capture(AllOf(...))`) or leaves
  (`Capture(ScanUntil(...))`); both shapes contribute, matching
  the recursive `match.Find(capture) != null` semantics.

`BenchmarkPlan` switches on `_useStateMachine` for the four search
models and falls through to the existing tree-walking path for the
recursive evaluator. The recursive evaluator builds the parse tree
no matter what (its `TryParse` always allocates Symbols), so the
no-tree fast path is state-machine-only for now.

All 35 supported rebar correctness cases return `OK` for both
engines (70 OK lines). The runner self-test stays green.
