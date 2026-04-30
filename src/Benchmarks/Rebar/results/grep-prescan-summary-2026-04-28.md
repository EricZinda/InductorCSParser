# Grep pre-scan trigger (2026-04-28)

Measured 2026-04-28 with rebar `0.1.0 (rev 8e952148cc)` on Windows
`win-arm64`, .NET host `10.0.6`. Lower medians are better.
The run used `--max-time 1s --max-warmup-time 500ms`.

This run includes the grep pre-scan trigger added to
`BenchmarkPlan.CountMatchingLines` and `CountGrepCaptures`. When the
grammar provides a literal that must appear in any matching line
(e.g. "# noqa" case-insensitive for the ruff-noqa benchmarks), the
runner does a single BCL substring search across the haystack to
find candidate lines instead of parsing every line. The 32MB
ruff-noqa haystack has ~400K lines and ~300 matches, so the trigger
turns hundreds of thousands of parses into a few hundred.

The pre-scan is opt-in per grammar via `PatternGrammar.GrepTrigger`.
Grammars without a guaranteed-literal-prefix (the 09-aws-keys/quick
case is `(?:ASIA|AKIA|AROA|AIDA)...`, four prefixes with no shared
substring beyond 'A') still walk every line.

Ratios are `engine median / .NET compiled median` for that row.

## Affected rows: ruff-noqa real and tweaked

| rebar name | model | engine | before | after | speedup |
|---|---:|---|---:|---:|---:|
| `04-ruff-noqa/real` | `grep-captures` | InductorParser recursive | 3.38s | 3.33ms | **1015x** |
| `04-ruff-noqa/real` | `grep-captures` | InductorParser state machine | 1.22s | 3.50ms | **349x** |
| `04-ruff-noqa/tweaked` | `grep-captures` | InductorParser recursive | 265.93ms | 3.26ms | **82x** |
| `04-ruff-noqa/tweaked` | `grep-captures` | InductorParser state machine | 227.82ms | 3.26ms | **70x** |

After the pre-scan, both InductorParser variants are FASTER than .NET
compiled regex on these benchmarks. `04-ruff-noqa/real` recursive
went from 61.41x .NET compiled (3.38s vs 53.95ms) to 0.06x (3.33ms vs
53.95ms), a 16x speedup over .NET compiled.

## Full table

| rebar name | model | engine | median | ratio vs .NET compiled |
|---|---:|---|---:|---:|
| `curated/04-ruff-noqa/real` | `grep-captures` | .NET compiled | 53.95ms | 1.00x |
| `curated/04-ruff-noqa/real` | `grep-captures` | .NET NonBacktracking | 65.74ms | 1.22x |
| `curated/04-ruff-noqa/real` | `grep-captures` | InductorParser recursive | 3.33ms | **0.06x** |
| `curated/04-ruff-noqa/real` | `grep-captures` | InductorParser state machine | 3.50ms | **0.06x** |
| `curated/04-ruff-noqa/tweaked` | `grep-captures` | .NET compiled | 41.35ms | 1.00x |
| `curated/04-ruff-noqa/tweaked` | `grep-captures` | .NET NonBacktracking | 48.91ms | 1.18x |
| `curated/04-ruff-noqa/tweaked` | `grep-captures` | InductorParser recursive | 3.26ms | **0.08x** |
| `curated/04-ruff-noqa/tweaked` | `grep-captures` | InductorParser state machine | 3.26ms | **0.08x** |

The remaining InductorParser cost on these rows is the IndexOf
substring search across the 32MB haystack plus parsing the few
hundred candidate lines. .NET compiled regex doesn't get to start
from a "find this case-insensitive substring once" prefilter; it
scans line by line through its own state machine, which on this
particular haystack the BCL's substring search beats by a wide
margin.

## Unaffected rows

| rebar name | model | engine | median | ratio |
|---|---:|---|---:|---:|
| `curated/09-aws-keys/quick` | `grep` | InductorParser recursive | 250.31ms | 8.56x |
| `curated/09-aws-keys/quick` | `grep` | InductorParser state machine | 222.87ms | 7.62x |

The `09-aws-keys/quick` pattern is `(?:ASIA|AKIA|AROA|AIDA)([A-Z0-7]{16})`
— four 4-letter prefix alternatives with no shared substring beyond
'A'. A single-string IndexOf trigger doesn't help here. Closing
this row would need a multi-substring prefilter
(`SearchValues<string>` from .NET 9, or hand-rolled Aho-Corasick).

The literal-count rows (01-literal/*, 02-literal-alternate/*) and
the words rows (08-words/*) don't touch the grep path so their
numbers are unchanged from full-comparison-2026-04-28.csv.

## Outstanding rows over 5x .NET compiled

| rebar name | best engine | ratio | note |
|---|---|---:|---|
| `02-literal-alternate/sherlock-casei-en` | state machine | 27.99x | needs a multi-substring prefilter |
| `09-aws-keys/quick` | state machine | 7.52x | needs a multi-substring prefilter |
| `04-ruff-noqa/tweaked` | both | 0.08x | now better than .NET compiled |
| `08-words/all-english` | recursive | 10.88x | structural — tree construction cost |
| `08-words/long-english` | recursive | 7.22x | structural — tree construction cost |
