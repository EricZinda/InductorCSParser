# Permutation sample (a custom Rule ported from parsec)

A worked example of taking a combinator someone actually shipped and
rewriting it with InductorParser, where the interesting part is a *custom
rule* that the built-ins genuinely can't express. The original is Haskell
parsec's `permute` (the `Text.Parsec.Perm` module): match a set of items in
any order, each exactly once. The rewrite's centerpiece is a user-defined
`Rule` subclass, `PermutationRule`.

## What's the original

parsec is one of the oldest and most widely copied parser-combinator
libraries. Its `Text.Parsec.Perm` module, Copyright (c) Daan Leijen 1999-2001
and (c) Paolo Martini 2007, BSD-licensed
(https://hackage.haskell.org/package/parsec), implements permutation parsers.
The algorithm is from the paper "Parsing Permutation Phrases" by Arthur Baars,
Andres Löh, and Doaitse Swierstra.

`Original/parsec_perm_example.hs` is a representative program built on it,
written from the module's documented API (it's Haskell, so it doesn't build in
this .NET solution, and it's kept for reference only). It parses three
attributes, `width`, `height`, and `depth`, in any order, into a `Box`:

```haskell
box = permute (Box
  <$$> attribute "width"
  <||> attribute "height"
  <||> attribute "depth")
```

`permute` runs each parser exactly once, in whatever order the input presents
them, and hands the results to the combining function (`Box`) in the order the
parsers were written. parsec's docs note the catch that each `(<||>)` parser
must consume input (no empty match).

## Why this needs a custom rule

The built-in combinators can express "in any order" only by spelling out
every ordering as an alternative:

```csharp
Or(And(A, B, C), And(A, C, B), And(B, A, C),
   And(B, C, A), And(C, A, B), And(C, B, A))
```

That's N! branches: six for three items, twenty-four for four, and it
re-tries each item across many branches. The factorial blowup is exactly why
parsec ships `permute` as a primitive instead of leaving it to the equivalent
of `Or`. There's no `BetweenInclusive`, `ScanWhile`, or repetition combinator
that helps, because permutation isn't repetition: it's "each of these
specific, distinct rules once, order-free."

So `Rewrite/PermutationRule.cs` subclasses `Rule` and overrides
`TryParseRule`. It matches in N rounds of at most N attempts (N-squared, not
N!): each round, try every not-yet-matched item at the current position and
keep the first that matches. This is a genuinely different surface from the
existing external custom rules (`src/InductorParser.ExternalContractTests/`)
and from a plain composite: it drives a *set* of child rules competitively and
tracks which ones are spent, which `AndRule` (fixed order) and `OrRule` (first
match wins, no "exactly once") don't do.

It's built entirely on the public + protected surface: `ParseChild` runs each
item with its own transaction (a failed item rolls the cursor back on its own,
so the next item is tried at the same spot), `lexer.TickBudget()` bounds the
outer loop, `lexer.RecordCompositeFailure(...)` anchors the failure, and the
Symbol it returns is built from `lexer.Input` / `lexer.Position` / `lexer.Context`
the way every composite builds its node. No internal members.

It inherits two limits from `permute`'s documented behavior, both spelled out
in the rule's header comment: each item must consume input (no
empty match), and items are matched greedily in declaration order, so they
should have disjoint first characters (distinct literal prefixes). The
practical use, a fixed set of distinctly-named fields, doesn't hit either.

## What the InductorParser version adds

parsec reports failures through its own `SourcePos`. The rewrite keeps the
same `Box` record and the same any-order behavior and adds a positioned, named
error: each attribute and the permutation as a whole carry a `.WithError`, and
the failure comes back as a `(charIndex, line, column)` triple.

## Side-by-side

Same inputs, both parsers.

```
width=4 height=5 depth=6
depth=6 width=4 height=5
height=5 depth=6 width=4
```

Original: all three give `Box {width = 4, height = 5, depth = 6}`
Rewrite: all three give `Box { Width = 4, Height = 5, Depth = 6 }`

