# Untitled

- Add LoweredOpCode.CheckPeekedTokenInSet (replaces CheckPeekedRuneInSet): tests Token.Chars against the set with the same first-rune-or-cluster semantics the recursive engine's CannotMatchLookahead uses, including the EOF and lone-surrogate edge cases.
