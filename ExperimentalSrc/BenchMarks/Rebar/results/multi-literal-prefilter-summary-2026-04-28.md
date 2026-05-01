# Multi-literal grep prefilter (2026-04-28)

Measured 2026-04-28 with rebar `0.1.0 (rev 8e952148cc)` on Windows
`win-arm64`, .NET host `10.0.6`. Lower medians are better. The run used
`--max-time 1s --max-warmup-time 500ms`.

Adds a multi-literal version of the existing required-literal grep
prefilter. Before this change the runner asked the match rule for a
single shared substring (`Rule.TryGetRequiredLiteral`); when the rule
didn't have one (the AWS-keys grammar's `(?:ASIA|AKIA|AROA|AIDA)` is
the canonical case), the runner walked every line of the haystack and
parsed each. Now there's a fallback path: ask for a small set of
literal alternatives (`Rule.TryGetRequiredLiteralAlternatives`), and
when the analysis returns one, pre-skip lines that contain none of
them via `String.IndexOfAny` over the unique first chars plus a
`string.Compare` per first-char hit. Mirrors what the parser-side
`BetweenInclusiveRule.TryCreateScannerSkip` already does for the
`ZeroOrMore(FirstOf(literal-choice, AnyToken.Delete))` scanner shape,
just hoisted to the runner's per-line iteration.

The win shows up almost entirely on the two AWS-keys grep rows. Other
grep / grep-captures rows already had a derivable single literal
(`# noqa` for ruff, the timestamp/level shape for unstructured-to-json
doesn't currently get one but isn't a multi-literal candidate either),
so they're unchanged.

## Headline numbers for `09-aws-keys`

| benchmark | engine | before | after | speedup |
|---|---|---:|---:|---:|
| `09-aws-keys/quick` | InductorParser recursive | 253.12ms | 5.07ms | **49.9x** |
| `09-aws-keys/quick` | InductorParser state machine | 224.03ms | 4.95ms | **45.3x** |
| `09-aws-keys/full` | InductorParser recursive | 451.67ms | 7.06ms | **64.0x** |
| `09-aws-keys/full` | InductorParser state machine | 280.42ms | 6.87ms | **40.8x** |

"Before" = numbers from `statemachine-with-skip-summary-2026-04-28.md`
(quick) and `new-benchmarks-summary-2026-04-28.md` (full). "After" =
this run.

Both rows now beat `.NET compiled`:

| benchmark | engine | median | ratio vs .NET compiled |
|---|---|---:|---:|
| `09-aws-keys/quick` | .NET compiled | 29.20ms | 1.00x |
| `09-aws-keys/quick` | .NET NonBacktracking | 34.93ms | 1.20x |
| `09-aws-keys/quick` | InductorParser recursive | 5.07ms | 0.17x |
| `09-aws-keys/quick` | InductorParser state machine | 4.95ms | 0.17x |
| `09-aws-keys/full` | .NET compiled | 39.44ms | 1.00x |
| `09-aws-keys/full` | InductorParser recursive | 7.06ms | 0.18x |
| `09-aws-keys/full` | InductorParser state machine | 6.87ms | 0.17x |

## Why the prefilter helps so much here

cpython-226484e4.py is 32MB, ~1M lines. Almost no line contains an
AWS access key (the bench's expected count is 0). Without a
prefilter, the runner had to invoke the parser on every line just
to discover that. With the prefilter:

1. The match-rule analysis sees `AllOf(FirstOf(Literal("ASIA"), Literal("AKIA"),
   Literal("AROA"), Literal("AIDA")), Exactly(16, ...))` and walks
   into the FirstOf, returning the four literals as a required-set.
2. The runner's IndexOfAny over `['A']` (all four literals share a
   first char) walks the haystack in one SIMD-tuned pass.
3. Each candidate position runs four cheap string compares; the
   parser is only invoked when one of the four prefixes actually
   matches.

The cpython haystack has plenty of capital A's, but nearly none
followed by a real AWS-key prefix, so the parser is invoked
approximately zero times in practice. The runner is essentially
just doing a tuned IndexOfAny + four memcmp's per hit.

## Why `04-ruff-noqa` and others didn't change

`04-ruff-noqa/real` and `04-ruff-noqa/tweaked` already had a single
required literal (`# noqa`, derived from the
`AllOf(Literal("# "), OneOf("Nn"), OneOf("Oo"), ...)` shape).
The new multi-literal path only kicks in when the single-literal
analysis fails, so those rows take the existing fast path and stay
where they were:

| benchmark | engine | median | ratio vs .NET compiled |
|---|---|---:|---:|
| `04-ruff-noqa/real` | .NET compiled | 53.53ms | 1.00x |
| `04-ruff-noqa/real` | InductorParser state machine | 3.26ms | 0.06x |
| `04-ruff-noqa/tweaked` | .NET compiled | 40.52ms | 1.00x |
| `04-ruff-noqa/tweaked` | InductorParser state machine | 3.24ms | 0.08x |

The `07-unicode-character-data/parse-line` and
`11-unstructured-to-json/extract` rows don't benefit from any
prefilter: every line of those haystacks matches the rule
(UnicodeData.txt and the log corpus are both well-formed by
construction), so there are no non-matching lines for a prefilter
to skip.

## Implementation notes

Three pieces added across two files:

- `Rule.TryGetRequiredLiteralAlternatives(maxAlternatives, out alternatives)`:
  caps the candidate-set size and returns the small set of literals
  derived from the rule's structure. Cap defaults to 8 in the runner
  (covers the AWS-keys 4-prefix case with headroom; keeps a 2,663-
  literal English-dictionary FirstOf from accidentally feeding the
  prefilter).
- `FirstOfRule.ComputeRequiredLiteralAlternatives`: union of children's
  required literals when every child has one. Recurses into a
  child's own multi-literal set for nested FirstOfs.
- `AllOfRule.ComputeRequiredLiteralAlternatives`: surfaces the first
  child's multi-literal set if any child has one. Single-literal AllOf
  shapes (like ruff's `# noqa`) keep using the existing
  `ComputeRequiredLiteral` walker, which the caller checks first.

Runner side: `BenchmarkPlan.CompileScanner` falls through to
`TryGetRequiredLiteralAlternatives` when the single-literal analysis
returns false, and `CandidateLines` picks one of three paths
(no-trigger → walk all lines, single-trigger → existing
substring-search loop, multi-trigger → IndexOfAny + per-position
string.Compare). The first-char set folds ASCII case when any
alternative is case-insensitive, mirroring `LiteralIgnoreAsciiCaseRule`'s
match semantics.

All 35 supported correctness cases still return `OK` for both the
recursive and state-machine evaluators (70 OK lines total). The
self-test stays green.
