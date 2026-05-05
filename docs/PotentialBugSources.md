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
- **Byte-level fast paths that bypass grapheme tokenization.**
  Optimization paths that use `string.IndexOfAny`, `string.IndexOf`,
  or other UTF-16-code-unit searches to fast-forward over input they
  don't care about (`Lexer.AdvanceUntilRuneIn`,
  `Lexer.AdvanceUntilLiteralCandidateIn`,
  `LiteralScannerCandidate.IndexIn`) can land at offsets that aren't
  grapheme-cluster boundaries the rest of the parser would visit. The
  practical case is the LF inside a CRLF cluster (UAX #29 GB3 keeps
  CR LF in one cluster, so the LF is at a non-token-boundary offset),
  but ZWJ inside emoji ZWJ sequences and combining marks attached to
  earlier bases have the same shape. When reviewing a fast-path that
  uses one of these searches, ask: can the candidate char ever appear
  as the second-or-later rune of a multi-rune cluster? If yes, the
  landing position has to be re-validated before the lexer's
  `_position` is set to it. See the 2026-05-04 entry below for the
  CRLF / combining-mark / ZWJ / VS cases and the
  `Lexer.IsAtMidGraphemeCluster` post-validation it added.

## Search log

Append-only. Newest entry on top. Don't rewrite past entries; the log
is a record of who-checked-what-when, not a current snapshot.

### 2026-05-04: Scanner-skip fast path (BetweenInclusiveRule + Lexer)
Reviewed: `BetweenInclusiveRule.cs` (the `TryCreateScannerSkip` /
`ScannerSkip.Advance` path), `Lexer.AdvanceUntilRuneIn`,
`Lexer.AdvanceUntilLiteralCandidateIn`, `Lexer.AdvanceWhileRuneIn`,
`Lexer.AdvanceWhileTokenIn`, `LiteralScannerCandidate.IndexIn` /
`MatchesAt` / `CanStartWith`, `TokenSet.TryGetBmpChars`, and the existing
scanner-shape tests in `BetweenInclusiveRuleTests.cs`. Cross-checked the
SM-side caller `ExperimentalSrc/InductorParser/StateMachine/Stepper.cs`
line 170 (it forwards to the same `AdvanceUntilRuneIn` so the
recursive-side fix carries over). Categories: byte-level-search vs
grapheme-token boundary mismatches, comments claiming ASCII-tokens-of-
themselves properties, optimization equivalence with the slow path,
multi-rune cluster handling at the IndexOfAny landing, recursive-vs-SM
divergence through a shared lexer helper. Found and fixed: the
`bmpCandidates` fast path in `AdvanceUntilRuneIn` was landing on the
LF inside a CRLF cluster when `\n` was in the candidate set, after
which the inner `FirstOf(OneOf("\n"), AnyToken.Delete)` read a fresh
one-rune `\n` token from a non-token-boundary offset and `OneOf`
mistakenly matched it. The slow path walks one grapheme at a time and
correctly rejects the CRLF cluster as a multi-rune token. The fix adds an `IsAtMidGraphemeCluster`
post-validation that walks one cluster forward from the codepoint
before the IndexOf landing (via `StringInfo.GetNextTextElement`) and
returns true when that cluster spans the landing offset. In
`AdvanceUntilRuneIn` the IndexOfAny is now in a loop that re-enters
when it lands mid-cluster; in both `AdvanceUntilLiteralCandidateIn`
paths (single-literal cached path, BMP-firstrunes IndexOfAny path) the
`AnyLiteralMatchesAt` confirmation is gated on the same check. The
generalized check covers CRLF, base+combining-mark (e.g. `é`),
emoji ZWJ sequences (e.g. man+ZWJ+woman), and base+variation-selector
(e.g. `#️`) within a two-codepoint span. Regression tests for
each cluster shape, plus a "still finds LF after CRLF" path and the
two `AdvanceUntilLiteralCandidateIn` paths, landed in
`BetweenInclusiveRuleTests.cs` next to the other scanner-shape tests.

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