```
width=4 height=5
```

Original: parse error (expecting "depth")
Rewrite: `line 1, column 17: expected the 'depth' attribute`

```
width=4 width=5 depth=6
```

Original: parse error at the second "width"
Rewrite: `line 1, column 9: expected the 'height' attribute`

The any-order equivalence is the headline: the first three inputs are
different strings that produce the same `Box`, which is the only reason a
permutation parser exists.

## Layout

```
E2ESamples/Permutation/
  README.md                      this file
  Permutation.csproj             one project, the rewrite plus its tests
  Original/
    parsec_perm_example.hs       the parsec reference program, reference-only
  Rewrite/
    PermutationRule.cs           the custom Rule (the port of permute)
    BoxParser.cs                 the grammar glue + the Box projection
  Tests/
    PermutationTests.cs          golden orderings, reject corpus, custom-rule unit tests
```

`Permutation.csproj` is wired into `InductorParser.sln`, so `dotnet build` at
the repo root builds it, and `dotnet test
E2ESamples/Permutation/Permutation.csproj` runs the corpus.

## Lines of code

|                                       | Lines |
| ------------------------------------- | ----- |
| Original/parsec_perm_example.hs       |    51 |
| Rewrite/PermutationRule.cs            |   144 |
| Rewrite/BoxParser.cs                  |   102 |

The raw totals aren't an apples-to-apples comparison. The parsec `Original` is
51 lines counting its header comment and the worked-example notes. The actual
program (the `Box` type, `attribute`, and `box`) is about 15 lines, and `permute`
itself ships inside the library so a parsec user writes zero lines for it.

`PermutationRule.cs` is 144 lines, of which about 90 are comment: the
attribution, the "why N! built-ins won't do" rationale, the two documented
limits, and inline notes on the round-robin loop. The matching logic is
roughly 40 lines. That's the cost of the combinator parsec gives its users for
free, written once. `BoxParser.cs` (the grammar glue and the typed-AST
projection) is comparable in size to the parsec program once you discount its
header.

## Worked example

```csharp
Box a = BoxParser.Parse("width=4 height=5 depth=6");
Box b = BoxParser.Parse("depth=6 width=4 height=5");
// a == b == new Box(4, 5, 6)
```

The parse tree is `box` → [the `width`, `height`, `depth` digit nodes, in
input order], with the name literals, the `=` signs, and the whitespace all
deleted. The projection finds each digit node by name, so it reads the same
`Box` no matter what order the attributes were written in.

```csharp
BoxParser.TryParse("width=4 height=5", out _, out var error);
// error.ToString() == "line 1, column 17: expected the 'depth' attribute"
```

## What this exercise found

Permutation is a genuine gap. There's no built-in that matches a set of
distinct rules order-free without writing out the factorial expansion by hand,
and the round-robin algorithm that avoids the blowup has to live below the
combinator layer. The friction is filed as a backlog item:

- `0001-no-permutation-combinator-for-any-order-matching.md` proposes a
  `Permutation(params Rule[])` factory (and an optional-items variant
  mirroring parsec's `<|?>`) so grammars that need any-order fields don't each
  reinvent this rule.

Nothing in the custom-rule surface fought back. `ParseChild`'s
per-item transaction gave the "try, fail, retry at the same spot" behavior for
free, and `RecordCompositeFailure` put the failure where a user would look. The
`Rule` base class handled the transaction, the budget, and the
Delete/Flatten/Preserve normalization without extra work.

## Running it

    dotnet build E2ESamples/Permutation/Permutation.csproj
    dotnet test  E2ESamples/Permutation/Permutation.csproj

21 tests in three fixtures. The golden fixture parses every ordering of the
three attributes and checks they all produce the same `Box`. The reject
fixture asserts the right position and message for a missing attribute, a
duplicate, empty input, and trailing junk. The custom-rule fixture exercises
`PermutationRule` directly on parsec's canonical character-permutation shape.
