# `result.Tree` is null when root rule is FlattenType.Flatten — surprises grammar authors who reach for `Tree.Find`

The first version of the Versionize ConventionalCommit rewrite under [E2ESamples/Versionize/Rewrite/ConventionalCommitParserRewrite.cs](../E2ESamples/Versionize/Rewrite/ConventionalCommitParserRewrite.cs) defined the header rule as `And(Type, Optional(...), ..., Subject, Eof())` without an explicit `Flatten`. `And` defaults to `FlattenType.Flatten`, so on success the parse produces a list of top-level children rather than a single root Symbol, and `result.Tree` returns `null` per [ParseResult.cs:61](../src/InductorParser/ParseResult.cs#L61) ("exactly one Symbol").

The first run hit a `NullReferenceException` from `headerResult.Tree!.Find(Type)`, which was the natural way to extract the type / scope / subject children. Adding `.Flatten(FlattenType.Preserve)` to the And fixed it. The fix is a one-liner once you know what to look for, but the failure mode is surprising: every other PEG combinator we know returns a tree. The fact that the root needs explicit Preserve to be tree-shaped is invisible at the call site.

The friction shows up specifically when a grammar author reaches for `result.Tree.Find(rule)`. The current API offers `result.Symbols` and `result.Tree`; nothing flags that `Tree` will be null for the most common composite (`And`).

## Possible fixes

1. **Doc-comment on `ParseResult.Tree`**: spell out the "exactly one Symbol" condition AND name the common case it bites: "`Tree` returns null when the root rule is FlattenType.Flatten (the default for `And` / `Or` / count rules), because those rules lift their children into the top-level Symbols list. Wrap the root in `.Flatten(FlattenType.Preserve)` if you want a single tree to walk." The doc currently describes the condition but doesn't name the trap.
2. **A `result.Find(rule)` shortcut** that walks `Symbols` instead of requiring a single-root tree. So `result.Find(Type)` works on either a Preserve-root tree or a Flatten-root list, and the grammar author doesn't need to know which one their root produces.
3. **A debug-mode warning** when the root rule's FlattenType is Flatten and the caller never reads `Symbols` (only `Tree`). Probably overkill.

Option 2 is the strictly-better ergonomic fix: it makes the natural way to extract a named child work regardless of root flatten type.

## Verify the friction (write test first)

Add a test that mirrors the rewrite's first attempt:

```csharp
var typeRule = ZeroOrMore(OneOf(TokenSet.Ascii.Letters)).As("type").Flatten(FlattenType.Preserve);
var rule = And(typeRule, Literal(": "), AnyToken());
rule.Compile();
var result = rule.Parse("ab: c");

Assert.That(result.Success, Is.True);
Assert.That(result.Tree, Is.Null);   // surprise: even though parse succeeded
Assert.That(result.Find(typeRule), Is.Not.Null);   // <- this would be the fix
```

Today the second assertion would need `result.Symbols.SelectMany(s => s.Walk()).First(s => s.Is(typeRule))` or similar. A `result.Find(rule)` shortcut would make the third line work directly.
