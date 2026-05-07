- GraphemeRule normalization re-pin silently overrides .As(new SymbolId(...))

`GraphemeRule.CollectNormalizationOffenders` (src/InductorParser/GraphemeRule.cs lines 87-90) unconditionally calls `SetIdInternal(new SymbolId(runeValue))` whenever the post-normalization expected text is single-rune. For an unnamed Token like `Token('Ω')` this is the documented and desired behavior: the leaf id tracks the post-normalization rune (covered by `Token_with_singleton_decomposable_rune_repins_id` in NormalizationTests.cs). But when the user pinned an explicit SymbolId via `.As(new SymbolId(custom))`, the same code path silently overwrites that pin with the post-normalization rune value, breaking the documented promise of `.As(SymbolId)` ("Pin an explicit SymbolId on this Rule for stable numbering across versions, useful when serializing parse trees").

The shape that bites: `Token('Ω').As(new SymbolId(0x200100)).Compile()`. The constructor pins Id=0x2126 (rune of OHM SIGN). The user's `.As` overrides Id=0x200100 (custom range). Compile's normalization pass projects U+2126 → U+03A9 under FormC and re-pins Id=0x03A9, dropping the user's 0x200100 on the floor. After compile, `rule.Id.Value == 0x03A9` instead of `0x200100`. Every parse-tree leaf the rule emits carries 0x03A9, not the user's pin, so any downstream code that serializes parse trees and reads them back keyed on the pinned id loses the rule. `rule.NameOf(0x200100)` returns null even though the user pinned to that exact id moments ago.

The bug only fires for canonical-singleton characters where post-normalization is also single-rune (U+212B → U+00C5 ANGSTROM, U+2126 → U+03A9 OHM, U+2329 → U+3008, U+212A → U+004B KELVIN, ...). Multi-rune-becomes-multi-rune cases (Token('Å') under FormD → "Å") don't fire because TrySingleRuneValue returns false on the projection. Multi-rune-becomes-single-rune cases (Token("é") under FormC → "é") DO fire and the re-pin is desired there for leaf-id consistency between Token('é') and the multi-rune form, so the fix has to preserve that shape.

Sibling check: `LiteralRule.CollectNormalizationOffenders` and `LiteralIgnoreAsciiCaseRule.CollectNormalizationOffenders` only update `_expected`, never touch Id. `OneOfRule.NormalizeAndValidate` only projects `_set`, never Id. So the bug surface is GraphemeRule-only.

## Verify the Bug (Write Test First)

Add this test to `src/InductorParser.Tests/Core/NormalizationTests.cs` next to the existing `Token_with_singleton_decomposable_rune_repins_id`. Imports already include `using static InductorParser.Rules` and `using static InductorParser.Tests.UnicodeExamples` (which gives `OhmGrapheme = "Ω"`); add `using InductorParser.SyntaxTree;` for the `SymbolId` and `SymbolRanges` types.

```csharp
[Test]
public void Token_with_singleton_decomposable_rune_preserves_user_pinned_id()
{
    // Sibling of Token_with_singleton_decomposable_rune_repins_id.
    // The unnamed case re-pins Id to the post-normalization rune,
    // which is desired (leaf-id consistency between Token('Ω') and
    // Token('Ω').Compile(FormC)). But when the user pinned an
    // explicit SymbolId via .As(new SymbolId(...)), Compile must NOT
    // overwrite it. .As(SymbolId) is documented as the
    // stable-numbering hook, useful for serialized parse trees, and
    // a silent re-pin under normalization defeats that promise.
    int pinned = SymbolRanges.CustomRangeStart + 0x100;
    var rule = Token(OhmGrapheme).As(new SymbolId(pinned));
    rule.Compile();
    Assert.That(rule.Id.Value, Is.EqualTo(pinned),
        "User-pinned SymbolId survives canonical-singleton normalization");
}
```

Run:

```
dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj --filter "FullyQualifiedName~Token_with_singleton_decomposable_rune_preserves_user_pinned_id"
```

Expected before any fix: failure with "Expected: 2097408 But was: 937" (937 is 0x03A9, the post-normalization OMEGA rune; 2097408 is the user's 0x200100 pin).

## Fix

Two-file change.

In `src/InductorParser/Rule.cs`, add an `_idUserPinned` flag set by `.As(SymbolId)` (not by `SetIdInternal`) so opportunistic auto-pin sites can tell a user pin from a constructor or compile-pass auto-pin:

```csharp
// alongside _idAssigned at the top of the class
private bool _idUserPinned;

// inside As(SymbolId)
public virtual Rule As(SymbolId id)
{
    ThrowIfSealed();
    Id = id;
    _idAssigned = true;
    _idUserPinned = true;
    return this;
}

// and an internal accessor for subclass auto-pin sites to consult
internal bool IsUserSymbolIdPinned => _idUserPinned;
```

In `src/InductorParser/GraphemeRule.cs` `CollectNormalizationOffenders`, gate the re-pin call on the new flag so a user pin survives the normalization pass:

```csharp
_expected = normalized;
// Re-pin the rule's Id to the post-normalization rune so leaves
// emitted by Token('Ω') and Token('Ω') (canonical-singleton
// U+2126 vs GREEK CAPITAL OMEGA U+03A9) carry the same Id under
// FormC. Skipped when the user explicitly pinned an Id via
// .As(new SymbolId(...)): the documented promise of .As(SymbolId)
// is stable numbering across versions, and a silent re-pin would
// defeat that.
if (!IsUserSymbolIdPinned && TrySingleRuneValue(_expected, out int runeValue))
    SetIdInternal(new SymbolId(runeValue));
```

Important to leave the no-`As` cases untouched: the existing `Token('Ω').Compile()` test expects Id to re-pin to 0x03A9, and the multi-rune-becomes-single-rune `Token("é").Compile(FormC)` shape relies on the re-pin to stay leaf-id-consistent with `Token('é')`. Both cases have `_idUserPinned == false`, so they keep the existing behavior.

`LiteralRule` and `LiteralIgnoreAsciiCaseRule` need no change: their `CollectNormalizationOffenders` already only touches `_expected`, never Id.

## Verify the Fix

Re-run the new test:

```
dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj --filter "FullyQualifiedName~Token_with_singleton_decomposable_rune_preserves_user_pinned_id"
```

Expected: passes. Then run the full suite (`dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj`) to confirm nothing else broke. Pre-fix the suite passes 1903 / 1905 (2 timing skips); post-fix the suite passes 1904 / 1906 (the new regression test plus the existing 2 skips).
