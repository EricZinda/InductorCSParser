# Rebar literal-prefilter focused comparison - 2026-04-24

Measured after the automatic literal scanner prefilter change with `rebar 0.1.0 (rev 8e952148cc)` on the two ASCII case-insensitive Sherlock rows.

| rebar name | model | .NET compiled | InductorParser before | InductorParser after | after/.NET compiled |
|---|---:|---:|---:|---:|---:|
| `curated/01-literal/sherlock-casei-en` | `count` | `83.20us` | `3.43ms` | `273.80us` | `3.29x` |
| `curated/02-literal-alternate/sherlock-casei-en` | `count` | `159.40us` | `14.90ms` | `4.70ms` | `29.49x` |

The single-literal row now uses a full-literal substring prefilter. Literal alternates use the safer first-rune prefilter because repeated substring searches across every alternate were slower on this haystack.
