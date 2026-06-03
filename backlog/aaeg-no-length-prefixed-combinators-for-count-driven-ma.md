# No length-prefixed combinators for count-driven matching

There's no built-in way to say "read a number from the input, then consume that
much." Two shapes show up constantly in wire formats:

- Read a count N, then take exactly N characters as one value. (Bulk strings,
  netstrings, bencode strings, most binary length-delimited fields.)
- Read a count N, then run a rule exactly N times. (RESP arrays, length-prefixed
  record lists, protobuf-style repeated fields.)

Neither is expressible by composing the built-ins, because the amount consumed
isn't known until the count is read. `OneOrMore` is greedy-until-failure,
`Exactly(n, rule)` takes a compile-time constant, not a value read from the
input, and `ScanWhile` / `ScanUntil` match against a fixed set or stopper, not a
running character budget. This is genuinely context-sensitive: it's past what a
fixed grammar of `And` / `Or` / repetition can describe.

This came up rewriting nom's `length_data` and `length_count` combinators (see
`E2ESamples/Resp/`, a RESP / Redis-protocol parser). nom ships both in its
`multi` module precisely because they can't be built from the context-free
combinators. The sample had to drop to two custom `Rule` subclasses
(`Rewrite/LengthPrefixedRules.cs`, ~90 lines of matching logic across a shared
base and the two rules) that read the count out of a child rule's matched text
and then either consume that many characters or run an item rule that many
times.

The custom-rule surface handled it cleanly, but it's friction for a shape this
common, and everyone parsing a length-prefixed format would rewrite the same two
loops.

Proposal: add two factories.

```csharp
LengthData(Rule count, Rule separator)             // take exactly `count` chars as a leaf
LengthCount(Rule count, Rule separator, Rule item) // run `item` exactly `count` times
```

`count` matches a digit run whose text is parsed as a non-negative integer.
`separator` is the delimiter consumed between the count and the payload (CRLF in
RESP, ':' in netstrings). nom folds the separator into the count parser with
`terminated`. An explicit separator parameter reads clearer in a fluent C# API.

Two behaviors to settle, both surfaced by the sample:

- The count unit is finer than what the lexer can consume. A rule advances the
  cursor only through `lexer.Read()` (whole graphemes, or runes in rune mode),
  but `lexer.Position` is in UTF-16 code units, so a code-unit-counted length
  can name an offset inside a grapheme that `Read` can't land on. The sample
  rejects such a length rather than splitting the cluster, which is stricter
  than nom's `length_data` (it splits freely). A `LengthData` factory either
  inherits that reject-mid-token behavior or depends on a new lexer primitive to
  advance by a fixed count. That primitive is its own item, "No primitive to
  advance the cursor by a fixed code-unit (or byte) count," which is the real
  fix here.
- Negative / null markers. RESP uses `$-1` and `*-1` for null. The sample
  handles those as a separate `Or` branch and keeps the custom rules to
  non-negative counts. A general factory should probably stay non-negative and
  leave null handling to the grammar, the way the sample does.
