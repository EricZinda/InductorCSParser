# Rebar all-runnable comparison - 2026-04-24

Measured with `rebar 0.1.0 (rev 8e952148cc)` on the 15-case InductorParser-supported subset. Values are median wall-clock timings from `all-runnable-2026-04-24.csv`.

Runnable engines in this workspace: `.NET compiled`, `.NET NonBacktracking`, `rust/regex`, `rust/regexold`, `rust/regex/lite`, `regress`, `go/regexp`, `javascript/v8`, and `InductorParser`. JavaScript doesn't appear for compile-only models. Engines requiring unavailable native/runtime dependencies weren't measured.

Note: a few rows report timer-floor medians such as `0.00ns` or `1.00ns` while their means are higher; keep the raw CSV mean/stddev columns in mind for those rows.

Later parser work improved the ASCII case-insensitive literal rows and the ASCII word rows; see `literal-prefilter-summary-2026-04-24.md` and `rune-run-summary-2026-04-24.md` for the focused reruns.

| rebar name | model | .NET compiled | .NET NB | rust/regex | rust/regexold | rust/regex lite | regress | go/regexp | JS/V8 | InductorParser | IP/.NET compiled |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `curated/01-literal/sherlock-casei-en` | `count` | `86.00us` | `147.30us` | `122.70us` | `707.65us` | `18.08ms` | `829.50us` | `30.97ms` | `489.20us` | `3.43ms` | `39.88x` |
| `curated/01-literal/sherlock-en` | `count` | `65.90us` | `125.40us` | `40.40us` | `40.10us` | `12.76ms` | `316.80us` | `1.00ns` | `185.30us` | `413.70us` | `6.28x` |
| `curated/01-literal/sherlock-ru` | `count` | `87.00us` | `132.90us` | `68.40us` | `67.90us` | `15.48ms` | `545.80us` | `1.00ms` | `55.40us` | `217.00us` | `2.49x` |
| `curated/01-literal/sherlock-zh` | `count` | `30.30us` | `32.30us` | `30.90us` | `30.80us` | `5.94ms` | `281.10us` | `1.00ns` | `128.90us` | `23.40us` | `0.77x` |
| `curated/02-literal-alternate/sherlock-casei-en` | `count` | `157.90us` | `245.90us` | `1.05ms` | `1.45ms` | `45.08ms` | `4.16ms` | `90.31ms` | `1.53ms` | `14.90ms` | `94.36x` |
| `curated/02-literal-alternate/sherlock-en` | `count` | `312.60us` | `400.60us` | `115.80us` | `1.38ms` | `35.31ms` | `689.50us` | `55.32ms` | `1.53ms` | `1.99ms` | `6.37x` |
| `curated/02-literal-alternate/sherlock-ru` | `count` | `2.31ms` | `3.80ms` | `321.20us` | `2.37ms` | `34.50ms` | `10.06ms` | `72.63ms` | `1.96ms` | `7.04ms` | `3.05x` |
| `curated/02-literal-alternate/sherlock-zh` | `count` | `52.30us` | `62.90us` | `93.50us` | `1.24ms` | `13.30ms` | `4.33ms` | `28.65ms` | `159.50us` | `121.10us` | `2.32x` |
| `curated/04-ruff-noqa/compile-real` | `compile` | `81.90us` | `402.70us` | `59.50us` | `0.00ns` | `3.80us` | `5.80us` | `1.00ns` |  | `7.20us` | `0.09x` |
| `curated/04-ruff-noqa/real` | `grep-captures` | `54.08ms` | `65.11ms` | `24.53ms` | `191.70ms` | `1.23s` | `1.31s` | `1.87s` | `417.01ms` | `3.27s` | `60.47x` |
| `curated/04-ruff-noqa/tweaked` | `grep-captures` | `40.73ms` | `48.45ms` | `25.41ms` | `29.41ms` | `484.49ms` | `96.26ms` | `104.69ms` | `110.47ms` | `249.77ms` | `6.13x` |
| `curated/08-words/all-english` | `count-spans` | `647.80us` | `1.33ms` | `733.00us` | `882.50us` | `2.62ms` | `599.40us` | `5.61ms` | `613.20us` | `16.81ms` | `25.95x` |
| `curated/08-words/long-english` | `count-spans` | `0.96ms` | `557.20us` | `119.40us` | `125.50us` | `1.99ms` | `592.20us` | `931.70us` | `528.20us` | `13.22ms` | `13.77x` |
| `curated/09-aws-keys/compile-quick` | `compile` | `81.50us` | `228.90us` | `19.60us` | `23.90us` | `2.30us` | `3.50us` | `1.00ns` |  | `2.80us` | `0.03x` |
| `curated/09-aws-keys/quick` | `grep` | `29.34ms` | `35.42ms` | `21.34ms` | `25.82ms` | `1.16s` | `71.87ms` | `66.93ms` | `91.14ms` | `240.40ms` | `8.19x` |
