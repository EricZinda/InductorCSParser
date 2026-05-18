# Missing factory: a SeparatedList rule for separator-delimited lists

Surfaced building the PEP 508 dependency-specifier sample in
`E2ESamples/Pep508/`.

A comma-separated list shows up twice in that grammar: the extras list
(`[security, socks]`) and the version list (`>=1.0, <2.0`). Both are the
classic `item (separator item)*` shape. Written out with the current
factories each one is:

```csharp
And(item, ZeroOrMore(And(whitespace, Token(','), whitespace, item)))
```

That's a five-piece rule for a shape every config-ish grammar needs, and
the JSON, CSS, and arithmetic grammars under `E2EExamples/` all build it
by hand too. It would read better as one call:

```csharp
SeparatedList(item, Token(','))                     // required: one or more
SeparatedList(item, Token(','), allowEmpty: true)   // zero or more
```

Two things make this more than sugar:

1. The inner `item` after a separator usually wants its own
   `.WithError` ("expected an extra name after ','"). Because
   `.WithError` is set-once per rule instance, that forces a second
   `item` instance today (see backlog item 0a05). A `SeparatedList`
   factory could take the after-separator message as a parameter and
   build the second instance internally.

2. The trailing-separator case (`[a,]`, `>=1.0,`) is easy to get wrong.
   The hand-written `ZeroOrMore(And(sep, item))` rolls the whole failed
   iteration back to before the separator, so the error lands on the
   separator instead of on the missing item. A factory could position
   that diagnostic correctly once, for every grammar.

Proposal: add `SeparatedList(Rule item, Rule separator, bool allowEmpty
= false, Rule? surroundingWhitespace = null, string? itemError = null)`
to `Rules.cs`, returning the `And(item, ZeroOrMore(...))` tree with the
after-separator `item` carrying `itemError`.
