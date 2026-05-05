# Potential bug sources

Cumulative log of bug-hunting sweeps through the parser source. The point
is to keep future hunts from re-walking ground a previous one already
covered. When a sweep finds something, the bug-and-fix detail goes into
its own backlog item; this doc just records "we already looked here, on
this date, for this kind of issue."

## Patterns to watch for

The following recurring shapes have caused real bugs. New code in the
named areas should be reviewed for these specifically; future hunts
should prefer them when picking where to dig next.

- **Local violations of the grapheme invariant.** The parser-wide
  invariant is that one token equals one user-perceived character (one
  UAX #29 grapheme cluster), and rules compare tokens as units. Any
  rule that drops to the rune level (peeks a single rune, compares
  against a fixed rune, advances by rune length) is locally answering
  a different question than the rest of the parser. ScanUntilRule had
  this shape and it produced two divergences from the rest of the
  parser at once: a stopper of `'"'` matching a `'"<combining-mark>'`
  cluster (where `OneOf("\"")` would refuse it) and a single-rune
  escape-start matching when the start rune was glued to extending
  characters (where `Token('\\')` would refuse it). See backlog 7scu.
  When reviewing a rule, ask: does this rule ever look at a partial
  cluster? If yes, it's potentially out of step with `OneOf`,
  `Token`, etc. on the same input.
- **Recursive vs. state-machine engine divergence.** The recursive
  evaluator and the state-machine evaluator should produce the same
  ParseResult on the same input. A divergence is almost always a bug
  in one of them. Cross-check rule implementations in
  `src/InductorParser/*Rule.cs` against their lowered counterparts in
  `ExperimentalSrc/InductorParser/StateMachine/Stepper.cs` and
  `Lowerer.cs` when looking for bugs. The cross-engine compare
  fixtures in `ExperimentalSrc/InductorParser.Tests/StateMachine*CompareTests.cs`
  are the right place to add a regression test for any new divergence
  found.
- **Discarding `Lexer.TryPeekRune`'s bool return when the rune feeds
  a TokenSet factory.** `TryPeekRune` returns `false` and writes
  `runeValue = -1` for lone surrogates; the validating `TokenSet.Single(int)`
  and friends reject `-1` with `ArgumentOutOfRangeException`. A rule that
  ignores the bool and pipes the out parameter straight into a TokenSet
  factory (LiteralRule and LiteralIgnoreAsciiCaseRule's ComputeRuleStart
  had this shape, see backlog pj8x) blows up from inside the factory
  with a "codepoint -1" error that doesn't explain the real cause.
  When reviewing a rule that calls TryPeekRune, ask: does the code use
  the bool return value? If not, and the runeValue feeds anything that
  validates scalar values, fall back to `TokenSet.Universe` /
  Advance.Always (the GraphemeRule pattern) so surrogate-prefixed text
  flows through under Compile(null) for WTF-8 round-tripping.

## Search log

Append-only. Newest entry on top. Don't rewrite past entries; the log
is a record of who-checked-what-when, not a current snapshot.

### 2026-05-04: LiteralRule + LiteralIgnoreAsciiCaseRule ComputeRuleStart sweep
Reviewed: `LiteralRule.cs`, `LiteralIgnoreAsciiCaseRule.cs`,
`GraphemeRule.cs` (for comparison), `Lexer.TryPeekRune`. Cross-checked
the FormC normalization-validation path in `Rule.Compile` and the
`UnexpectedUnicodeTests.cs` Token(string) round-tripping comments to
confirm a surrogate-prefixed literal under Compile(null) is a
documented use case and not a "don't do that." Found and fixed:
both `LiteralRule.ComputeRuleStart` and
`LiteralIgnoreAsciiCaseRule.ComputeRuleStart` discarded the bool
return from `Lexer.TryPeekRune` and passed the resulting
`runeValue = -1` straight to `TokenSet.Single`, which validates the
codepoint and threw `ArgumentOutOfRangeException` ("Actual value
was -1.") out of Compile when the literal's first char was a lone
surrogate. `GraphemeRule.ComputeRuleStart` already handled this
correctly by checking the return value and falling back to
`TokenSet.Universe`; the fix mirrors that. Backlog pj8x has the
full write-up. Three regression tests in `UnexpectedUnicodeTests`
lock in the surrogate-prefix Literal / LiteralIgnoreAsciiCase /
low-surrogate-first-char shapes.

### 2026-05-04: ScanUntilRule + Lexer + TokenSet sweep
Reviewed: `ScanUntilRule.cs`, `ScanWhileRule.cs`, `OneOfRule.cs`,
`NoneOfRule.cs`, `LiteralRule.cs`, `LiteralIgnoreAsciiCaseRule.cs`,
`GraphemeRule.cs`, `AnyTokenRule.cs`, `NotRule.cs`,
`BetweenInclusiveRule.cs`, `Rules.cs`, `TokenSet.cs`, `Lexing/Lexer.cs`,
`Lexing/Token.cs`, `Lexing/NormalizedPositionMap.cs`,
`WithinTokenRule.cs`, `Rule.cs` (partial). Cross-checked
`ExperimentalSrc/InductorParser/StateMachine/Stepper.cs` for the
ScanUntil opcode. Categories: rune-vs-token boundary mismatch,
escape-handling fast paths, multi-rune grapheme cluster handling, lone
surrogate handling, normalization-form interaction, recursive-vs-SM
divergence. Found and fixed: `ScanUntilRule`'s loop was rune-scoped
(stopper check via `_stopperSet.Contains(runeValue)`, escape-start
check via `runeValue == _escapeStartRune`, early-exit on
`!Lexer.TryPeekRune` for lone surrogates) while the rest of the
parser is grapheme-scoped, so its answers diverged from `OneOf` /
`Token` / `ZeroOrMore(NoneOf(...))` on inputs with multi-rune
clusters whose first rune was a stopper or escape, and on inputs
containing lone surrogates under `Compile(null)`. Backlog 7scu has
the full write-up. The fix moved both checks to
`_stopperSet.ContainsToken(...)` and `tokenLen == runeLen && runeValue
== _escapeStartRune` so the rule asks the same question as `OneOf`
on the same input, and dropped the lone-surrogate early-exit so
unpaired surrogates flow through as one-char body tokens (the leaf
Memory is a zero-copy slice, so they round-trip through `ToString()`
verbatim). Also added the CRLF cluster as a multi-rune entry to
`TokenSet.LineTerminators` and `TokenSet.Ascii.AnyWhitespace` (the
comment claiming "CRLF can't live in a rune set" was stale since the
multi-rune-TokenSet refactor) so line-oriented grammars don't lose
CRLF coverage under the new grapheme-scoped checks. One followup
deferred: parallel change in `Step_ScanUntilFast` (tracked in backlog
9sm2, blocked on the ExperimentalSrc/ RuneSet → TokenSet rename).
