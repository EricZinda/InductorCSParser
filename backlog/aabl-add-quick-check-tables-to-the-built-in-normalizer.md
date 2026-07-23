# Add quick-check tables to the built-in normalizer

The built-in UAX #15 normalizer has no quick-check tables. It decomposes, reorders, and recomposes every non-ASCII input on every call, then hands back the original string instance when the result is content-identical. The runtime's ICU normalizer checks the NFC_QC property first and returns already-normalized input after a single scan, and real-world text is almost always already NFC, so the built-in implementation does the full rebuild precisely in the common case where there's nothing to rebuild.

UnicodeJsonBench (src/Benchmarks/Json/UnicodeJsonBench.cs) measures the cost on JSON whose string values are real Unicode text. It runs both implementations in one BenchmarkDotNet run (two jobs differing only in the INDUCTORPARSER_UNICODE_IMPLEMENTATION environment variable, Runtime as baseline). Re-run it with `Benchmarks.exe --filter "*UnicodeJsonBench*"`. Results from 2026-07-22, .NET 8.0.25 Arm64, ShortRun:

| Pool       | NormalizeOnly Runtime | NormalizeOnly Built-in | Ratio | Allocated (Runtime, Built-in) | Full-parse ratio |
| ---------- | --------------------- | ---------------------- | ----- | ----------------------------- | ---------------- |
| NfcLatin   | 5.1 us                | 445 us                 | 87x   | 0, 111 KB                     | 1.72             |
| Cjk        | 8.8 us                | 316 us                 | 36x   | 0, 55 KB                      | 1.50             |
| Hangul     | 8.8 us                | 387 us                 | 44x   | 0, 111 KB                     | 1.60             |
| Decomposed | 185 us                | 487 us                 | 2.6x  | 14 KB, 72 KB                  | 1.21             |
| Emoji      | 33 us                 | 542 us                 | 17x   | 4 B, 136 KB                   | 1.72             |

The ParseNoNormalize rows (same grammar compiled with Compile(null)) are identical across implementations on every pool, so the entire parse-level gap is the normalizer. On all-ASCII input both implementations return the input unchanged and there's no difference at all (measured separately with the plain JsonBench under each setting).

Who pays: the built-in implementation is the default only in the netstandard2.1 assembly that Unity's Mono and IL2CPP load, plus anyone who opts in for cross-runtime agreement. That audience is the one most sensitive to the allocation half of the problem, since 55 to 136 KB of garbage per parse is GC-hitch material in a game.

The optimization is the one the UnicodeNormalization remarks already anticipate: generate the NFC_QC / NFD_QC quick-check property tables (a third generated table family alongside decomposition and composition), scan the input first, and return it unchanged when every scalar is quick-check Yes and the combining classes are already in canonical order. The subtle part is the Maybe resolution, where a Maybe scalar forces a real normalization of the surrounding sequence. IsNormalized should use the same scan instead of normalizing and comparing.

Done when the built-in NormalizeOnly time on the already-NFC pools (NfcLatin, Cjk, Hangul, Emoji) drops to near the runtime's with zero allocation, the full-parse ratio on those pools is near 1.0, the Decomposed pool still normalizes correctly, and the UAX #15 conformance tests still pass.
