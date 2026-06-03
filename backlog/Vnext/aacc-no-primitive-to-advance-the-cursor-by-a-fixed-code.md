# No primitive to advance the cursor by a fixed code-unit (or byte) count

A custom rule can only move the cursor forward through `lexer.Read()`, whose
smallest step is one grapheme cluster in normal mode (one rune in the
`WithinToken` sub-lexer). It never stops in the middle of a cluster or a rune.
Meanwhile `lexer.Position` is measured in UTF-16 code units (C# chars), a finer
unit than `Read` can land on. There's no public method to advance the cursor by
a fixed number of code units, runes, or bytes.

That gap makes a whole class of length-prefixed parsing either impossible or
stricter than it should be. It came up porting nom's `length_data` (see
`E2ESamples/Resp/`, a RESP / Redis-protocol parser): the bulk-string rule reads
a length and then has to consume exactly that many units. nom's `length_data`
splits the input freely at the count boundary, exactly N bytes on byte input
or exactly N code points on a string, cutting through a grapheme cluster without
complaint. The InductorParser port can't reproduce that. Because `Read` only
advances by whole graphemes, a code-unit-counted length that lands inside a
grapheme (a surrogate pair, a combining sequence, a flag emoji) names an offset
the rule can never reach, so the sample rejects it rather than splitting the
cluster. nom would have returned a result there.

So:

- A byte-exact `length_data` (nom on byte input) isn't reachable at all, since
  bytes are UTF-8 and positions are UTF-16.
- A code-point-exact `length_data` (nom on a string) is almost reachable via the
  rune-mode `WithinToken` sub-lexer, except the count is in code units, not
  runes, so a count can still land mid-surrogate-pair.
- Even a code-unit-exact `length_data` isn't reachable, because `Read` won't
  split a rune or a cluster to land on an arbitrary code-unit boundary.

Proposal: a lexer primitive (and/or a protected `Rule` helper) that advances the
cursor by a fixed count, something like:

```csharp
// advance exactly `codeUnits` UTF-16 code units, or fail if that lands
// past end-of-input
bool TryAdvance(int codeUnits);
```

with a rune-count variant if that reads cleaner for code-point-based formats.
That would let a length-prefixed rule consume an exact span (and let it choose
to split a cluster, the way nom does, when the format's length really is a raw
unit count) instead of being limited to whole-grapheme steps. It would also make
the consume-N-units loop a single call rather than a hand-rolled `Read` loop
with an overshoot check.

This is the underlying gap behind the missing combinators in "No length-prefixed
combinators for count-driven matching". That item proposes the `LengthData` /
`LengthCount` factories, this one is the lexer surface they'd want underneath.
