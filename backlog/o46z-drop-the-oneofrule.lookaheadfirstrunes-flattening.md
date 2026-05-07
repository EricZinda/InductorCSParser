- Drop the OneOfRule.LookaheadFirstRunes flattening in the Or-alt and BuildOrJumpTable paths; pass the rule's published FirstConsumedTokens directly to CheckPeekedTokenInSet, which now handles multi-rune entries.

The scanner-skip path's Lowerer.TryCreateScannerSkipSpec still needs LookaheadFirstRunes (it feeds Lexer.AdvanceUntilRuneIn, which is rune-only by design and won't move to token-peek). That call site stays.

## Files to touch
