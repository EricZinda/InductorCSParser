# State machine vs. recursive evaluator on rebar curated benchmarks

Measured 2026-04-28 with rebar `0.1.0 (rev 8e952148cc)` on Windows
`win-arm64`, .NET host `10.0.6`. Lower medians are better. The run used
`--max-time 1s --max-warmup-time 500ms`.

Both InductorParser engines run the same hand-translated grammars from
[BenchmarkPlan.cs](../BenchmarkPlan.cs). The only difference is which
evaluator processes the parse: `inductorparser` calls `Rule.Parse` (the
recursive evaluator), `inductorparser-statemachine` calls
`StateMachineParser.Parse` (the state-machine evaluator).

Ratios are `engine median / .NET compiled median` for that row.

| rebar name | model | engine | median | ratio vs .NET compiled |
|---|---:|---|---:|---:|
| `curated/01-literal/sherlock-en` | `count` | .NET compiled | 66.00us | 1.00x |
| `curated/01-literal/sherlock-en` | `count` | .NET NonBacktracking | 125.30us | 1.90x |
| `curated/01-literal/sherlock-en` | `count` | InductorParser recursive | 212.70us | 3.22x |
| `curated/01-literal/sherlock-en` | `count` | InductorParser state machine | 16.79ms | 254.39x |
| `curated/01-literal/sherlock-casei-en` | `count` | .NET compiled | 82.90us | 1.00x |
| `curated/01-literal/sherlock-casei-en` | `count` | .NET NonBacktracking | 147.90us | 1.78x |
| `curated/01-literal/sherlock-casei-en` | `count` | InductorParser recursive | 268.20us | 3.23x |
| `curated/01-literal/sherlock-casei-en` | `count` | InductorParser state machine | 17.86ms | 215.44x |
| `curated/01-literal/sherlock-ru` | `count` | .NET compiled | 87.00us | 1.00x |
| `curated/01-literal/sherlock-ru` | `count` | .NET NonBacktracking | 133.80us | 1.54x |
| `curated/01-literal/sherlock-ru` | `count` | InductorParser recursive | 237.20us | 2.73x |
| `curated/01-literal/sherlock-ru` | `count` | InductorParser state machine | 19.46ms | 223.68x |
| `curated/01-literal/sherlock-zh` | `count` | .NET compiled | 30.20us | 1.00x |
| `curated/01-literal/sherlock-zh` | `count` | .NET NonBacktracking | 32.40us | 1.07x |
| `curated/01-literal/sherlock-zh` | `count` | InductorParser recursive | 39.40us | 1.30x |
| `curated/01-literal/sherlock-zh` | `count` | InductorParser state machine | 9.75ms | 322.85x |
| `curated/02-literal-alternate/sherlock-en` | `count` | .NET compiled | 311.40us | 1.00x |
| `curated/02-literal-alternate/sherlock-en` | `count` | .NET NonBacktracking | 399.55us | 1.28x |
| `curated/02-literal-alternate/sherlock-en` | `count` | InductorParser recursive | 719.20us | 2.31x |
| `curated/02-literal-alternate/sherlock-en` | `count` | InductorParser state machine | 17.53ms | 56.30x |
| `curated/02-literal-alternate/sherlock-casei-en` | `count` | .NET compiled | 164.40us | 1.00x |
| `curated/02-literal-alternate/sherlock-casei-en` | `count` | .NET NonBacktracking | 240.80us | 1.46x |
| `curated/02-literal-alternate/sherlock-casei-en` | `count` | InductorParser recursive | 4.56ms | 27.74x |
| `curated/02-literal-alternate/sherlock-casei-en` | `count` | InductorParser state machine | 22.39ms | 136.19x |
| `curated/02-literal-alternate/sherlock-ru` | `count` | .NET compiled | 2.30ms | 1.00x |
| `curated/02-literal-alternate/sherlock-ru` | `count` | .NET NonBacktracking | 3.79ms | 1.65x |
| `curated/02-literal-alternate/sherlock-ru` | `count` | InductorParser recursive | 1.95ms | 0.85x |
| `curated/02-literal-alternate/sherlock-ru` | `count` | InductorParser state machine | 26.75ms | 11.63x |
| `curated/02-literal-alternate/sherlock-zh` | `count` | .NET compiled | 52.80us | 1.00x |
| `curated/02-literal-alternate/sherlock-zh` | `count` | .NET NonBacktracking | 64.10us | 1.21x |
| `curated/02-literal-alternate/sherlock-zh` | `count` | InductorParser recursive | 89.20us | 1.69x |
| `curated/02-literal-alternate/sherlock-zh` | `count` | InductorParser state machine | 10.55ms | 199.81x |
| `curated/04-ruff-noqa/real` | `grep-captures` | .NET compiled | 53.83ms | 1.00x |
| `curated/04-ruff-noqa/real` | `grep-captures` | .NET NonBacktracking | 65.17ms | 1.21x |
| `curated/04-ruff-noqa/real` | `grep-captures` | InductorParser recursive | 3.22s | 59.82x |
| `curated/04-ruff-noqa/real` | `grep-captures` | InductorParser state machine | 1.48s | 27.49x |
| `curated/04-ruff-noqa/tweaked` | `grep-captures` | .NET compiled | 40.97ms | 1.00x |
| `curated/04-ruff-noqa/tweaked` | `grep-captures` | .NET NonBacktracking | 48.53ms | 1.18x |
| `curated/04-ruff-noqa/tweaked` | `grep-captures` | InductorParser recursive | 314.98ms | 7.69x |
| `curated/04-ruff-noqa/tweaked` | `grep-captures` | InductorParser state machine | 882.54ms | 21.54x |
| `curated/04-ruff-noqa/compile-real` | `compile` | .NET compiled | 78.30us | 1.00x |
| `curated/04-ruff-noqa/compile-real` | `compile` | .NET NonBacktracking | 405.80us | 5.18x |
| `curated/04-ruff-noqa/compile-real` | `compile` | InductorParser recursive | 7.10us | 0.09x |
| `curated/04-ruff-noqa/compile-real` | `compile` | InductorParser state machine | 7.20us | 0.09x |
| `curated/08-words/all-english` | `count-spans` | .NET compiled | 643.10us | 1.00x |
| `curated/08-words/all-english` | `count-spans` | .NET NonBacktracking | 1.31ms | 2.04x |
| `curated/08-words/all-english` | `count-spans` | InductorParser recursive | 6.99ms | 10.87x |
| `curated/08-words/all-english` | `count-spans` | InductorParser state machine | 6.86ms | 10.67x |
| `curated/08-words/long-english` | `count-spans` | .NET compiled | 0.96ms | 1.00x |
| `curated/08-words/long-english` | `count-spans` | .NET NonBacktracking | 531.40us | 0.55x |
| `curated/08-words/long-english` | `count-spans` | InductorParser recursive | 6.90ms | 7.19x |
| `curated/08-words/long-english` | `count-spans` | InductorParser state machine | 4.02ms | 4.19x |
| `curated/09-aws-keys/quick` | `grep` | .NET compiled | 29.38ms | 1.00x |
| `curated/09-aws-keys/quick` | `grep` | .NET NonBacktracking | 34.95ms | 1.19x |
| `curated/09-aws-keys/quick` | `grep` | InductorParser recursive | 294.97ms | 10.04x |
| `curated/09-aws-keys/quick` | `grep` | InductorParser state machine | 869.43ms | 29.59x |
| `curated/09-aws-keys/compile-quick` | `compile` | .NET compiled | 89.30us | 1.00x |
| `curated/09-aws-keys/compile-quick` | `compile` | .NET NonBacktracking | 243.90us | 2.73x |
| `curated/09-aws-keys/compile-quick` | `compile` | InductorParser recursive | 2.80us | 0.03x |
| `curated/09-aws-keys/compile-quick` | `compile` | InductorParser state machine | 2.80us | 0.03x |

