# Untitled

- Add LoweredOpCode.CheckPeekedTokenNotInSet: the negative-polarity counterpart. Skip the alt iff peek IS in the fail-set (strict cluster check). Lowerer picks this opcode when child.Polarity == MustNotBeIn.
