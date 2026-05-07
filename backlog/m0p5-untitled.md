# Untitled

- SM polarity-aware dispatch and token-peek opcodes (Phase 6 follow-up to the token-peek refactor)

The recursive engine's lookahead shortcut now peeks the next TOKEN (one grapheme cluster) and dispatches on a polarity-tagged TokenSet (Polarity.MustBeIn or MustNotBeIn). NoneOf publishes its set as MustNotBeIn so the shortcut can precisely skip a NoneOf branch when the peek IS in the fail-set. The state-machine engine still dispatches via rune-peek opcodes (LoadPeekedRune, CheckPeekedRuneInSet, LoadPeekedRuneAndJumpAlt) and Lowerer.cs has two stopgaps:

1. CanSkipUnreachableAlt returns false for any child whose Polarity is MustNotBeIn, so NoneOf alternatives never get the SM's CheckPeekedRuneInSet pre-check. They fall through to the general PushBacktrack + try-the-rule path. Correct, just less optimal than what the recursive engine does.

2. The Lowerer flattens each child's FirstConsumedTokens via OneOfRule.LookaheadFirstRunes before passing it to CheckPeekedRuneInSet and the ASCII jump table. This re-introduces the OLD multi-rune-entry-first-rune inflation that the recursive engine's CannotMatchLookahead does inline (first-rune-or-cluster check). Without the flattening the SM's rune-only Contains(int) check would miss multi-rune-only sets like Runes("\r\n") and skip alts the rule actually accepts.

The proper fix is to make the SM token-aware:
