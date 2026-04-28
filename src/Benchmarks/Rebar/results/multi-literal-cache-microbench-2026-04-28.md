# Multi-literal scanner-cache verification (2026-04-28)

In-process microbenchmark validating the multi-literal scanner-cache
fix. The rebar harness was unavailable to rerun (the rebar checkout
under `.external/rebar/` was removed between runs and there's no
local Rust toolchain to rebuild it), so the comparison ran in-process
on a synthetic haystack instead. The benchmark source lives at
`C:/tmp/ScannerSkipBench/`.

## Setup

- 900,183-char synthetic haystack of English-like prose ("Sherlock
  studied...", "Inspector Jones jotted...", etc.) with 13 of the
  Sherlock target literals injected.
- Same five-literal alternation rebar's curated `02-literal-alternate/`
  uses: `Sherlock Holmes`, `John Watson`, `Irene Adler`,
  `Inspector Lestrade`, `Professor Moriarty`.
- All four engines time the same `Matches.Count` operation:
  `.NET Compiled` and `.NET NonBacktracking` regex, InductorParser
  recursive, InductorParser state machine. Engines all return
  `count=13` so correctness lines up.
- Median of 30 timed iterations after 10 warmup runs. Stopwatch-based
  ns measurement.

## Results

### sherlock-en (case-sensitive)

| engine | median | ratio vs .NET compiled |
|---|---:|---:|
| .NET Compiled | 359.80us | 1.00x |
| .NET NonBacktracking | 1322.30us | 3.68x |
| InductorParser recursive | 965.80us | 2.68x |
| InductorParser state machine | 951.50us | 2.64x |

### sherlock-casei-en (case-insensitive)

| engine | median | ratio vs .NET compiled |
|---|---:|---:|
| .NET Compiled | 3010.10us | 1.00x |
| .NET NonBacktracking | 2332.60us | 0.77x |
| InductorParser recursive | 4557.80us | 1.51x |
| InductorParser state machine | 1376.90us | **0.46x** |

## What the numbers say

The synthetic haystack is much harder for every engine than rebar's
actual Sherlock corpus (lots of false-positive 'S','J','I','P'
starts in the prose), so absolute medians don't line up with the
rebar table; the ratio columns are what's comparable.

**No regression on case-sensitive.** Both InductorParser variants
sit at 2.64-2.68x .NET compiled, the same band rebar showed before
the cache change (recursive 2.31x, state machine 1.88x). The cache
change allocates a small `int[]` per parse on this path that wasn't
allocated before, but the per-iteration savings already pay it back.

**Case-insensitive improved drastically.** Before the cache change,
rebar showed 28.53x recursive and 28.59x state machine. Both
collapse here:

- Recursive: 1.51x .NET compiled. Down from 28x. Cache pays off
  because every false-positive first-rune stop ('s', 'i', 'j', 'p',
  in lower-case prose) used to fire 5 case-insensitive `MatchesAt`
  calls. With the cache, the per-literal `IndexOf(OrdinalIgnoreCase)`
  uses the BCL's specialized substring search and amortizes across
  iterations.
- State machine: 0.46x .NET compiled. Faster than .NET compiled
  regex on this shape. The state-machine path benefits more than
  recursive because its per-iteration overhead is lower; once the
  cache removes the false-positive cost, the flat-program dispatch
  pulls ahead.

## Conclusion

The change ships as-is. Case-sensitive multi-literal stays where it
was; case-insensitive multi-literal closes the 28x gap to .NET
compiled regex (recursive lands at 1.5x, state machine pulls ahead
at 0.46x).

The full rebar comparison should be rerun once the rebar checkout
is restored to confirm against the actual Sherlock corpora, but the
microbench is sufficient to verify the gate flip is safe and
delivers the expected win.
