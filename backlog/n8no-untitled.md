# Untitled

- NoneOf FirstConsumedTokens excludes runes that can start passing multi-rune clusters

`NoneOfRule.ComputeRuleStart` (src/InductorParser/NoneOfRule.cs lines 72-85) publishes `~_set` (or `~_set.RunesOnlyPart` when the set has multi-rune entries) as the rule's `FirstConsumedTokens`. The reasoning in the existing comment only considers tokens whose first rune is the head of a multi-rune entry IN the set; it misses the inverse case where the first rune IS in `_set.RunesOnlyPart` but the token at the cursor is a multi-rune cluster that ISN'T in `_set._multiRuneGraphemes`. NoneOf reads a whole token: a multi-rune cluster like `é` (e + combining acute), a ZWJ family emoji starting with 0x1F468, or a regional-indicator pair would all pass `NoneOf(Single('e'))` / `NoneOf(Single(0x1F468))` / `NoneOf(Single(0x1F1FA))` because the cluster isn't a single rune in the set's rune intervals and the set has no multi-rune entries to reject it as a whole-token match. But `~Single('e')` excludes 'e' from the rule's `FirstConsumedTokens`, so `OrRule.TryParseRule`'s lookahead shortcut (peek the next rune, ask each child `CannotMatchLookahead(peekRune)`) returns true and skips the NoneOf branch entirely. User-visible consequence: a grammar that wraps `NoneOf(set)` inside `Or` (or `BetweenInclusive`) silently fails on input where NoneOf would have succeeded, returning a "Parse failed at offset 0: unexpected 'é'" error rather than consuming the cluster. Most likely to bite grammars compiled with `Compile(null)` (no normalization), but also any grammar where the set's rune-only part overlaps with the first runes of NFC-stable multi-rune clusters (skin-tone modifiers, ZWJ sequences, regional-indicator flags).

## Verify the Bug (Write Test First)

Add this test to `src/InductorParser.Tests/Rules/NoneOfRuleTests.cs`. With the buggy `~_set` formula, `OrRule` peeks 'e' (0x65), asks the NoneOf child `CannotMatchLookahead(0x65)`, the child reports `Advance.Always` and `~Single('e')` doesn't contain 'e', so the shortcut wrongly skips NoneOf and the Or has no other branches to try.

```csharp
[Test]
public void NoneOf_in_Or_matches_multi_rune_grapheme_starting_with_excluded_rune()
{
    // Pinning test for the lookahead-shortcut bug. NoneOf(Single('e'))
    // SHOULD match a multi-rune cluster like "é" (e + combining
    // acute): the cluster is multi-rune, the set has no multi-rune
    // entries, so the cluster isn't in the set and NoneOf accepts.
    //
    // But NoneOfRule.ComputeRuleStart published FirstConsumedTokens =
    // ~Single('e'), which excludes 'e' itself. OrRule's lookahead
    // shortcut peeks the first rune ('e') and asks each child
    // CannotMatchLookahead(0x65). NoneOf reports Advance.Always and
    // ~Single('e') doesn't contain 'e', so the shortcut wrongly skips
    // NoneOf. With no other branches, Or fails on input where
    // NoneOf would have succeeded.
    //
    // Compile(null) keeps the decomposed cluster. Default NFC would
    // compose "é" into "é" and the test premise (multi-rune
    // cluster starting with 'e') would vanish.
    var rule = Or(NoneOf(TokenSet.Single('e')).Preserve());
    rule.Compile(null);
    var result = rule.Parse(LatinEAcuteGrapheme);

    Assert.That(result.Success, Is.True, result.ErrorMessage);
    Assert.That(result.Tree!.ToString(), Is.EqualTo(LatinEAcuteGrapheme));
}
```

Run:

```
dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj --filter "FullyQualifiedName~NoneOf_in_Or_matches_multi_rune_grapheme"
```

Expected: failure with "Parse failed at offset 0: unexpected 'é'".

## Fix

In `src/InductorParser/NoneOfRule.cs`, replace `ComputeRuleStart` so it returns `TokenSet.Universe` for `FirstConsumedTokens`. Universe is the safe answer because the parser can't enumerate "every multi-rune cluster that could start with rune r and pass NoneOf", so it can't soundly exclude any rune from the first-consumed set. The lookahead shortcut is disabled for NoneOf branches as a result, falling back to the pre-shortcut behavior of always trying the rule.

```csharp
internal override RuleStartRequirements ComputeRuleStart()
{
    return new RuleStartRequirements(TokenSet.Universe, Advance.Always);
}
```

Update the existing comment to explain why the previous `~_set` / `~_set.RunesOnlyPart` formula was unsound (a rune in the rune-only part can still be the first rune of a multi-rune cluster the set doesn't list).

## Verify the Fix

Re-run the same test:

```
dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj --filter "FullyQualifiedName~NoneOf_in_Or_matches_multi_rune_grapheme"
```

Expected: passes. Run the full suite (`dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj`) to confirm no other test broke; the suite is 1868/1870 (2 skipped) before and after.
