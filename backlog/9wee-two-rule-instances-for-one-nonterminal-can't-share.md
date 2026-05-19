# Two rule instances for one nonterminal can't share an As name

Surfaced building the PEP 508 dependency-specifier sample in
`E2ESamples/Pep508/`.

In the extras list `[a, b, c]`, every entry is the same nonterminal: an
identifier. But the first entry and the after-a-comma entries want
different `.WithError` text. The after-comma one should say "expected
an extra name after ','", the first one shouldn't (it would be wrong
for `[ ]`-shaped input). Because `.WithError` is set-once per rule
instance, that means two `Rule` instances of the identical shape.

The natural thing is to name both `.As("extra")`, since both genuinely
are extras and the parse tree wants both to show up as `extra` nodes.
`Compile` rejects it:

```
Two reachable rules share the name 'extra'. Each .As(string) name
must be unique within a grammar.
```

So the author has to either invent a second, slightly-wrong name
(`"extra"` and `"extraAfterComma"`, leaking a parser-internal
distinction into the tree's node names), or leave the second instance
unnamed and keep it in the tree with `.Preserve()`. The sample took the
second route, which leaves the parse tree asymmetric: the first extra is
an `extra`-labelled node and the rest are anonymous `And` nodes that
happen to be `.Preserve()`d. Tree consumers that match on rule identity
(`symbol.Is(rule)`) still work, but `PrintTree` and trace output show
the mismatch.

The name-uniqueness check has a real purpose (`NameOf` would be
ambiguous, traces would be confusing). But "the same nonterminal in two
positions, each needing its own message" is a normal grammar shape, not
an abuse. The check shouldn't make it awkward.

Options:

- Allow duplicate `.As(string)` names for reachable rules. Ids already
  linear-probe to stay distinct, so lookups by id still work. `NameOf`
  returning the same string for two rules is arguably fine when they
  are, semantically, the same thing.
- Or add a per-use-site message overload that doesn't need a fresh
  name, e.g. an `And`/`ZeroOrMore` overload, or a wrapping rule, that
  attaches a `.WithError` to a shared inner rule for one use only.

Related: backlog item 0a02 (a `SeparatedList` factory could build the
second instance internally and hide this entirely from the author).

Hit again building the Cron sample (`E2ESamples/Cron/`, backlog 018a).
The cron grammar's "item" nonterminal appears at the head of a field
and after a comma. Friction 1 in that sample's README forces a separate
`Rule` instance for the after-comma item so it can carry its own
`.WithError`, and both instances genuinely are "item". `Compile`
rejected the shared name and they had to be split into `"item"` and
`"listItem"`. Second sample, same wall.
