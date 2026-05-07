# Untitled

- Add LoweredOpCode.LoadPeekedToken (replaces LoadPeekedRune): peeks the next cluster's offset+length+first rune, stashes them on Machine. Falls back to a single rune-value scratch slot for the ASCII jump-table fast path so LoadPeekedTokenAndJumpAlt keeps its inline ASCII decode.
