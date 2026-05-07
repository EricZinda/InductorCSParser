# State machine vs. recursive after scanner-skip port (2026-04-28)

Measured 2026-04-28 with rebar `0.1.0 (rev 8e952148cc)` on Windows
`win-arm64`, .NET host `10.0.6`. Lower medians are better. The run used
`--max-time 1s --max-warmup-time 500ms`.

This run includes the scanner-shape skip ported into the state-machine
evaluator. Both InductorParser engines run the same hand-translated
grammars from [BenchmarkPlan.cs](../BenchmarkPlan.cs); the only
difference is which evaluator processes the parse.

The earlier comparison (`statemachine-vs-recursive-summary-2026-04-28.md`)
showed the state machine running 50-300x slower than the recursive
evaluator on the literal-count benchmarks because the recursive
evaluator had a `ZeroOrMore(Or(match, AnyToken.Delete))`
scanner-shape skip that the state machine lacked. That skip is now
implemented as the `ScannerSkipAdvance` opcode in the state-machine
path, with the same detection logic as
`BetweenInclusiveRule.TryCreateScannerSkip`.

Ratios are `engine median / .NET compiled median` for that row.

| rebar name | model | engine | median | ratio vs .NET compiled |
|---|---:|---|---:|---:|
| `curated/01-literal/sherlock-en` | `count` | .NET compiled | 66.00us | 1.00x |
| `curated/01-literal/sherlock-en` | `count` | .NET NonBacktracking | 125.70us | 1.90x |
| `curated/01-literal/sherlock-en` | `count` | InductorParser recursive | 214.30us | 3.25x |
| `curated/01-literal/sherlock-en` | `count` | InductorParser state machine | 134.70us | 2.04x |
| `curated/01-literal/sherlock-casei-en` | `count` | .NET compiled | 82.60us | 1.00x |
| `curated/01-literal/sherlock-casei-en` | `count` | .NET NonBacktracking | 147.50us | 1.79x |
| `curated/01-literal/sherlock-casei-en` | `count` | InductorParser recursive | 270.70us | 3.28x |
| `curated/01-literal/sherlock-casei-en` | `count` | InductorParser state machine | 191.70us | 2.32x |
| `curated/01-literal/sherlock-ru` | `count` | .NET compiled | 87.30us | 1.00x |
| `curated/01-literal/sherlock-ru` | `count` | .NET NonBacktracking | 133.90us | 1.53x |
| `curated/01-literal/sherlock-ru` | `count` | InductorParser recursive | 236.70us | 2.71x |
| `curated/01-literal/sherlock-ru` | `count` | InductorParser state machine | 147.50us | 1.69x |
| `curated/01-literal/sherlock-zh` | `count` | .NET compiled | 30.20us | 1.00x |
| `curated/01-literal/sherlock-zh` | `count` | .NET NonBacktracking | 32.30us | 1.07x |
| `curated/01-literal/sherlock-zh` | `count` | InductorParser recursive | 39.50us | 1.31x |
| `curated/01-literal/sherlock-zh` | `count` | InductorParser state machine | 37.20us | 1.23x |
| `curated/02-literal-alternate/sherlock-en` | `count` | .NET compiled | 313.80us | 1.00x |
| `curated/02-literal-alternate/sherlock-en` | `count` | .NET NonBacktracking | 403.90us | 1.29x |
| `curated/02-literal-alternate/sherlock-en` | `count` | InductorParser recursive | 725.20us | 2.31x |
| `curated/02-literal-alternate/sherlock-en` | `count` | InductorParser state machine | 590.70us | 1.88x |
| `curated/02-literal-alternate/sherlock-casei-en` | `count` | .NET compiled | 160.90us | 1.00x |
| `curated/02-literal-alternate/sherlock-casei-en` | `count` | .NET NonBacktracking | 241.60us | 1.50x |
| `curated/02-literal-alternate/sherlock-casei-en` | `count` | InductorParser recursive | 4.59ms | 28.53x |
| `curated/02-literal-alternate/sherlock-casei-en` | `count` | InductorParser state machine | 4.60ms | 28.59x |
| `curated/02-literal-alternate/sherlock-ru` | `count` | .NET compiled | 2.31ms | 1.00x |
| `curated/02-literal-alternate/sherlock-ru` | `count` | .NET NonBacktracking | 3.80ms | 1.65x |
| `curated/02-literal-alternate/sherlock-ru` | `count` | InductorParser recursive | 1.99ms | 0.86x |
| `curated/02-literal-alternate/sherlock-ru` | `count` | InductorParser state machine | 1.85ms | 0.80x |
| `curated/02-literal-alternate/sherlock-zh` | `count` | .NET compiled | 52.60us | 1.00x |
| `curated/02-literal-alternate/sherlock-zh` | `count` | .NET NonBacktracking | 64.30us | 1.22x |
| `curated/02-literal-alternate/sherlock-zh` | `count` | InductorParser recursive | 90.10us | 1.71x |
| `curated/02-literal-alternate/sherlock-zh` | `count` | InductorParser state machine | 84.80us | 1.61x |
| `curated/04-ruff-noqa/real` | `grep-captures` | .NET compiled | 54.97ms | 1.00x |
| `curated/04-ruff-noqa/real` | `grep-captures` | .NET NonBacktracking | 66.78ms | 1.21x |
| `curated/04-ruff-noqa/real` | `grep-captures` | InductorParser recursive | 3.28s | 59.67x |
| `curated/04-ruff-noqa/real` | `grep-captures` | InductorParser state machine | 1.24s | 22.56x |
| `curated/04-ruff-noqa/tweaked` | `grep-captures` | .NET compiled | 41.47ms | 1.00x |
| `curated/04-ruff-noqa/tweaked` | `grep-captures` | .NET NonBacktracking | 48.34ms | 1.17x |
| `curated/04-ruff-noqa/tweaked` | `grep-captures` | InductorParser recursive | 254.63ms | 6.14x |
| `curated/04-ruff-noqa/tweaked` | `grep-captures` | InductorParser state machine | 230.20ms | 5.55x |
| `curated/04-ruff-noqa/compile-real` | `compile` | .NET compiled | 92.35us | 1.00x |
| `curated/04-ruff-noqa/compile-real` | `compile` | .NET NonBacktracking | 418.60us | 4.53x |
| `curated/04-ruff-noqa/compile-real` | `compile` | InductorParser recursive | 7.30us | 0.08x |
| `curated/04-ruff-noqa/compile-real` | `compile` | InductorParser state machine | 7.30us | 0.08x |
| `curated/08-words/all-english` | `count-spans` | .NET compiled | 642.70us | 1.00x |
| `curated/08-words/all-english` | `count-spans` | .NET NonBacktracking | 1.31ms | 2.04x |
| `curated/08-words/all-english` | `count-spans` | InductorParser recursive | 7.30ms | 11.36x |
| `curated/08-words/all-english` | `count-spans` | InductorParser state machine | 7.88ms | 12.26x |
| `curated/08-words/long-english` | `count-spans` | .NET compiled | 0.96ms | 1.00x |
| `curated/08-words/long-english` | `count-spans` | .NET NonBacktracking | 543.30us | 0.57x |
| `curated/08-words/long-english` | `count-spans` | InductorParser recursive | 7.05ms | 7.34x |
| `curated/08-words/long-english` | `count-spans` | InductorParser state machine | 7.24ms | 7.54x |
| `curated/09-aws-keys/quick` | `grep` | .NET compiled | 29.40ms | 1.00x |
| `curated/09-aws-keys/quick` | `grep` | .NET NonBacktracking | 35.12ms | 1.19x |
| `curated/09-aws-keys/quick` | `grep` | InductorParser recursive | 253.12ms | 8.61x |
| `curated/09-aws-keys/quick` | `grep` | InductorParser state machine | 224.03ms | 7.62x |
| `curated/09-aws-keys/compile-quick` | `compile` | .NET compiled | 83.00us | 1.00x |
| `curated/09-aws-keys/compile-quick` | `compile` | .NET NonBacktracking | 244.00us | 2.94x |
| `curated/09-aws-keys/compile-quick` | `compile` | InductorParser recursive | 2.80us | 0.03x |
| `curated/09-aws-keys/compile-quick` | `compile` | InductorParser state machine | 2.90us | 0.03x |

