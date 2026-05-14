- A loop-style composite that breaks out on a zero-width-match guard before adding the matched child to outputSymbols drops a Preserve'd zero-width Inner's wrapper Symbol
    - `BetweenInclusiveRule.TryParseRule` runs a loop of `Inner.TryParse` calls and writes each successful Inner's Symbol into the parent's children list. The loop body has to guard against a zero-width Inner (one that succeeds without advancing the lexer) because the loop would otherwise spin forever incrementing count on a rule like `ZeroOrMore(Optional(X))`. The natural place to put that guard is right after the success check, immediately on `lexer.Position == positionBefore`. But putting the break *before* the Add silently drops the matched child for any Inner whose `FlattenType == Preserve` and succeeds with zero width: the wrapper Symbol Inner returned sits in `nextSymbol`, the break runs, and `outputSymbols.Add(nextSymbol)` never executes. `Tree.Find(inner)` returns null for a rule that demonstrably succeeded.
    - Inner shapes that succeed with zero width AND return a real wrapper Symbol when `FlattenType.Preserve`:
        - `Not(X).Preserve()` when X fails. Documented in `NotRule.cs:60-67` — the success path returns a zero-width memory wrapper exactly so consumers can highlight "the parser asserted X is not here" at the right offset.
        - `Peek(X).Preserve()` when X succeeds. Same shape, documented in `PeekRule.cs:49-57`.
        - `Eof().Preserve()` at end of input. Same shape, documented in `EofRule.cs:38-45`.
        - Any composite whose every child is zero-width when run under PreserveAllSymbols (debug mode), or any Preserve'd composite that the grammar author built out of the above primitives.
    - The fix shape: reorder the loop so the Add runs on every successful child and only the break is gated on zero-width.
        ```csharp
        if (nextSymbol == null) break;
        bool zeroWidthMatch = lexer.Position == positionBefore;
        if (outputSymbols != null && !ReferenceEquals(nextSymbol, Symbol.Discarded))
            outputSymbols.Add(nextSymbol);
        if (zeroWidthMatch) break;
        count++;
        ```
        AndRule (`AndRule.cs:32-35`) is the reference: it adds non-Discarded children unconditionally on success and is the reason `And(..., Not(X).Preserve(), ...)` already places the wrapper in the tree. The fix brings BetweenInclusive in line with And.
    - Counting semantics deliberately stay the same. Moving `count++` before the break too would change `Exactly(1, zero-width-Inner)` from fail to succeed, and the long-standing "Exit with whatever count we have. The AtLeast check below decides if that's enough" comment makes the no-count-on-zero-width semantics explicit. The minimal fix honors Inner's contract (add the returned wrapper) without changing what counts as a match.
    - Reviewer mental model: any composite that runs an inner rule on every iteration and decides whether to continue based on lexer progress has to add the inner's wrapper before checking progress, not after. "Inner succeeded with zero width" and "Inner's wrapper belongs in my tree" are two facts; conflating them is the bug.
    - Adjacent shapes worth keeping an eye on for the same pattern:
        - A future user-defined custom `Rule` subclass that wraps an inner in a `while (...)` loop and exits on "no progress" needs the same ordering. The `Rule.TryParseRule` contract documents the `outputSymbols.Add` semantics but doesn't currently call out the "must add before any progress-based break" gotcha.
        - Any future engine path (state machine, alternative evaluator) that lowers BetweenInclusive into its own loop opcode needs the same ordering. Today the recursive engine is the only path; if the SM ever has a native loop opcode, the lowerer has to replicate the Add-then-break order.
        - The scanner-skip fast path in `BetweenInclusiveRule.TryCreateScannerSkip` already takes a different "Inner can't match the lookahead, skip the loop entirely" exit. That fast path doesn't have the bug (it never enters the body, so there's no wrapper to drop), but it's gated on `Inner is OrRule with an AnyTokenRule fallback` and is unrelated to the zero-width-Inner case.
    - Distinct from the `0000b-shallower-witherror-vs-deeper-orphan-deepest.md` pattern. That one is about a `RecordFailure` call landing at a shallower position than the deepest-wins comparison values. This one is about a successful `outputSymbols.Add` call that never fires. Both ends with the user's content invisible, but the cause and fix are different: that one is about the failure-recording API, this is about the success-recording loop order.
