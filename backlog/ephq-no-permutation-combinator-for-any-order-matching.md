# No permutation combinator for any-order matching

There's no built-in way to say "match each of these distinct rules exactly
once, in any order." The only composition that expresses it is the full
factorial expansion:

```csharp
// "A, B, and C in any order" today:
Or(And(A, B, C), And(A, C, B), And(B, A, C),
   And(B, C, A), And(C, A, B), And(C, B, A))
```

Six branches for three items, twenty-four for four, and each item is re-tried
across many branches. This isn't repetition, so `BetweenInclusive` /
`ScanWhile` / the count rules don't help. `And` is fixed-order and `Or` is
first-match-wins with no "exactly once" bookkeeping, so neither composes into
permutation either.

This came up rewriting Haskell parsec's `permute` combinator (see
`E2ESamples/Permutation/`). parsec ships `Text.Parsec.Perm` precisely because
the factorial expansion is untenable. The algorithm is from the paper "Parsing
Permutation Phrases." Real grammars want this for any-order field sets:
HTML/SVG attributes, command-line flags, CSS shorthand, record literals where
the fields can appear in any order.

The sample had to drop to a custom `Rule` subclass
(`Rewrite/PermutationRule.cs`, ~40 lines of matching logic) that matches in N
rounds of at most N attempts (N-squared, not N!): each round, try every
not-yet-matched item at the current position and keep the first that matches.
That's a fine proof that the custom-rule surface can do it, but it's friction
for a shape this common, and everyone who needs it would rewrite the same
loop.

Proposal: add a `Permutation` factory.

```csharp
Permutation(params Rule[] items)          // each item exactly once, any order
```

and, mirroring parsec's required-vs-optional split (`<||>` vs `<|?>`), a way to
mark some items optional so a missing optional item falls back instead of
failing. The required-only form is the common case and the simplest to ship
first.

Two behaviors to settle, both surfaced by the sample's implementation:

- Items must consume input. An item that can match empty (Optional,
  ZeroOrMore) would "match" without advancing and be marked done at the wrong
  spot. parsec forbids empty-accepting parsers on `<||>` for the same reason.
  The factory should either reject a known-nullable item at Compile time or
  document the constraint.
- Greedy first-match assumes disjoint first sets. If two items can both start
  at the same position, the earlier-declared one wins, which may be wrong.
  parsec's type-level machinery sidesteps this. A practical `Permutation` over
  distinctly-named fields doesn't hit it, but the docs should say so.
