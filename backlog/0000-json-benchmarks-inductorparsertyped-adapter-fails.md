# JSON benchmark's InductorParserTyped adapter fails its spot-check

The JSON benchmark's `InductorParserTyped` row is dead: it throws on every
input shape, so `--spot-check` exits 1 and the four `*_InductorParserTyped`
rows come back `NA` ("Benchmarks with issues") in a normal benchmark run.
The `InductorParserToken` rows are unaffected.

This is **pre-existing**, not a regression from the lexer/rules transaction
redesign. Verified by stashing that branch's changes and running
`--spot-check` on a clean `docs` HEAD (`b8035de`): the failures are
byte-identical with and without the redesign.

## Symptom

`dotnet run -c Release --project src/Benchmarks/Benchmarks.csproj -- --spot-check`
prints:

```
[Big]  FAIL InductorParserTyped: threw InvalidOperationException: Unexpected symbol id 557057565
[Long] FAIL InductorParserTyped: threw ArgumentOutOfRangeException: Index was out of range...
[Deep] FAIL InductorParserTyped: threw ArgumentOutOfRangeException: Index was out of range...
[Wide] FAIL InductorParserTyped: threw InvalidOperationException: Unexpected symbol id 557057565
```

A normal benchmark run (`--filter *Json*`) reports the same four under
"Benchmarks with issues" and prints `NA` for their Mean / Ratio / Allocated
columns, so the apples-to-apples row the README points competitors at
("InductorParserTyped is the apples-to-apples row vs. competitors") is
missing from the table entirely.

## Where

`src/Benchmarks/Json/InductorParsers/InductorJsonParser.cs`, the
`BuildTyped` Symbol-tree walker (and `DecodeStringBody`, which it calls).

`BuildTyped` dispatches on `symbol.Is(JsonStringRule)` / `Is(JsonArrayRule)`
/ `Is(JsonObjectRule)` and, for an object, reads `symbol.Children` pairwise
(even index = key, odd index = value), relying on `JsonMemberRule`'s
key+value children being lifted into the object's child list. "Unexpected
symbol id 557057565" is the final `throw` when a child matches none of the
three rules. The "Index out of range" is `symbol.Children[i + 1]` running
off the end when the pairwise key/value assumption no longer holds.

So the parse tree the JSON grammar produces has a different shape (symbol
ids and/or child grouping) than `BuildTyped` was written against. The
grammar still parses correctly: the `InductorParser` (Token) spot-check
round-trips every shape, only the typed-tree walker is stale. Something
changed the tree shape and `BuildTyped` was not updated to match. The
benchmark is a separate solution (`Benchmarks.sln`) and not in CI, so it
went unnoticed.

## How to reproduce

1. `dotnet run -c Release --project src/Benchmarks/Benchmarks.csproj -- --spot-check`
2. Observe the four `FAIL InductorParserTyped` lines and exit code 1.

## Fix direction

Decide which side has drifted and reconcile them:

- Dump the actual Symbol tree for a small object like `{"a":"b"}` parsed by
  `InductorJsonParser.JsonRule` and compare it against what `BuildTyped`
  assumes (object children = a flat `[key, value, ...]` list, and every
  value node is one of the three named rules).
- Either update `BuildTyped` / `DecodeStringBody` to walk the tree shape
  the grammar produces today, or adjust the grammar's `.As(...)` /
  `Flatten` choices so the tree matches what the walker expects.
- Check the `JsonMemberRule` assumption first: the comment in `BuildTyped`
  says "JsonMemberRule is Flatten-default, so its two kept children ... are
  lifted into JsonObjectRule's children." If `member` symbols now survive
  in the object's child list, the pairwise read is wrong and `557057565`
  is likely the `member` rule's id.

## Verify the fix

`--spot-check` exits 0 with `OK InductorParserTyped` on all four shapes,
and a `--filter *InductorParserTyped*` run produces real Mean / Ratio
numbers instead of `NA`.
