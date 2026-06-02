- A scan loop bounded by `while (!lexer.IsEof)` that performs a per-position rule check inside the body never runs that check at the EOF position itself
    - `ScanUntilRule.TryParseRule` scans the body one token at a time with `while (!lexer.IsEof)` and tests the stopper at the top of each iteration. When input runs out the loop condition goes false and the loop exits — but the EOF position is itself a position, and a rule can succeed there. So a check that lives only inside the loop body silently never sees the EOF position.
    - For a TokenSet membership test this is harmless: a TokenSet is matched against a real token and EOF produces none, so "the set never matches at EOF" is correct by construction. The bug only shows up when the per-position check is a *rule* invocation, because rules can be EOF-sensitive:
        - `Eof()` succeeds only at end of input.
        - `Not(AnyToken())` succeeds only at end of input (AnyToken fails at EOF, so Not succeeds there).
        - `Or(Literal("END"), Eof())` and similar — the natural way to write "stop here OR at end of input."
      A `ScanUntil(Rule)` whose stopper is one of these never gets the stopper tested at EOF, so the body that should have ended at EOF instead overruns into the strict-failure branch. `ScanUntil(Eof())` always fails even though it means the same thing as the library's own `ScanUntilEof()` helper, which succeeds.
    - The fix shape: after the loop, when it exited *because* input ran out (`lexer.IsEof` true, distinguishing it from a `break`), run the rule check once more at the EOF position. Gate it on the conditions that make it matter (rule-stopper, strict mode, not already matched) so the common paths are untouched:
        ```csharp
        if (!stopperMatched && !_eofIsTerminator && _stopperRule != null && lexer.IsEof)
        {
            using var peek = lexer.BeginTransaction();
            if (_stopperRule.TryParse(lexer, outputSymbols: null) != null)
                stopperMatched = true;
        }
        ```
    - Reviewer mental model: `while (!atEnd) { check(); advance(); }` tests `check()` at positions `0, 1, ..., end-1` but never at `end`. If `end` is a legitimate position for the thing being checked to be true — and end-of-input is a legitimate position for an EOF-sensitive rule — the loop has a blind spot exactly one position wide. Either test inside the loop AND once after it, or restructure as `do { check(); if (atEnd) break; advance(); } while (true)`.
    - Adjacent shapes worth keeping an eye on for the same pattern:
        - The state-machine engine lowers `ScanUntil` into its own opcode. The TokenSet fast path (`Step_ScanUntilFast`, backlog `t6t6`) is TokenSet-only so it can't carry an EOF-sensitive stopper. A `ScanUntil(Rule)` bridges back to the recursive rule, so it inherits this fix — but any future native SM loop opcode that re-implements the Rule-stopper scan has to replicate the post-loop EOF check.
        - `ScanWhileRule` / `Lexer.AdvanceWhile*` loop on `_position < _endPosition` and test TokenSet membership per token. They have no rule-valued per-position check, so they don't have this blind spot — but a future "scan while a rule matches" variant would.
        - Any user-defined custom `Rule` subclass that scans with `while (!lexer.IsEof)` and runs a sub-rule per position needs the same post-loop check if that sub-rule can be EOF-sensitive.
    - Distinct from the `0000-loop-composite-break-before-add-drops-preserve-zero-width-inner.md` pattern. That one is a loop body that drops a symbol the inner rule returned. This one is a loop bound that skips a position entirely, so the inner rule is never even invoked there. Both surface as a rule that "demonstrably should match" not mattering, but one is a missing `Add`, the other a missing iteration.
