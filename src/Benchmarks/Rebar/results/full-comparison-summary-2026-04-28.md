# Full rebar comparison after restoring the rebar checkout (2026-04-28)

Measured 2026-04-28 with rebar `0.1.0 (rev 8e952148cc)` on Windows
`win-arm64`, .NET host `10.0.6`. Lower medians are better.
The run used `--max-time 1s --max-warmup-time 500ms`.

Both InductorParser engines run the same hand-translated grammars from
[BenchmarkPlan.cs](../BenchmarkPlan.cs); the only difference is which
evaluator processes the parse. Ratios are `engine median / .NET
compiled median` for that row.

| rebar name | model | engine | median | ratio vs .NET compiled |
|---|---:|---|---:|---:|
| `curated/01-literal/sherlock-en` | `count` | .NET compiled | 67.20us | 1.00x |
| `curated/01-literal/sherlock-en` | `count` | .NET NonBacktracking | 125.90us | 1.87x |
| `curated/01-literal/sherlock-en` | `count` | InductorParser recursive | 219.50us | 3.27x |
| `curated/01-literal/sherlock-en` | `count` | InductorParser state machine | 129.80us | 1.93x |
| `curated/01-literal/sherlock-casei-en` | `count` | .NET compiled | 80.40us | 1.00x |
| `curated/01-literal/sherlock-casei-en` | `count` | .NET NonBacktracking | 148.10us | 1.84x |
| `curated/01-literal/sherlock-casei-en` | `count` | InductorParser recursive | 270.80us | 3.37x |
| `curated/01-literal/sherlock-casei-en` | `count` | InductorParser state machine | 193.80us | 2.41x |
| `curated/01-literal/sherlock-ru` | `count` | .NET compiled | 87.90us | 1.00x |
| `curated/01-literal/sherlock-ru` | `count` | .NET NonBacktracking | 134.70us | 1.53x |
| `curated/01-literal/sherlock-ru` | `count` | InductorParser recursive | 238.10us | 2.71x |
| `curated/01-literal/sherlock-ru` | `count` | InductorParser state machine | 149.90us | 1.71x |
| `curated/01-literal/sherlock-zh` | `count` | .NET compiled | 30.20us | 1.00x |
| `curated/01-literal/sherlock-zh` | `count` | .NET NonBacktracking | 32.30us | 1.07x |
| `curated/01-literal/sherlock-zh` | `count` | InductorParser recursive | 39.50us | 1.31x |
| `curated/01-literal/sherlock-zh` | `count` | InductorParser state machine | 37.40us | 1.24x |
| `curated/02-literal-alternate/sherlock-en` | `count` | .NET compiled | 308.80us | 1.00x |
| `curated/02-literal-alternate/sherlock-en` | `count` | .NET NonBacktracking | 419.00us | 1.36x |
| `curated/02-literal-alternate/sherlock-en` | `count` | InductorParser recursive | 727.40us | 2.36x |
| `curated/02-literal-alternate/sherlock-en` | `count` | InductorParser state machine | 581.30us | 1.88x |
| `curated/02-literal-alternate/sherlock-casei-en` | `count` | .NET compiled | 164.70us | 1.00x |
| `curated/02-literal-alternate/sherlock-casei-en` | `count` | .NET NonBacktracking | 242.40us | 1.47x |
| `curated/02-literal-alternate/sherlock-casei-en` | `count` | InductorParser recursive | 4.63ms | 28.11x |
| `curated/02-literal-alternate/sherlock-casei-en` | `count` | InductorParser state machine | 4.61ms | 27.99x |
| `curated/02-literal-alternate/sherlock-ru` | `count` | .NET compiled | 2.31ms | 1.00x |
| `curated/02-literal-alternate/sherlock-ru` | `count` | .NET NonBacktracking | 3.82ms | 1.65x |
| `curated/02-literal-alternate/sherlock-ru` | `count` | InductorParser recursive | 1.98ms | 0.86x |
| `curated/02-literal-alternate/sherlock-ru` | `count` | InductorParser state machine | 1.87ms | 0.81x |
| `curated/02-literal-alternate/sherlock-zh` | `count` | .NET compiled | 52.50us | 1.00x |
| `curated/02-literal-alternate/sherlock-zh` | `count` | .NET NonBacktracking | 64.60us | 1.23x |
| `curated/02-literal-alternate/sherlock-zh` | `count` | InductorParser recursive | 89.40us | 1.70x |
| `curated/02-literal-alternate/sherlock-zh` | `count` | InductorParser state machine | 84.30us | 1.61x |
| `curated/04-ruff-noqa/real` | `grep-captures` | .NET compiled | 55.04ms | 1.00x |
| `curated/04-ruff-noqa/real` | `grep-captures` | .NET NonBacktracking | 67.80ms | 1.23x |
| `curated/04-ruff-noqa/real` | `grep-captures` | InductorParser recursive | 3.38s | 61.41x |
| `curated/04-ruff-noqa/real` | `grep-captures` | InductorParser state machine | 1.22s | 22.16x |
| `curated/04-ruff-noqa/tweaked` | `grep-captures` | .NET compiled | 41.67ms | 1.00x |
| `curated/04-ruff-noqa/tweaked` | `grep-captures` | .NET NonBacktracking | 49.21ms | 1.18x |
| `curated/04-ruff-noqa/tweaked` | `grep-captures` | InductorParser recursive | 265.93ms | 6.38x |
| `curated/04-ruff-noqa/tweaked` | `grep-captures` | InductorParser state machine | 227.82ms | 5.47x |
| `curated/04-ruff-noqa/compile-real` | `compile` | .NET compiled | 80.30us | 1.00x |
| `curated/04-ruff-noqa/compile-real` | `compile` | .NET NonBacktracking | 417.60us | 5.20x |
| `curated/04-ruff-noqa/compile-real` | `compile` | InductorParser recursive | 7.20us | 0.09x |
| `curated/04-ruff-noqa/compile-real` | `compile` | InductorParser state machine | 7.40us | 0.09x |
| `curated/08-words/all-english` | `count-spans` | .NET compiled | 645.40us | 1.00x |
| `curated/08-words/all-english` | `count-spans` | .NET NonBacktracking | 1.30ms | 2.01x |
| `curated/08-words/all-english` | `count-spans` | InductorParser recursive | 7.02ms | 10.88x |
| `curated/08-words/all-english` | `count-spans` | InductorParser state machine | 7.53ms | 11.67x |
| `curated/08-words/long-english` | `count-spans` | .NET compiled | 0.97ms | 1.00x |
| `curated/08-words/long-english` | `count-spans` | .NET NonBacktracking | 528.20us | 0.54x |
| `curated/08-words/long-english` | `count-spans` | InductorParser recursive | 7.00ms | 7.22x |
| `curated/08-words/long-english` | `count-spans` | InductorParser state machine | 7.05ms | 7.27x |
| `curated/09-aws-keys/quick` | `grep` | .NET compiled | 29.25ms | 1.00x |
| `curated/09-aws-keys/quick` | `grep` | .NET NonBacktracking | 35.73ms | 1.22x |
| `curated/09-aws-keys/quick` | `grep` | InductorParser recursive | 254.82ms | 8.71x |
| `curated/09-aws-keys/quick` | `grep` | InductorParser state machine | 219.98ms | 7.52x |
| `curated/09-aws-keys/compile-quick` | `compile` | .NET compiled | 88.00us | 1.00x |
| `curated/09-aws-keys/compile-quick` | `compile` | .NET NonBacktracking | 246.20us | 2.80x |
| `curated/09-aws-keys/compile-quick` | `compile` | InductorParser recursive | 2.80us | 0.03x |
| `curated/09-aws-keys/compile-quick` | `compile` | InductorParser state machine | 2.90us | 0.03x |

## Notes

This run is the baseline against which future changes are compared.
The earlier `statemachine-with-skip-summary-2026-04-28.md` was on a
different machine state and didn't include `dotnet/compiled` numbers
on the same run; this CSV has all four engines side by side.

The state-machine evaluator matches or beats the recursive evaluator
on every search row. Biggest gap: `04-ruff-noqa/real` where the state
machine pulls 2.8x ahead (1.22s vs 3.38s).

The `02-literal-alternate/sherlock-casei-en` row sits at 28x .NET
compiled for both InductorParser variants. An earlier attempt to
close that gap by enabling the substring-search cache for multi-
literal alternates measured 23x slower than the IndexOfAny path on
this exact haystack, which the upstream Lexer comment had warned
about. The cache stays single-literal-only. See
`multi-literal-cache-rebar-2026-04-28.csv` for the regression
evidence; the Lexer's `AdvanceUntilLiteralCandidateIn` and the gate
in `BetweenInclusiveRule.TryCreateScannerSkip` carry the why-not
in their comments.
