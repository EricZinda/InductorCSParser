# Untitled

- Update LoadPeekedTokenAndJumpAlt to fold polarity into the per-codepoint table: for each ASCII codepoint, build the table by asking each child "would you accept this codepoint as a single-rune cluster?" using the polarity-aware predicate, emit the alt index that wins or failTarget if none does.

Once the new opcodes land, the two stopgaps in Lowerer.cs can come out:
