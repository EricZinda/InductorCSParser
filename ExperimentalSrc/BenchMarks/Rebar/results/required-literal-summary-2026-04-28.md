# Auto-derived required-literal prefilter (2026-04-28)

Measured 2026-04-28 with rebar `0.1.0 (rev 8e952148cc)` on Windows
`win-arm64`, .NET host `10.0.6`. Lower medians are better.
The run used `--max-time 1s --max-warmup-time 500ms`.

This run replaces the hand-tuned `"# noqa"` trigger from the previous
`grep-prescan-2026-04-28` run with an automatic literal-extraction
analysis on the parser core's `Rule` API. Every serious regex engine
does this internally (rust/regex's `literal` module, .NET's compiled
regex's prefix extraction, PCRE2's "studied" patterns); now the
parser does it too via `Rule.TryGetRequiredLiteral`.

## What changed

`Rule.TryGetRequiredLiteral(out string literal, out bool ignoreAsciiCase)`
returns the longest literal substring that any successful match of the
rule must consume. It walks the rule tree:

- `Literal("foo")` → `("foo", false)`
- `LiteralIgnoreAsciiCase("foo")` → `("foo", true)`
- `Token("a")` → `("a", false)`
- `OneOf` with one BMP char → `(char, false)`
- `OneOf` with two ASCII letters that fold (e.g. `"Nn"`) →
  `("n", true)` (case-insensitive)
- `And(...)` concatenates consecutive concatenable children (so
  `And(Literal("# "), OneOf("Nn"), OneOf("Oo"), OneOf("Qq"), OneOf("Aa"))`
  yields `("# noqa", true)`) and recurses into non-concatenable
  children for standalone candidates, picking the longest seen.
- `BetweenInclusive[atLeast>=1]` propagates the inner's required
  literal; `Exactly(n, X)` repeats `X`'s concatenable text n times.
- Other rules (Or, ZeroOrMore, Optional, ScanUntil, Peek, Not)
  return null. Or could return a common-prefix literal across
  alternatives but isn't worth the complexity for the supported
  rebar subset; multi-literal alternation is the SearchValues<string>
  / Aho-Corasick story.

`BenchmarkPlan.CompileScanner` calls `match.TryGetRequiredLiteral`
after `Compile()` and uses the result to drive the same grep
pre-scan that previously had `"# noqa"` baked in. The hand-tuned
`NoqaTrigger` constant and `PatternGrammar.GrepTrigger` field are
deleted.

## Results

The auto-derivation produces identical numbers to the hand-tuned
trigger, within measurement noise:

| benchmark | hand-tuned (grep-prescan) | auto-derived | delta |
|---|---:|---:|---:|
| `04-ruff-noqa/real` (recursive) | 3.33ms | 3.32ms | within noise |
| `04-ruff-noqa/real` (state machine) | 3.50ms | 3.30ms | within noise |
| `04-ruff-noqa/tweaked` (recursive) | 3.26ms | 3.24ms | within noise |
| `04-ruff-noqa/tweaked` (state machine) | 3.26ms | 3.28ms | within noise |

For the literal-count rows (01-literal/*) the trigger is irrelevant
(those use `count` model, not `grep`), and numbers match the prior
baseline:

- `01-literal/sherlock-en` recursive 212.30us, state machine 129.10us
- `01-literal/sherlock-casei-en` recursive 269.40us, state machine 193.00us

For the AWS-keys row (`09-aws-keys/quick`), the `match` rule is
`And(Or(Literal("ASIA"), Literal("AKIA"), Literal("AROA"),
Literal("AIDA")), Exactly(16, OneOf(AwsKeyTail)))`. The auto-
derivation walks this:

- `Or(...)` → null (no common literal across alternatives)
- `Exactly(16, OneOf(AwsKeyTail))` → null (`OneOf` set is too broad
  to be a single concatenable char)

So no required literal is derived, and the runner falls back to
walking every line — same behavior as before, same medians (250ms
recursive, 222ms state machine). Closing this row needs a multi-
literal prefilter (`SearchValues<string>` from .NET 9 or hand-rolled
Aho-Corasick), which is a separate piece of work.

## Why this is now a parser feature

A user writing their own grammar gets the speedup without knowing
this analysis exists. They build a `Rule` tree the normal way; if
their grammar happens to require a fixed literal, the runner (or
any other caller) can ask for it via `TryGetRequiredLiteral` and
do its own pre-filtering. The `BenchmarkPlan` integration is just
one consumer; nothing in the runner is grammar-specific anymore.

This is the same shape as the parser's existing `FirstConsumedRunes`
and `Advance` analysis: a static rule-tree property the parser
exposes for callers and uses internally for shortcuts.

## Outstanding rows

| rebar name | best engine | ratio | path forward |
|---|---|---:|---|
| `02-literal-alternate/sherlock-casei-en` | state machine | 27.99x | needs a multi-substring prefilter |
| `09-aws-keys/quick` | state machine | 7.62x | needs a multi-substring prefilter |
| `08-words/all-english` | recursive | 10.88x | structural (tree construction cost) |
| `08-words/long-english` | recursive | 7.22x | structural |
