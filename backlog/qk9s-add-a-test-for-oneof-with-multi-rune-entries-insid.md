- Add a test for OneOf with multi-rune entries inside a Or, confirming the SM no longer needs the LookaheadFirstRunes pre-flatten and dispatches via the new CheckPeekedTokenInSet against the rule's full set.

The current code is correct, just slower than it needs to be in NoneOf-heavy or multi-rune-OneOf-heavy SM grammars. Rate of return depends on whether those shapes show up on the rebar / production benchmarks.
