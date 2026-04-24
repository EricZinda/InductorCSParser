- Adopt rebar for regex-engine comparison benchmarks

Recommendation: use BurntSushi/rebar as the external harness for comparing
InductorParser against regex engines.

Why rebar fits:

- It is explicitly a regex-engine barometer, not a language microbenchmark.
- It already compares .NET Regex, .NET NonBacktracking, RE2, PCRE2, PCRE2 JIT,
  Hyperscan, Rust regex, Go regexp, Java, V8, Python, Perl, ICU, regress, and
  others.
- Its curated benchmark set includes real-world regexes and corpora: Ruff
  `# noqa`, Veryl lexer rules, Cloudflare ReDoS, UnicodeData parsing, word
  scanning over OpenSubtitles, AWS key detection, unstructured-log extraction,
  dictionary search, Nosey Parker secret patterns, and quadratic edge cases.
- Benchmark definitions live in TOML and are separate from engine runner code.
  That makes it feasible to add an InductorParser runner without porting every
  existing engine into this repository.
- The runner protocol is intentionally simple: rebar sends key-length-value
  input on stdin, runner programs time their own work, and they print
  duration/count samples to stdout. Counts are part of the harness contract, so
  a too-fast wrong parser is caught.

Recommended integration shape:

1. Keep rebar as an optional external benchmark harness, not as part of the
   main `InductorParser.sln`.
2. Add a small `net8.0` console runner under `src/Benchmarks/Rebar/` that reads
   rebar KLV input, dispatches by benchmark name/model, and emits rebar samples.
3. Start with hand-translated InductorParser grammars for a small curated subset
   whose regex features map cleanly to existing Rules.cs primitives:
   - `curated/01-literal/*`
   - `curated/02-literal-alternate/*`
   - `curated/03-date/*`
   - `curated/04-ruff-noqa/real`
   - `curated/04-ruff-noqa/tweaked`
   - ASCII `curated/08-words/*` cases
   - `curated/09-aws-keys/quick`
4. Support rebar models in this order:
   - `count`
   - `count-spans`
   - `grep`
   - `grep-captures`
   - `compile`, where the measured operation is grammar construction or
     future compiled-emitter construction
5. Exclude or mark unsupported at first:
   - backreferences
   - lookbehind
   - Unicode case-insensitive matching until full case folding lands
   - multi-pattern regex sets until there is a rule-level equivalent
   - arbitrary regex strings until the regex-to-InductorParser converter exists
6. Add an engine entry in a local rebar fork or overlay:
   - engine name: `inductorparser`
   - build: `dotnet build -c Release`
   - run: the published runner executable
   - version: commit hash plus assembly version
7. Run full cross-engine comparisons in Linux or WSL x64 for the broadest engine
   coverage. Local Windows runs can still compare InductorParser with the .NET
   regex engines and any portable runners installed on the machine.

Why not use the smaller `mariomka/regex-benchmark` suite:

- It is much easier to run, but it only measures a few fixed patterns such as
  email, URI, and IPv4 over a single synthetic-ish text corpus.
- It does not cover capture extraction, line-oriented grep models, compile-time
  cost, multi-pattern matching, Unicode-heavy cases, or production regexes that
  explain why engines perform differently.

Done when:

- The InductorParser rebar runner builds from the benchmark solution or its own
  optional solution.
- `rebar measure -t -e '^inductorparser$'` passes for the supported subset.
- `rebar measure` can produce a comparison CSV including InductorParser and at
  least .NET compiled/non-backtracking regex on this machine.
- The benchmark README documents which rebar cases are true apples-to-apples
  hand translations and which cases are intentionally unsupported.

Sources:

- https://github.com/BurntSushi/rebar
- https://github.com/BurntSushi/rebar/blob/master/METHODOLOGY.md
- https://github.com/BurntSushi/rebar/blob/master/KLV.md
- https://github.com/BurntSushi/rebar/blob/master/FORMAT.md
- https://github.com/BurntSushi/rebar/blob/master/BYOB.md
- https://github.com/mariomka/regex-benchmark
