# Alias of a Preserve leaf rule renders an empty ToString()

`AliasRule` wrapping a Preserve leaf rule produces a tree node whose `ToString()` returns `""` even though the rule matched real text. The rebadge step in `AliasRule.TryParseRule` (`src/InductorParser/AliasRule.cs`, the `foreach (var child in innerSymbol.Children)` loop, originally lines 86-90) assumes the inner Symbol is a composite with children to lift. When the inner rule is a leaf-emitting rule under `FlattenType.Preserve` (`Token('a').Preserve()`, `Literal("abc").Preserve()`, `OneOf("abc").Preserve()`, etc.), the inner returns a *leaf* Symbol: it carries its match as text in `_leafChars`, with an empty `Children` list. The rebadge loop iterates that empty list, adds nothing, and the alias's own `Symbol` ends up an empty composite. Its `ToString()` walks the (empty) child list and renders `""`. `SourceText` reads the recorded match span instead and still returns the matched text, so the two accessors disagree silently.

The user-visible consequence: `Literal("abc").Preserve().AliasedAs("word").Parse("abc").Tree.ToString()` returns `""`, while the same leaf used directly (`Literal("abc").Preserve().Parse("abc").Tree.ToString()`) returns `"abc"`. Wrapping a Preserve leaf in an alias to give it a name silently empties it out of the rendered tree. The same shape bites a `FlattenType.Flatten` alias (an unnamed `Alias(...)`): the inner leaf is dropped instead of bubbling up into the parent. This is exactly the "two accessors disagree" tree-shape bug class that backlog `i1nt` flagged for `Rules.Integer()`.

## Verify the Bug (Write Test First)

The test goes in the existing `src/InductorParser.Tests/Rules/AliasRuleTests.cs` (the `SourceText / SourceRange` section). It already imports everything needed.

```csharp
[Test]
public void Alias_of_a_Preserve_leaf_inner_renders_the_matched_text_in_ToString()
{
    // The inner is a Preserve leaf: Literal carries its match as text
    // (a leaf Symbol), not as child Symbols. The rebadge lifts the
    // inner Symbol's children into the alias's child list, but a leaf
    // has no children, so the alias ends up an empty composite.
    // SourceText reads the recorded span and is fine; ToString walks
    // the (empty) child list and renders "". The two accessors
    // disagree silently.
    var inner = Literal("abc").Preserve();
    var alias = inner.AliasedAs("word");

    var result = alias.Parse("abc");

    Assert.That(result.Success, Is.True, result.ErrorMessage);
    Assert.That(result.Tree!.SourceText, Is.EqualTo("abc"));
    Assert.That(result.Tree!.ToString(), Is.EqualTo("abc"),
        "ToString() on the alias node should render the matched text, " +
        "the same as the inner leaf rendered directly.");
    Assert.That(result.Tree!.Find(inner), Is.Null,
        "Rebadge still hides the inner leaf's identity under the alias.");
}
```

Run with:

```
dotnet test --filter "FullyQualifiedName~AliasRuleTests.Alias_of_a_Preserve_leaf_inner_renders_the_matched_text_in_ToString"
```

Pre-fix the `SourceText` assertion passes (the recorded span is fine) but the `ToString()` assertion fails: expected `"abc"`, was `string.Empty`.

## Fix

The rebadge has to distinguish a composite inner (lift its children) from a leaf inner (the leaf *is* the content, there are no children to lift).

1. In `src/InductorParser/SyntaxTree/Symbol.cs`, expose the existing private `_isLeaf` field through a new internal accessor next to `LeafMemory`:

   ```csharp
   internal bool IsLeaf => _isLeaf;
   ```

2. In `src/InductorParser/AliasRule.cs`, replace the rebadge `foreach` loop. When `innerSymbol.IsLeaf` is true: in Preserve mode, mark the alias so it emits its own *leaf* Symbol carrying the matched span (`new Symbol(Id, FlattenType, matchedSpan, lexer.Context)`); in Flatten mode, add the inner leaf itself to `outputSymbols` so it bubbles up. Otherwise keep lifting `innerSymbol.Children` as before. Emitting the alias as a leaf keeps the rebadge contract intact (`Find(inner)` still returns null — the inner's identity is fully hidden) because the alias leaf carries only the alias's Id.

## Verify the Fix

Re-run the same command:

```
dotnet test --filter "FullyQualifiedName~AliasRuleTests"
```

All 30 `AliasRuleTests` pass (29 existing plus the new leaf-inner test). The full `InductorParser.Tests` suite stays green under both engines (`./test.sh both`): 5689 passed, 2 skipped, 0 failed.
