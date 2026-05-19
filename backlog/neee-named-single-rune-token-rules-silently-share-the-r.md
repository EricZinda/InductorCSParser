# Named single-rune Token rules silently share the rune Id

`Rules.Token('a')` constructs a [GraphemeRule](src/InductorParser/GraphemeRule.cs) that pins its `Id` to the rune's code point (0x61) at construction time, via `SetIdInternal` at [GraphemeRule.cs:59](src/InductorParser/GraphemeRule.cs#L59). Two `Token('a').As("first")` and `Token('a').As("second")` rules in the same grammar both keep that auto-pinned `Id = 0x61` because `Rule.As(string)` at [Rule.cs:414](src/InductorParser/Rule.cs#L414) writes `Name` but leaves `_idAssigned` set. `Compile` then walks `AssignNamedIds` ([Rule.cs:1350](src/InductorParser/Rule.cs#L1350)) and skips both rules because `_idAssigned` is already true. The result: two distinct user-named rules silently share `SymbolId(0x61)`.

The Compile-time duplicate-id checks `CollectPinnedIds` at [Rule.cs:1297](src/InductorParser/Rule.cs#L1297) and `CheckNameUniqueness` at [Rule.cs:1328](src/InductorParser/Rule.cs#L1328) don't catch it. `CollectPinnedIds` only flags rules whose `_idUserPinned` is true (the `.As(SymbolId)` path), and `CheckNameUniqueness` only flags duplicate names (the two names differ). The two named rules sail through Compile with the same `Id`.

User-visible consequence: `Tree.Find(firstA)` and `Tree.Find(secondA)` both return the leaf the FIRST `Token('a')` rule produced, regardless of which rule actually matched it. The grammar author can't distinguish between two same-rune named Tokens in tree walking, and `NameOf(0x61)` reports back only whichever name `CollectNames` visited first.

The pattern shows up in real grammars whenever a single character has more than one role: a comma that's both `commaInList` and `argSeparator`, a `'='` that's both `assignmentEquals` and `pairSeparator`, etc.

The asymmetric `.As(SymbolId)` overload already rejects user pins into the rune range (see `As_SymbolId_rejects_pins_below_the_custom_range` in [IdAssignmentTests.cs:108](src/InductorParser.Tests/Core/IdAssignmentTests.cs#L108)) precisely to prevent this kind of silent collision. The auto-pin path slipped past that gate.

## Verify the Bug (Write Test First)

Two tests in `src/InductorParser.Tests/Core/IdAssignmentTests.cs`. The first asserts the rules get distinct ids. The second asserts `Tree.Find` resolves to different leaves.

```csharp
[Test]
public void Two_named_single_rune_Tokens_of_the_same_rune_get_distinct_ids()
{
    var firstA = Token('a').As("first");
    var secondA = Token('a').As("second");
    var doc = And(firstA, secondA);

    doc.Compile();

    Assert.That(firstA.Id, Is.Not.EqualTo(secondA.Id));
    Assert.That(firstA.Id.Value, Is.GreaterThanOrEqualTo(SymbolRanges.CustomRangeStart));
    Assert.That(secondA.Id.Value, Is.GreaterThanOrEqualTo(SymbolRanges.CustomRangeStart));
}

[Test]
public void Named_Token_leaves_carry_the_rule_id_not_the_rune_id()
{
    var firstA = Token('a').As("first");
    var secondA = Token('a').As("second");
    var doc = And(firstA, secondA);
    var result = doc.Parse("aa");

    Assert.That(result.Success, Is.True, result.ErrorMessage);
    var foundFirst = result.Find(firstA);
    var foundSecond = result.Find(secondA);
    Assert.That(foundFirst, Is.Not.Null);
    Assert.That(foundSecond, Is.Not.Null);
    Assert.That(ReferenceEquals(foundFirst, foundSecond), Is.False);
}
```

Run with:

```
dotnet test --filter "FullyQualifiedName~IdAssignmentTests.Two_named_single_rune_Tokens|FullyQualifiedName~IdAssignmentTests.Named_Token_leaves_carry"
```

Pre-fix, the first test reports `Expected: not equal to 97 / But was: 97`, and the second test reports `Tree.Find(firstA) and Tree.Find(secondA) must return different leaves`.

## Fix

Two changes:

1. In [Rule.cs:414](src/InductorParser/Rule.cs#L414) (`Rule.As(string)`), after writing `Name`, clear `_idAssigned` when the rule's id was auto-pinned (the `!_idUserPinned` check). That lets `Compile`'s `AssignNamedIds` give it a fresh name-hashed custom-range id. A user-pinned id (`.As(SymbolId)`) stays put because `_idUserPinned` is true.

2. In [GraphemeRule.cs:91](src/InductorParser/GraphemeRule.cs#L91) (`CollectNormalizationOffenders`), the post-normalization rune re-pin already skips when the user pinned a SymbolId. Extend the check to also skip when the rule has a `Name`, so the custom-range id `AssignNamedIds` assigned to a named Token doesn't get clobbered by a `SetIdInternal` call when normalization changes the expected rune (e.g., OHM SIGN U+2126 to GREEK CAPITAL LETTER OMEGA U+03A9 under FormC).

The fix doesn't change anonymous-Token behavior. `Token('a')` (no `.As`) still auto-pins to 0x61, and its leaf still carries SymbolId(0x61) so tree walkers can dispatch on `leaf.Id == 'a'`.

## Verify the Fix

Re-run the same filter:

```
dotnet test --filter "FullyQualifiedName~IdAssignmentTests.Two_named_single_rune_Tokens|FullyQualifiedName~IdAssignmentTests.Named_Token_leaves_carry"
```

Both new tests pass. Run the full `InductorParser.Tests` suite to verify no regressions: every existing `Named_*_Token_*` and `Named_*_Find_*` test continues to pass because they compare `rule.Id` against `leaf.Id` (both shift to the same custom-range value together) rather than asserting a specific rune-id value for a named Token.
