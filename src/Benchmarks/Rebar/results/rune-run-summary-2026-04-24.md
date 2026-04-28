# Rebar RuneRun focused comparison - 2026-04-24

Measured after adding `RuneRun(set, minimumCount)` and using it for the ASCII word benchmarks. Raw CSV is in `rune-run-2026-04-24.csv`.

The previous InductorParser medians are from `all-runnable-summary-2026-04-24.md`. The new run used the same `rebar 0.1.0 (rev 8e952148cc)` checkout, `--max-time 1s`, and `--max-warmup-time 500ms`.

| rebar name | engine | median | ratio vs .NET compiled |
|---|---|---:|---:|
| `curated/08-words/all-english` | regress | `547.70us` | `0.85x` |
| `curated/08-words/all-english` | JS/V8 | `613.30us` | `0.96x` |
| `curated/08-words/all-english` | .NET compiled | `640.80us` | `1.00x` |
| `curated/08-words/all-english` | rust/regexold | `876.60us` | `1.37x` |
| `curated/08-words/all-english` | rust/regex | `1.01ms` | `1.58x` |
| `curated/08-words/all-english` | .NET NonBacktracking | `1.32ms` | `2.06x` |
| `curated/08-words/all-english` | rust/regex lite | `2.62ms` | `4.09x` |
| `curated/08-words/all-english` | go/regexp | `5.55ms` | `8.66x` |
| `curated/08-words/all-english` | InductorParser | `7.02ms` | `10.96x` |
| `curated/08-words/long-english` | rust/regex | `119.40us` | `0.12x` |
| `curated/08-words/long-english` | rust/regexold | `125.60us` | `0.13x` |
| `curated/08-words/long-english` | .NET NonBacktracking | `520.80us` | `0.53x` |
| `curated/08-words/long-english` | JS/V8 | `528.10us` | `0.54x` |
| `curated/08-words/long-english` | regress | `592.80us` | `0.60x` |
| `curated/08-words/long-english` | .NET compiled | `0.98ms` | `1.00x` |
| `curated/08-words/long-english` | rust/regex lite | `1.99ms` | `2.03x` |
| `curated/08-words/long-english` | go/regexp | `2.01ms` | `2.05x` |
| `curated/08-words/long-english` | InductorParser | `6.91ms` | `7.05x` |

| rebar name | .NET compiled | InductorParser before | InductorParser after | after/.NET compiled | IP speedup |
|---|---:|---:|---:|---:|---:|
| `curated/08-words/all-english` | `640.80us` | `16.81ms` | `7.02ms` | `10.96x` | `2.39x` |
| `curated/08-words/long-english` | `0.98ms` | `13.22ms` | `6.91ms` | `7.05x` | `1.91x` |

`RuneRun` collapses the old `AtLeast/OneOrMore(OneOf(...))` tree from one leaf per word rune into one leaf per word. `Symbol.GetUtf8ByteCount()` also lets the runner sum `count-spans` results without materializing each match with `ToString()`.