## Reading the numbers

The state-machine evaluator now wins or ties the recursive evaluator
on every search row in this comparison. The biggest moves are on the
literal-count cases the earlier run highlighted:

| benchmark | before scanner-skip port | after | speedup |
|---|---:|---:|---:|
| `01-literal/sherlock-en` | 16.79ms | 134.70us | 125x |
| `01-literal/sherlock-zh` | 9.75ms | 37.20us | 262x |
| `02-literal-alternate/sherlock-en` | 17.53ms | 590.70us | 30x |
| `02-literal-alternate/sherlock-ru` | 26.75ms | 1.85ms | 14x |
| `04-ruff-noqa/real` | 1.48s | 1.24s | 1.2x |
| `09-aws-keys/quick` | 869ms | 224ms | 3.9x |

The literal cases bench close to the recursive evaluator (1.06-1.60x
faster) because both are now dominated by the BCL's
`String.IndexOf(ordinal)` / `IndexOfAny` substring search. The state
machine sits a fraction lower because its outer-loop dispatch is one
opcode rather than a virtual `TryParseRule` call plus per-rule
transaction setup, but the lion's share of the work is the BCL search
either way.

The captures-heavy `04-ruff-noqa/real` shows the biggest gap (2.6x
faster than recursive). That row spends most of its time inside the
`match` rule actually parsing captures, which is where the
state-machine's flat-program dispatch consistently wins against the
recursive evaluator's per-rule overhead.

`08-words/all-english` and `long-english` are roughly tied between the
two evaluators because RuneRunRule does the same bulk-rune walk in
both paths. The state-machine path is within noise (1.03-1.08x).

## Implementation notes

The port lives in three small pieces:

- `LoweredOpCode.ScannerSkipAdvance`: new opcode whose body calls
  `Lexer.AdvanceUntilLiteralCandidateIn` (or `AdvanceUntilRuneIn`)
  with the precomputed spec.
- `CompiledProgram.ScannerSkipSpecs`: new side table carrying the
  candidate `RuneSet`, optional BMP first-char array (for the
  `IndexOfAny` fast path), optional `LiteralScannerCandidate[]` (for
  the stronger literal prefilter), and a flag for whether to allocate
  the per-position substring-search cache.
- `Lowerer.TryLowerBetweenScanner`: detection logic mirroring the
  recursive `TryCreateScannerSkip`. Same gating: AtLeast=0,
  AtMost=int.MaxValue, Inner is `Or` whose last alternative is a
  deleted `AnyToken`, candidate runes are non-empty and not
  `Universe`, no `WithError` on the bits we can't attribute.

The opcode is inert when the lowerer doesn't recognize the shape (it
is never emitted), so syntax-tree output never changes for grammars
that don't fit the pattern. Same correctness invariant the recursive
evaluator's `ScannerSkip` carries.

The substring-search cache (`int[]` per parse, one slot per literal)
mirrors the recursive evaluator's `CreateUnknownPositions`: only
allocated for the single-literal case, where each iteration would
otherwise re-run a substring search across the whole remaining
haystack. Multi-literal alternates use the per-position
`IndexOfAny`-based path which doesn't need the cache.
