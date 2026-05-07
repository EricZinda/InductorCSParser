# Untitled

- FirstConsumedTokens cached before normalization rewrites the rule's matchable runes

`Rule.Compile(NormalizationForm?)` (src/InductorParser/Rule.cs lines 411-445) runs `ComputeRuleStartAll` BEFORE `CollectNormalizationOffendersAll`. The first pass caches each rule's `FirstConsumedTokens` against the rule's user-supplied `_set` / `_expected`. The second pass then form-projects literal-bearing rules: `GraphemeRule`, `LiteralRule`, `LiteralIgnoreAsciiCaseRule` rewrite `_expected`, and `OneOfRule` / `NoneOfRule` rewrite `_set`. For a rule whose user-supplied text doesn't match its own normalized form (canonical singletons like U+212B → U+00C5 under FormC, U+212A → U+004B, U+2126 → U+03A9, or any character whose NFD decomposes into a different first rune), the cached `FirstConsumedTokens` keeps the pre-projection runes while the post-projection rule actually matches a different first rune. `BetweenInclusiveRule` and `OrRule` consult the cached `FirstConsumedTokens` via `CannotMatchLookahead` and wrongly skip the rule on input the normalized form would make match. User-visible consequence: a grammar like `OneOrMore(Token("Å"))` compiled with the default `FormC` fails on input `"Å"` with `"Parse failed at offset 0: unexpected 'Å'"`, even though the lexer-normalized input (`"Å"`) is exactly what the post-projection rule matches. Same shape for `OneOf(TokenSet.Single(0x212B))` inside any composite that uses the lookahead shortcut, and for `Literal(string)` whose first rune normalizes to a different first rune.

## Verify the Bug (Write Test First)

Add this test to `src/InductorParser.Tests/Rules/OneOfRuleTests.cs`. With the buggy ordering, `BetweenInclusiveRule.TryParseRule`'s lookahead shortcut peeks the input's first rune (0x00C5 after Parse-time NFC), checks against the cached `FirstConsumedTokens = {0x212B}`, concludes the inner `OneOf` can't match, and fails `OneOrMore` at AtLeast=1. The inner `OneOf` would have matched if it had been called: its post-projection set is `{0x00C5}`, and the lexer hands it a token of U+00C5.

```csharp
[Test]
public void OneOf_in_OneOrMore_matches_after_FormC_canonical_singleton_substitution()
{
    // U+212B ANGSTROM SIGN is a canonical singleton: under FormC it
    // converts to U+00C5 LATIN CAPITAL LETTER A WITH RING ABOVE.
    // OneOf(TokenSet.Single(0x212B)) compiled with FormC has its set
    // re-projected at Compile time: _set becomes {0x00C5}. The lexer
    // sees input runes in the same form (Parse normalizes the input
    // first), so a token of U+00C5 should match the projected set.
    //
    // The bug: ComputeRuleStartAll runs BEFORE the normalization-form
    // pass that mutates _set, so OneOfRule.FirstConsumedTokens stays
    // pinned to the pre-projection set {0x212B}. OneOrMore's lookahead
    // shortcut peeks the input's first rune (0x00C5), checks it
    // against the cached {0x212B} (not Contains), concludes the inner
    // can't match, and fails the OneOrMore at AtLeast=1. The inner
    // OneOf would have matched if it had been called.
    var rule = OneOrMore(OneOf(TokenSet.Single(0x212B)))
        .Compile(System.Text.NormalizationForm.FormC);
    var result = rule.Parse("Å");
    Assert.That(result.Success, Is.True, result.ErrorMessage);
}
```

Add a sibling test to `src/InductorParser.Tests/Rules/GraphemeRuleTests.cs` for the `Token` (GraphemeRule) shape, which has the same staleness via `_expected`:

```csharp
[Test]
public void Token_in_OneOrMore_matches_after_FormC_canonical_singleton_substitution()
{
    // Same staleness shape as the OneOf test in OneOfRuleTests:
    // GraphemeRule.ComputeRuleStart reads _expected and pins
    // FirstConsumedTokens to the first rune of that text. The Compile
    // pipeline then runs the normalization-form pass, which can
    // rewrite _expected (U+212B ANGSTROM SIGN converts to U+00C5
    // LATIN CAPITAL LETTER A WITH RING ABOVE under FormC). The
    // cached FirstConsumedTokens stays {0x212B} even though the rule
    // now matches a token of U+00C5. OneOrMore's lookahead shortcut
    // peeks the input's first rune (also U+00C5 after Parse-time
    // input normalization), checks against the stale set, and bails
    // before calling the rule.
    var rule = OneOrMore(Token("Å"))
        .Compile(System.Text.NormalizationForm.FormC);
    var result = rule.Parse("Å");
    Assert.That(result.Success, Is.True, result.ErrorMessage);
}
```

Run:

```
dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj --filter "FullyQualifiedName~_in_OneOrMore_matches_after_FormC_canonical_singleton_substitution"
```

Expected: both fail with `"Parse failed at offset 0: unexpected 'Å'"`.

## Fix

In `src/InductorParser/Rule.cs` `Compile(NormalizationForm?)`, swap the order so `CollectNormalizationOffendersAll` runs BEFORE `ComputeRuleStartAll`. Both passes already require `ValidateAll` to have run, so neither precondition shifts. Move the entire `if (normalizeInput.HasValue) { ... }` block immediately after `ValidateAll`, then run `ComputeRuleStartAll` after that. Update the comment on `ComputeRuleStartAll` to note "after the normalization-form pass so the cached `FirstConsumedTokens` reflects the post-normalization `_set` / `_expected`."

```csharp
visited.Clear();
ValidateAll(this, visited);

if (normalizeInput.HasValue)
{
    // ... existing CollectNormalizationOffendersAll block ...
}

var computing = new HashSet<Rule>(ReferenceComparer<Rule>.Instance);
visited.Clear();
ComputeRuleStartAll(this, visited, computing);
```

The fix is order-only; no other rule code changes. `ComputeRuleStartAll` is idempotent on a fully-validated graph and reading the post-projection `_set` / `_expected` gives the same answer the rule would compute by hand on its actual matching data. When `normalizeInput` is null the order doesn't matter because `_set` / `_expected` aren't touched.

## Verify the Fix

Re-run the same tests:

```
dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj --filter "FullyQualifiedName~_in_OneOrMore_matches_after_FormC_canonical_singleton_substitution"
```

Expected: both pass. Run the full suite (`dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj`) to confirm no other test broke; the suite is 1905/1907 (2 pre-existing timing skips) before and after.
