# Untitled

- SM polarity-aware dispatch and token-peek opcodes (Phase 6 follow-up to the token-peek refactor)

The recursive engine's lookahead shortcut now peeks the next TOKEN (one grapheme cluster) and dispatches on a polarity-tagged TokenSet (Polarity.MustBeIn or MustNotBeIn). NoneOf publishes its set as MustNotBeIn so the shortcut can precisely skip a NoneOf branch when the peek IS in the fail-set. The state-machine engine still dispatches via rune-peek opcodes (LoadPeekedRune, CheckPeekedRuneInSet, LoadPeekedRuneAndJumpAlt) and Lowerer.cs has two stopgaps:

1. CanSkipUnreachableAlt returns false for any child whose Polarity is MustNotBeIn, so NoneOf alternatives never get the SM's CheckPeekedRuneInSet pre-check. They fall through to the general PushBacktrack + try-the-rule path. Correct, just less optimal than what the recursive engine does.

2. The Lowerer flattens each child's FirstConsumedTokens via OneOfRule.LookaheadFirstRunes before passing it to CheckPeekedRuneInSet and the ASCII jump table. This re-introduces the OLD multi-rune-entry-first-rune inflation that the recursive engine's CannotMatchLookahead does inline (first-rune-or-cluster check). Without the flattening the SM's rune-only Contains(int) check would miss multi-rune-only sets like Runes("\r\n") and skip alts the rule actually accepts.

The proper fix is to make the SM token-aware:

- Add LoweredOpCode.LoadPeekedToken (replaces LoadPeekedRune): peeks the next cluster's offset+length+first rune, stashes them on Machine. Falls back to a single rune-value scratch slot for the ASCII jump-table fast path so LoadPeekedTokenAndJumpAlt keeps its inline ASCII decode.
- Add LoweredOpCode.CheckPeekedTokenInSet (replaces CheckPeekedRuneInSet): tests Token.Chars against the set with the same first-rune-or-cluster semantics the recursive engine's CannotMatchLookahead uses, including the EOF and lone-surrogate edge cases.
- Add LoweredOpCode.CheckPeekedTokenNotInSet: the negative-polarity counterpart. Skip the alt iff peek IS in the fail-set (strict cluster check). Lowerer picks this opcode when child.Polarity == MustNotBeIn.
- Update LoadPeekedTokenAndJumpAlt to fold polarity into the per-codepoint table: for each ASCII codepoint, build the table by asking each child "would you accept this codepoint as a single-rune cluster?" using the polarity-aware predicate, emit the alt index that wins or failTarget if none does.

Once the new opcodes land, the two stopgaps in Lowerer.cs can come out:

- Drop the `child.Polarity == MustNotBeIn` short-circuit in CanSkipUnreachableAlt; let MustNotBeIn alts use the new CheckPeekedTokenNotInSet path.
- Drop the OneOfRule.LookaheadFirstRunes flattening in the Or-alt and BuildOrJumpTable paths; pass the rule's published FirstConsumedTokens directly to CheckPeekedTokenInSet, which now handles multi-rune entries.

The scanner-skip path's Lowerer.TryCreateScannerSkipSpec still needs LookaheadFirstRunes (it feeds Lexer.AdvanceUntilRuneIn, which is rune-only by design and won't move to token-peek). That call site stays.

## Files to touch

- ExperimentalSrc/InductorParser/StateMachine/LoweredOpCode.cs (new opcodes)
- ExperimentalSrc/InductorParser/StateMachine/Machine.cs (PeekedRune slot grows to PeekedToken: offset, length, runeValue)
- ExperimentalSrc/InductorParser/StateMachine/Stepper.cs (Step_LoadPeekedToken / Step_CheckPeekedTokenInSet / Step_CheckPeekedTokenNotInSet, polarity-aware Step_LoadPeekedTokenAndJumpAlt)
- ExperimentalSrc/InductorParser/StateMachine/Lowerer.cs (emit new opcodes; drop stopgaps; polarity-aware ASCII table builder)

## Verification

- All existing SM tests pass (96 in InductorParser.StateMachine.Tests + the recursive-engine suite under INDUCTOR_DEFAULT_ENGINE=statemachine).
- Add a test that runs `Or(NoneOf(TokenSet.Single('a')), Literal("a"))` against `"a"` under SM and confirms the SM precisely skips NoneOf via CheckPeekedTokenNotInSet (observable in trace output, or by checking the lowered program's opcode sequence).
- Add a test for OneOf with multi-rune entries inside a Or, confirming the SM no longer needs the LookaheadFirstRunes pre-flatten and dispatches via the new CheckPeekedTokenInSet against the rule's full set.

The current code is correct, just slower than it needs to be in NoneOf-heavy or multi-rune-OneOf-heavy SM grammars. Rate of return depends on whether those shapes show up on the rebar / production benchmarks.