## Reading the numbers

The state machine wins on three rows: `curated/04-ruff-noqa/real`
(2.18x faster than recursive), `curated/08-words/long-english`
(1.72x faster), and a tie on `curated/08-words/all-english` and the
two `compile` rows. It loses on every other row, often badly:
50-300x slower than recursive on the literal-count benchmarks.

The reason is the scanner-shape skip optimization. The recursive
evaluator has a fast path for the
`ZeroOrMore(Or(match, AnyToken.Delete))` shape that the rebar
runner builds for every search benchmark. When the next input rune
can't possibly start a `match`, the scanner skips it directly
without calling into `match` and getting back a failure. That
optimization is what keeps the recursive evaluator at 1-3x .NET
compiled on the literal cases.

The state-machine evaluator doesn't have the same skip implemented.
It compiles the scanner shape into a generic Or loop that
attempts `match` at every position, takes the failure, then falls
through to consume one token. On a 900KB haystack with thousands of
positions where the literal could conceivably start, that's the
50-300x slowdown.

The two cases where the state machine wins (`ruff-noqa/real`,
`long-english`) are the cases where most of the work is inside
`match` rather than at scanner boundaries. There the state machine's
flat-program dispatch shows up favorably against the recursive
evaluator's per-rule virtual dispatch and transaction setup.

This is a known gap that calls for porting the scanner-shape skip
from the recursive path into the state-machine path. Until that
lands, the state-machine evaluator should not be picked over the
recursive one for regex-style scanning benchmarks. The JSON-style
benchmarks in the BenchmarkDotNet suite (where the grammar drives
the parse end-to-end rather than scanning a haystack for sparse
matches) tell a different story; see
[../README.md](../README.md) for those.
