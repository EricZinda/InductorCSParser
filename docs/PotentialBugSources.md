- Local violations of the grapheme invariant
    - The parser-wide invariant is that one token equals one user-perceived character (one UAX #29 grapheme cluster), and rules compare tokens as units. Any rule that drops to the rune level (peeks a single rune, compares against a fixed rune, advances by rune length) is locally answering a different question than the rest of the parser.
    - ScanUntilRule had this shape and it produced two divergences from the rest of the parser at once: a stopper of `'"'` matching a `'"<combining-mark>'` cluster (where `OneOf("\"")` would refuse it) and a single-rune escape-start matching when the start rune was glued to extending characters (where `Token('\\')` would refuse it). See backlog 7scu.
    - When reviewing a rule, ask: does this rule ever look at a partial cluster? If yes, it's potentially out of step with `OneOf`, `Token`, etc. on the same input.

- Char-unit rendering in user-facing strings
    - Anywhere the parser shows the user a "character" of input (the `{character}` placeholder in default error messages, trace lines that quote the current token, debug renderers), the unit shown should match what the parser reads as one token: a UAX #29 grapheme cluster.
    - Indexing the input with `input[pos]` returns one UTF-16 code unit, which is a lone surrogate half for any supplementary-plane rune (every emoji past the BMP, math alphanumerics like `𝐀`) and only the first rune of a multi-rune cluster under `Compile(null)` (`é` decomposed, CRLF, ZWJ emoji sequences). The rendered message lies about what the parser actually saw.
    - `BuildErrorMessage` had this shape; see backlog c9p3.
    - The fix is to render via `StringInfo.GetNextTextElement(input, pos)` (or any other path that returns the full token), matching the lexer.

- Recursive vs. state-machine engine divergence
    - The recursive evaluator and the state-machine evaluator should produce the same ParseResult on the same input. A divergence is almost always a bug in one of them.
    - Cross-check rule implementations in `src/InductorParser/*Rule.cs` against their lowered counterparts in `ExperimentalSrc/InductorParser/StateMachine/Stepper.cs` and `Lowerer.cs` when looking for bugs.
    - The cross-engine compare fixtures in `ExperimentalSrc/InductorParser.Tests/StateMachine*CompareTests.cs` are the right place to add a regression test for any new divergence found.

- Byte-level fast paths that bypass grapheme tokenization
    - Optimization paths that use `string.IndexOfAny`, `string.IndexOf`, or other UTF-16-code-unit searches to fast-forward over input they don't care about (`Lexer.AdvanceUntilRuneIn`, `Lexer.AdvanceUntilLiteralCandidateIn`, `LiteralScannerCandidate.IndexIn`) can land at offsets that aren't grapheme-cluster boundaries the rest of the parser would visit.
    - The practical case is the LF inside a CRLF cluster (UAX #29 GB3 keeps CR LF in one cluster, so the LF is at a non-token-boundary offset), but ZWJ inside emoji ZWJ sequences and combining marks attached to earlier bases have the same shape.
    - When reviewing a fast-path that uses one of these searches, ask: can the candidate char ever appear as the second-or-later rune of a multi-rune cluster? If yes, the landing position has to be re-validated before the lexer's `_position` is set to it.
    - See the 2026-05-04 "Scanner-skip fast path" entry in `BugSearchLog.md` for the CRLF / combining-mark / ZWJ / VS cases and the `Lexer.IsAtMidGraphemeCluster` post-validation it added.

- Static analysis on a TokenSet that ignores its multi-rune entries
    - Rules that declare what they consume (`ComputeRuleStart` for the lookahead skip, `ComputeRequiredLiteral` / `ComputeConcatenableText` for the substring prefilter, similar) often inspect a TokenSet by walking only its rune-only part (`_set.TryGetBmpChars`, `_set.RunesOnlyPart`). On a mixed set (rune-only entries plus multi-rune entries like the CRLF cluster, regional-indicator pairs, etc.), the multi-rune side is silently invisible to that walk.
    - The lookahead-skip side already handles this via `OneOfRule.LookaheadFirstRunes`, which folds each multi-rune entry's first rune into the rune-only set so the skip stays sound. Static analyses that produce a literal-prefilter answer don't have that machinery yet.
    - `OneOfRulePrefilter.ComputeConcatenableText` (in `ExperimentalSrc/InductorParser.Prefilter/OneOfRule.cs`, originally an override on `OneOfRule` in core before the prefilter was moved out) had this shape: a set like `Single('a') | Runes("\r\n")` returned `("a", false)` as a required substring even though the rule also matched the CRLF cluster, whose consumed text contains no "a". Breaks the `TryGetRequiredLiteral` "every match contains this literal" contract, so a grep prefilter silently skips matchable input. See backlog n4q7.
    - When reviewing a `Compute*` analyzer that walks a TokenSet, ask: does the analysis hold for every entry the rule could match, including multi-rune ones? If the analysis only covers the rune-only part, gate the analyzer on `!set.HasMultiRuneGraphemes` and return the conservative answer (null / Universe) for mixed sets.

- Discarding `Lexer.TryPeekRune`'s bool return when the rune feeds a TokenSet factory
    - `TryPeekRune` returns `false` and writes `runeValue = -1` for lone surrogates; the validating `TokenSet.Single(int)` and friends reject `-1` with `ArgumentOutOfRangeException`.
    - A rule that ignores the bool and pipes the out parameter straight into a TokenSet factory (`LiteralRule` and `LiteralIgnoreAsciiCaseRule`'s `ComputeRuleStart` had this shape, see backlog pj8x) blows up from inside the factory with a "codepoint -1" error that doesn't explain the real cause.
    - When reviewing a rule that calls TryPeekRune, ask: does the code use the bool return value? If not, and the runeValue feeds anything that validates scalar values, fall back to `TokenSet.Universe` / Advance.Always (the GraphemeRule pattern) so surrogate-prefixed text flows through under Compile(null) for WTF-8 round-tripping.
