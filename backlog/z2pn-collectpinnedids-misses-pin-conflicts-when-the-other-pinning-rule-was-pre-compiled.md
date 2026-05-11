# CollectPinnedIds misses pin conflicts when the other pinning rule was pre-compiled

`CollectPinnedIds` in [src/InductorParser/Rule.cs](../src/InductorParser/Rule.cs) used `!r._sealed && idValue >= SymbolRanges.CustomRangeStart` to decide which rules count as "user pinned" for the duplicate-pin check. The intent was to exclude two non-user kinds of assigned ids: rune-range pre-pins (Token('a') sets Id=0x61 in its constructor) and anonymous custom-range ids stamped by a previous Compile on a sub-rule. The `_sealed` half of the check filed both into the same bucket: a sub-rule that the user explicitly pinned via `.As(SymbolId.Custom(N))` and then compiled standalone now had `_sealed=true`, which made `isUserPinnedCustom=false`, so the rule wasn't tracked in `pinnedRules`. A second rule in the larger grammar that pinned the same id silently won the slot, the conflict went undetected, and `Tree.Find` against either rule reference returned the same nodes regardless of which rule actually matched.

The mistake was conflating "rule was previously compiled" with "rule wasn't user-pinned." `_idUserPinned` is the precise flag (set only by `.As(SymbolId)`, persists through sealing), so swapping `!r._sealed` for `r._idUserPinned` keeps the rune-range and anonymous-id exclusions while letting pre-compiled user pins participate in the conflict check.

This is a real path: a shared rule with a custom-pinned SymbolId for stable serialization, compiled standalone (e.g., from a static initializer in a "tokens" library), then composed into multiple larger grammars. The first grammar that adds a second pinning rule with the same id sees no compile-time error, and the parse tree silently merges the two rules.

## Verify the Bug (Write Test First)

Test in [src/InductorParser.Tests/Core/IdAssignmentTests.cs](../src/InductorParser.Tests/Core/IdAssignmentTests.cs), as a sibling of `Two_reachable_rules_pinned_to_the_same_SymbolId_fail_to_compile`:

```csharp
[Test]
public void Pre_compiled_sub_rule_pin_collides_with_unsealed_sibling_pin()
{
    var pinned = new SymbolId(SymbolRanges.CustomRangeStart + 5678);
    var preCompiled = OneOrMore(OneOf(TokenSet.Letters)).As(pinned).As("first");
    preCompiled.Compile();

    var unsealed = OneOrMore(OneOf(TokenSet.Digits)).As(pinned).As("second");
    var doc = And(preCompiled, unsealed);

    var exception = Assert.Throws<InvalidOperationException>(() => doc.Compile());
    Assert.That(exception!.Message, Does.Contain(pinned.Value.ToString()));
    Assert.That(exception.Message, Does.Contain("first"));
    Assert.That(exception.Message, Does.Contain("second"));
}
```

Run:

```
dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj --filter "FullyQualifiedName~Pre_compiled_sub_rule_pin_collides_with_unsealed_sibling_pin"
```

Before the fix the test fails with `Expected: <System.InvalidOperationException> But was: null` because Compile silently accepts the duplicate pin.

## Fix

In [Rule.cs](../src/InductorParser/Rule.cs) `CollectPinnedIds`, replace

```csharp
bool isUserPinnedCustom = !r._sealed && idValue >= SymbolRanges.CustomRangeStart;
```

with

```csharp
bool isUserPinnedCustom = r._idUserPinned && idValue >= SymbolRanges.CustomRangeStart;
```

and update the doc comment that justifies the rune-range / prior-Compile exclusions to describe the `_idUserPinned` gate instead of the `_sealed` proxy.

## Verify the Fix

Re-run the new test plus the existing pin / name / id-assignment fixtures:

```
dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj --filter "FullyQualifiedName~IdAssignment|FullyQualifiedName~NameOf|FullyQualifiedName~Compile"
```

All pass. The full suite (`dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj`) also passes.
