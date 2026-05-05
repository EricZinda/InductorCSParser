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
- **Char-unit rendering in user-facing strings.** Anywhere the parser
  shows the user a "character" of input (the `{character}` placeholder
  in default error messages, trace lines that quote the current token,
  debug renderers), the unit shown should match what the parser reads
  as one token: a UAX #29 grapheme cluster. Indexing the input with
  `input[pos]` returns one UTF-16 code unit, which is a lone surrogate
  half for any supplementary-plane rune (every emoji past the BMP, math
  alphanumerics like `𝐀`) and only the first rune of a multi-rune
  cluster under `Compile(null)` (`é` decomposed, CRLF, ZWJ emoji
  sequences). The rendered message lies about what the parser actually
  saw. `BuildErrorMessage` had this shape; see backlog c9p3. The fix
  is to render via `StringInfo.GetNextTextElement(input, pos)` (or any
  other path that returns the full token), matching the lexer.
- **Reusing the failure-path Math.Max idiom on a SUCCESS-then-trailing-input path.** `Rule.ParseRecursive` has three "report failure" branches: a parse-failed branch, a budget-aborted branch, and a trailing-input-after-success branch. The first two correctly use `Math.Max(lexer.DeepestFailure, lexer.Position)` because rollback put `lexer.Position` at 0 and `DeepestFailure` is the only meaningful "how far did we get" hint. The third doesn't have that property: the parse SUCCEEDED, `lexer.Position` is the position of the first unconsumed char, and `DeepestFailure` is from a sibling alternative the parser tried and discarded via rollback. Copy-pasting the Math.Max idiom into the trailing-input branch surfaces the rolled-back position and (worse) the rolled-back rule's `WithError` message, both contradicting the documented contract that `input[ErrorCharIndex]` is the character that didn't match. See backlog h4tn. When reviewing a position-reporting branch, ask: which of `lexer.Position` and `DeepestFailure` is the meaningful one in this control-flow state? They aren't interchangeable.
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

### 2026-05-05: error-position handling sweep (trailing-input branch)
Reviewed: `Rule.ParseRecursive`'s three failure-reporting branches
(parse-failed / budget-aborted / trailing-input-after-success) and
their interaction with `lexer.DeepestFailure` /
`lexer.DeepestFailureMessage` / `lexer.Position`,
`Lexing/NormalizedPositionMap.cs` (lockstep + per-grapheme walkers
under FormC/FormD/FormKC/FormKD and the ReferenceEquals fast path),
`SyntaxTree/SourcePositionConverter.cs` (token-index, line/column,
CRLF and BMP-vs-supplementary handling), `Lexing/Lexer.cs`'s
`RecordFailure` (the deepest-wins + equal-depth-claim logic).
Categories: position-vs-message-mismatch in the failure-rendering
branches, rolled-back-alternative leakage into trailing-input
output, normalization-form position translation edge cases, line/
column math on `\r\n` boundaries, defensive Math.Max idioms
applied where their rationale doesn't hold. Found and fixed:
the trailing-input branch of `ParseRecursive` reported
`Math.Max(lexer.DeepestFailure, lexer.Position)` instead of just
`lexer.Position`, so when a sibling alternative explored deeper
than where the rule stopped consuming and rolled back, the
trailing-input error pointed at a character the parser had already
abandoned. Same branch passed `lexer.DeepestFailureMessage` as the
custom error message, so a `WithError` on a rolled-back rule
appeared as the trailing-input message even though that rule
wasn't on the success path. Both contradicted the documented
"first leftover character" / "input[ErrorCharIndex] is the
character that didn't match" contract in
`docs/InductorParserDesignDecisions.md`. The fix drops the Math.Max
and passes `customMessage: null` in the trailing-input branch
only; the parse-failed and budget-aborted branches still use the
Math.Max idiom because their rationale (`lexer.Position` is 0 after
rollback) still applies. Two regression tests in
`AllowTrailingInputTests.cs` lock in the position fix and the
message-leakage fix. Backlog h4tn had the write-up.

### 2026-05-04: error-message rendering + position math sweep
Reviewed: `Rule.cs` (BuildErrorMessage / FormatTemplate / PositionPlaceholders),
`ParseOptions.cs`, `ParseResult.cs`, `SyntaxTree/SourcePosition.cs`,
`SyntaxTree/SourcePositionConverter.cs`, `SyntaxTree/Symbol.cs`,
`SyntaxTree/SourceRange.cs`, `SyntaxTree/SymbolExtensions.cs`,
`SyntaxTree/SymbolId.cs`, `SyntaxTree/SymbolRanges.cs`, `EofRule.cs`,
`PeekRule.cs`, `NotRule.cs`, `AllOfRule.cs`, `FirstOfRule.cs`,
`AnyTokenRule.cs`, `WithinTokenRule.cs`. Categories: char-vs-rune-vs-
grapheme rendering in user-facing strings, EOF / end-position edge
cases in line/column math, surrogate-pair handling in placeholder
substitution, multi-rune grapheme cluster handling under
`Compile(null)`, partial-output cleanup on inner-rule failure.
Found and fixed: `BuildErrorMessage` rendered `{character}` via
`parseInput[posInParseInput].ToString()`, which is one UTF-16 char
even when the token the parser was looking at is several chars
(supplementary-plane rune like `𝐀`, multi-rune cluster like `é`
under `Compile(null)`). On a supplementary-plane fail the message
showed a lone surrogate (rendered as `'�'`); on a decomposed-grapheme
fail it showed only the first rune of the cluster. Backlog c9p3 has
the write-up. The fix swaps the substitution to
`StringInfo.GetNextTextElement(parseInput, posInParseInput)` so the
substituted value is exactly the token the lexer would have read.
Added two regression tests in `ErrorMessageTemplateTests.cs`. ASCII
messages are unchanged (one char == one grapheme).

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
