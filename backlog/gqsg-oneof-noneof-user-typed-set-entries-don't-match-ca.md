- OneOf / NoneOf: user-typed set entries don't match canonically-equivalent input + lone surrogates in rune intervals don't match either

Two related membership-test bugs in `OneOfRule` and `NoneOfRule`.

## Bug 1: canonically-equivalent input doesn't match user-typed entries

When a grammar contains `OneOf("é")` (user-typed precomposed) or `OneOf("é")` (user-typed decomposed), the rule fails to match input in the OTHER canonical form because the lexer and the set use different representations:
