# InductorParser rebar runner

This directory contains an optional [rebar](https://github.com/BurntSushi/rebar)
runner for comparing InductorParser with regex engines. It is intentionally
separate from the main `InductorParser.sln`; use `src/Benchmarks/Benchmarks.sln`
or this project directly when you want the external regex-engine barometer.

## License and provenance

This directory is not a fork of rebar. The runner is an independent .NET
implementation of rebar's runner protocol (KLV stdin, `duration,count` stdout
samples) as described in
[KLV.md](https://github.com/BurntSushi/rebar/blob/master/KLV.md),
[FORMAT.md](https://github.com/BurntSushi/rebar/blob/master/FORMAT.md), and
[BYOB.md](https://github.com/BurntSushi/rebar/blob/master/BYOB.md). No rebar
source code is reused. Rebar itself is dual-licensed under MIT and the
Unlicense.

The benchmark *names* used here (for example `curated/01-literal/sherlock-en`)
match rebar's curated benchmark identifiers on purpose. Rebar groups all the
engine results for a single benchmark on one row, so if our runner answered
to its own made-up names we'd just be running a separate benchmark that
nobody could line up against .NET regex, RE2, PCRE2, and friends. Reusing
rebar's names is what makes the comparison apples to apples.

The benchmark *haystacks* are not committed to this repo. They live in the
rebar checkout at `.external/rebar/benchmarks/haystacks/` (Sherlock Holmes
text in English, Russian, and Chinese, the Ruff `# noqa` corpus,
OpenSubtitles English samples for `08-words`, the AWS-key haystack, and so
on), which is gitignored. Two reasons for keeping them there instead of
copying them in. First, those corpora aren't ours to redistribute. Each one
carries its own license inside rebar, and copying them into this tree would
mean re-committing to all of those licenses for no real gain. Second, the
expected match counts that correctness mode checks against are baked into
rebar's TOML against rebar's own copy of each haystack. Keeping the
haystacks where rebar already keeps them avoids drift between the bytes
we test against and the counts we get verified against.

When rebar drives the runner, it reads each haystack from `.external/rebar/`
and pipes the bytes in over KLV stdin. The only strings stored under *this*
directory are the short synthetic haystacks in `--self-test` in `Program.cs`,
which are hand-written sanity-check inputs, not corpora.

## Build

From the repo root:

```powershell
dotnet build -c Release src/Benchmarks/Rebar/RebarRunner.csproj -m:1 -p:BuildInParallel=false
```

Local contract check:

```powershell
dotnet run -c Release --project src/Benchmarks/Rebar/RebarRunner.csproj -- --self-test
```

## Runner contract

The executable follows rebar's runner protocol:

- `--version` prints the runner version.
- `inductorparser` reads one benchmark execution from KLV on stdin.
- stdout contains one `duration,count` sample per measured iteration, where
  `duration` is nanoseconds.

The runner expects UTF-8 haystacks. rebar permits arbitrary bytes, but the
current hand-translated InductorParser subset is text-oriented.

## Supported rebar subset

The runner doesn't compile regex strings. It can't, because InductorParser
isn't a regex engine. It's a grammar API where you build `Rule` objects in C#
out of combinators (`Literal`, `FirstOf`, `AllOf`, `RuneRun`, and so on). For every
rebar case the runner supports, somebody sat down and wrote a C# grammar that
matches the same things the regex would match on the same haystack. That's
what "hand translation" means here. The dispatcher is a `switch` on the rebar
benchmark name in [BenchmarkPlan.cs](./BenchmarkPlan.cs). Ask the runner for a
name that isn't in that switch and it throws.

A hand translation only counts as correct once rebar agrees with it. Rebar
runs each engine in correctness mode (`rebar measure -t`) and compares the
engine's reported count against the expected count baked into the benchmark's
TOML. If the InductorParser grammar matches a different number of things than
the regex would, the case fails and shows up as `FAIL`, not as a fast number.

What the columns mean:

- *rebar name* is rebar's identifier for the benchmark. It points at a
  (regex, haystack) pair in `benchmarks/definitions/curated/*.toml` inside
  the rebar checkout, and it's what `rebar measure -f '^...$'` filters on.
- *model* is rebar's word for the job the engine has to do over that pair.
  Different models ask different questions of the same input:
  - `count`: number of matches in the haystack.
  - `count-spans`: total bytes of all matches (sum of match lengths).
  - `grep`: line-oriented, the count is lines with at least one match.
  - `grep-captures`: same line orientation, but the reported count includes
    captured groups.
  - `compile`: measure how long it takes to construct the engine, not to
    search. For InductorParser that means building the `Rule` and calling
    `Compile()`, since there's no regex string to parse.
- *notes* is a short reminder of what's particular about that row (language
  of the corpus, ASCII vs Unicode, what the `compile` rows are timing).

| rebar name | model | notes |
|---|---:|---|
| `curated/01-literal/sherlock-en` | `count` | exact ASCII literal |
| `curated/01-literal/sherlock-casei-en` | `count` | ASCII case-insensitive literal |
| `curated/01-literal/sherlock-ru` | `count` | exact Unicode literal |
| `curated/01-literal/sherlock-zh` | `count` | exact Unicode literal |
| `curated/02-literal-alternate/sherlock-en` | `count` | exact ASCII alternates |
| `curated/02-literal-alternate/sherlock-casei-en` | `count` | ASCII case-insensitive alternates |
| `curated/02-literal-alternate/sherlock-ru` | `count` | exact Unicode alternates |
| `curated/02-literal-alternate/sherlock-zh` | `count` | exact Unicode alternates |
| `curated/04-ruff-noqa/real` | `grep-captures` | capture count includes the matching line plus non-empty captures |
| `curated/04-ruff-noqa/tweaked` | `grep-captures` | same capture counting as upstream runners |
| `curated/04-ruff-noqa/compile-real` | `compile` | measured operation is grammar construction and `Compile()` |
| `curated/08-words/all-english` | `count-spans` | ASCII word spans |
| `curated/08-words/long-english` | `count-spans` | ASCII words of length 12+ |
| `curated/09-aws-keys/quick` | `grep` | quick AWS key detector |
| `curated/09-aws-keys/compile-quick` | `compile` | measured operation is grammar construction and `Compile()` |

Intentionally unsupported for now:

- Unicode-aware case-insensitive matches.
- Backreferences and lookbehind.
- Multi-pattern regex sets.
- The `curated/03-date/*` monster regex until the regex-to-InductorParser
  converter exists or the date tokenizer is translated by hand.
- Unicode `curated/08-words/*` cases until the desired `\b`/`\w` semantics are
  specified independently of each regex engine.
- The full AWS detector, which spans multiple lines and has many capture slots.

## How to run it yourself

You need three things: the .NET runner built, a `rebar` binary built, and an
`inductorparser` engine registered in rebar's `engines.toml`. The runner and
rebar then talk to each other over the KLV protocol described above.

The instructions below assume rebar is cloned to `.external/rebar/` (gitignored)
so the engine entry's `cwd` lines up out of the box. You can put rebar
anywhere else as long as you adjust `cwd` to point back at this repo.

### 1. Build the .NET runner

```powershell
dotnet build -c Release src/Benchmarks/Rebar/RebarRunner.csproj -m:1 -p:BuildInParallel=false
```

Optional sanity check that doesn't need rebar in the loop:

```powershell
dotnet run -c Release --project src/Benchmarks/Rebar/RebarRunner.csproj -- --self-test
```

### 2. Clone and build rebar

You need rust and `cargo` installed.

```powershell
git clone https://github.com/BurntSushi/rebar.git .external/rebar
cargo build --release --manifest-path .external/rebar/Cargo.toml
```

### 3. Register the InductorParser engine in rebar

Rebar only runs engines that are listed in `benchmarks/engines.toml` and
opted into in each curated benchmark's `engines = [...]` list. Add both:

- Append the `[[engine]]` block from
  [`rebar-engine.inductorparser.toml`](./rebar-engine.inductorparser.toml)
  to `.external/rebar/benchmarks/engines.toml`.
- Add `'inductorparser'` to the `engines = [...]` list inside each curated
  definition file you want to run, in
  `.external/rebar/benchmarks/definitions/curated/`. The supported set today
  lives in `01-literal.toml`, `02-literal-alternate.toml`, `04-ruff-noqa.toml`,
  `08-words.toml`, and `09-aws-keys.toml`.

### 4. Run a measurement

From the repo root:

```powershell
.external/rebar/target/release/rebar measure -t -e '^inductorparser$'
```

That runs rebar in correctness-check mode. Every supported case should print
`OK`. For a timed comparison against .NET's regex engines:

```powershell
.external/rebar/target/release/rebar measure -e '^(inductorparser|dotnet/(compiled|nobacktrack))$' --max-time 1s --max-warmup-time 500ms
```

Pipe to a file for a CSV (rebar prints the CSV on stdout).

## Local comparison baseline

Measured on 2026-04-24 with rebar `0.1.0 (rev 8e952148cc)` on Windows
`win-arm64`, .NET SDK `10.0.202`, .NET host `10.0.6`. Lower medians are better.
The run used `--max-time 1s --max-warmup-time 500ms`. These numbers include
the parser core's automatic scanner-shape skip for
`ZeroOrMore(FirstOf(match, AnyToken.Delete))`.

A broader run against every engine that built successfully in this workspace is
saved under `results/`: raw CSV in `all-runnable-2026-04-24.csv`, with a compact
Markdown matrix in `all-runnable-summary-2026-04-24.md`.
A later focused rerun for the ASCII case-insensitive literal prefilter is saved
as `literal-prefilter-summary-2026-04-24.md`. A focused rerun for the word-run
optimization is saved as `rune-run-summary-2026-04-24.md`.

Correctness check:

```powershell
rebar measure -t -e '^inductorparser$' -f '^(curated/01-literal/(sherlock-en|sherlock-casei-en|sherlock-ru|sherlock-zh)|curated/02-literal-alternate/(sherlock-en|sherlock-casei-en|sherlock-ru|sherlock-zh)|curated/04-ruff-noqa/(real|tweaked|compile-real)|curated/08-words/(all-english|long-english)|curated/09-aws-keys/(quick|compile-quick))$'
```

Result: all 15 supported `inductorparser` cases returned `OK`.

Timed comparison command:

```powershell
rebar measure -e '^(inductorparser|dotnet/compiled|dotnet/nobacktrack)$' -f '^(curated/01-literal/(sherlock-en|sherlock-casei-en|sherlock-ru|sherlock-zh)|curated/02-literal-alternate/(sherlock-en|sherlock-casei-en|sherlock-ru|sherlock-zh)|curated/04-ruff-noqa/(real|tweaked|compile-real)|curated/08-words/(all-english|long-english)|curated/09-aws-keys/(quick|compile-quick))$' --max-time 1s --max-warmup-time 500ms
```

The ratio is `engine median / .NET compiled median` within the same benchmark.
`.NET compiled` is therefore always `1.00x`; lower is faster, higher is slower.

| rebar name | model | engine | median | ratio vs .NET compiled |
|---|---:|---|---:|---:|
| `curated/01-literal/sherlock-en` | `count` | .NET compiled | 65.60us | 1.00x |
| `curated/01-literal/sherlock-en` | `count` | .NET NonBacktracking | 125.50us | 1.91x |
| `curated/01-literal/sherlock-en` | `count` | InductorParser | 398.90us | 6.08x |
| `curated/01-literal/sherlock-casei-en` | `count` | .NET compiled | 82.50us | 1.00x |
| `curated/01-literal/sherlock-casei-en` | `count` | .NET NonBacktracking | 147.50us | 1.79x |
| `curated/01-literal/sherlock-casei-en` | `count` | InductorParser | 3.31ms | 40.12x |
| `curated/01-literal/sherlock-ru` | `count` | .NET compiled | 87.20us | 1.00x |
| `curated/01-literal/sherlock-ru` | `count` | .NET NonBacktracking | 135.00us | 1.55x |
| `curated/01-literal/sherlock-ru` | `count` | InductorParser | 218.60us | 2.51x |
| `curated/01-literal/sherlock-zh` | `count` | .NET compiled | 30.20us | 1.00x |
| `curated/01-literal/sherlock-zh` | `count` | .NET NonBacktracking | 32.40us | 1.07x |
| `curated/01-literal/sherlock-zh` | `count` | InductorParser | 23.10us | 0.76x |
| `curated/02-literal-alternate/sherlock-en` | `count` | .NET compiled | 314.40us | 1.00x |
| `curated/02-literal-alternate/sherlock-en` | `count` | .NET NonBacktracking | 415.80us | 1.32x |
| `curated/02-literal-alternate/sherlock-en` | `count` | InductorParser | 2.00ms | 6.36x |
| `curated/02-literal-alternate/sherlock-casei-en` | `count` | .NET compiled | 158.50us | 1.00x |
| `curated/02-literal-alternate/sherlock-casei-en` | `count` | .NET NonBacktracking | 246.90us | 1.56x |
| `curated/02-literal-alternate/sherlock-casei-en` | `count` | InductorParser | 15.26ms | 96.28x |
| `curated/02-literal-alternate/sherlock-ru` | `count` | .NET compiled | 2.31ms | 1.00x |
| `curated/02-literal-alternate/sherlock-ru` | `count` | .NET NonBacktracking | 3.82ms | 1.65x |
| `curated/02-literal-alternate/sherlock-ru` | `count` | InductorParser | 7.04ms | 3.05x |
| `curated/02-literal-alternate/sherlock-zh` | `count` | .NET compiled | 52.40us | 1.00x |
| `curated/02-literal-alternate/sherlock-zh` | `count` | .NET NonBacktracking | 64.10us | 1.22x |
| `curated/02-literal-alternate/sherlock-zh` | `count` | InductorParser | 119.80us | 2.29x |
| `curated/04-ruff-noqa/real` | `grep-captures` | .NET compiled | 54.11ms | 1.00x |
| `curated/04-ruff-noqa/real` | `grep-captures` | .NET NonBacktracking | 65.90ms | 1.22x |
| `curated/04-ruff-noqa/real` | `grep-captures` | InductorParser | 3.38s | 62.47x |
| `curated/04-ruff-noqa/tweaked` | `grep-captures` | .NET compiled | 40.95ms | 1.00x |
| `curated/04-ruff-noqa/tweaked` | `grep-captures` | .NET NonBacktracking | 49.30ms | 1.20x |
| `curated/04-ruff-noqa/tweaked` | `grep-captures` | InductorParser | 271.34ms | 6.63x |
| `curated/04-ruff-noqa/compile-real` | `compile` | .NET compiled | 81.80us | 1.00x |
| `curated/04-ruff-noqa/compile-real` | `compile` | .NET NonBacktracking | 376.70us | 4.61x |
| `curated/04-ruff-noqa/compile-real` | `compile` | InductorParser | 7.20us | 0.09x |
| `curated/08-words/all-english` | `count-spans` | .NET compiled | 640.60us | 1.00x |
| `curated/08-words/all-english` | `count-spans` | .NET NonBacktracking | 1.30ms | 2.03x |
| `curated/08-words/all-english` | `count-spans` | InductorParser | 17.07ms | 26.65x |
| `curated/08-words/long-english` | `count-spans` | .NET compiled | 0.96ms | 1.00x |
| `curated/08-words/long-english` | `count-spans` | .NET NonBacktracking | 526.20us | 0.55x |
| `curated/08-words/long-english` | `count-spans` | InductorParser | 13.34ms | 13.90x |
| `curated/09-aws-keys/quick` | `grep` | .NET compiled | 29.35ms | 1.00x |
| `curated/09-aws-keys/quick` | `grep` | .NET NonBacktracking | 35.47ms | 1.21x |
| `curated/09-aws-keys/quick` | `grep` | InductorParser | 241.11ms | 8.21x |
| `curated/09-aws-keys/compile-quick` | `compile` | .NET compiled | 85.80us | 1.00x |
| `curated/09-aws-keys/compile-quick` | `compile` | .NET NonBacktracking | 257.90us | 3.01x |
| `curated/09-aws-keys/compile-quick` | `compile` | InductorParser | 2.80us | 0.03x |

The `compile` rows are not measuring a regex parser for InductorParser. They
measure construction of the hand-translated grammar plus `Rule.Compile()`.

The search rows show the current tradeoff: the runner is correct and the parser
core now bulk-skips impossible starts for the common `match | AnyToken.Delete`
scanner shape. It is still a generic grammar confirmation path rather than a
full regex optimizer, so cases with very common candidate starts or complex
line-capture work remain slower than .NET's specialized regex prefilters.
