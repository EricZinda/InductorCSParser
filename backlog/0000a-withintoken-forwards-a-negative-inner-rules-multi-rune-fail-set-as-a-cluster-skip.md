# WithinToken forwards a negative inner rule's multi-rune fail-set as a cluster-level skip

`WithinTokenRule.ComputeRuleStart` (`src/InductorParser/WithinTokenRule.cs`) forwards the inner rule's `FirstConsumedTokens` and `Polarity` straight out to the WithinToken, with only the `Advance` overwritten to `Always`. The inner rule runs on a one-rune-per-token sub-lexer, so the set it publishes describes *runes*. The enclosing `Or` / `BetweenInclusive` lookahead shortcut peeks a whole *grapheme cluster*. Those are two different units, and the forwarding doesn't reconcile them.

For a `MustBeIn` inner rule the mismatch is harmless: `Rule.CannotMatchLookahead`'s MustBeIn path tests the peek cluster's *first rune* against the set (`FirstConsumedTokens.Contains(peekFirstRune)`), which is exactly the rune the inner rule reads first. For a `MustNotBeIn` inner rule (`NoneOf`, or any negative composite whose `MatchesAllOf` / `MatchesAnyOf` result stays MustNotBeIn) it is not harmless: `CannotMatchLookahead`'s MustNotBeIn path tests the *whole* peek cluster against the fail-set (`FirstConsumedTokens.ContainsToken(peekToken)`) and skips the rule when the cluster IS a member. When the inner rule's fail-set contains a multi-rune grapheme (a CRLF cluster, a ZWJ emoji, a skin-toned face, a decomposed accented letter), and the peek cluster is exactly that grapheme, the shortcut skips the WithinToken — but inside WithinToken the inner rule walks that cluster one rune at a time and never sees it as a unit, so it can match every rune (none of which is in the fail-set on its own).

User-visible consequence: the lookahead shortcut, which is a pure optimization that must never change a parse result, changes one. `Or(WithinToken(OneOrMore(NoneOf(TokenSet.Graphemes("\r\n")))), ...)` fails to match the input `"\r\n"` even though the `WithinToken` branch matches it when parsed on its own. The `Or` peeks the CRLF cluster, sees it is a member of the inner `NoneOf`'s fail-set `{ "\r\n" }`, wrongly concludes the branch can't match, skips it, and the parse fails on input the grammar accepts. The same divergence shows up under `BetweenInclusive` (its per-iteration shortcut consults `Inner.CannotMatchLookahead` the same way) and on both the recursive and state-machine engines, since both read the compiled `Rule.FirstConsumedTokens` / `Rule.Polarity` fields this method populates.

## Verify the Bug (Write Test First)

The test goes in the existing `src/InductorParser.Tests/Rules/WithinTokenRuleTests.cs`, next to the other `Or_WithinToken_*` shortcut tests. It already imports everything needed.

```csharp
[Test]
public void Or_WithinToken_does_not_skip_when_inner_negative_rule_walks_a_multi_rune_cluster()
{
    // A TokenSet whose only member is the CRLF grapheme cluster. \r
    // and \n are NOT members on their own — only the two-rune "\r\n"
    // cluster is.
    var crlfCluster = TokenSet.Graphemes("\r\n");

    // WithinToken runs OneOrMore(NoneOf(...)) against the runes
    // inside one outer token. On the "\r\n" cluster the inner rule
    // sees \r and \n one rune at a time; neither is a member of
    // crlfCluster, so the inner NoneOf accepts both and WithinToken
    // consumes the whole cluster. Standalone, it matches:
    var standalone = WithinToken(OneOrMore(NoneOf(crlfCluster)));
    Assert.That(standalone.Parse("\r\n").Success, Is.True,
        "WithinToken(OneOrMore(NoneOf(crlfCluster))) should consume the CRLF cluster rune by rune");

    // The same WithinToken as an Or branch must still match. The
    // Or's lookahead shortcut peeks the whole "\r\n" cluster and asks
    // the WithinToken's published first-token requirement whether
    // the branch can match. WithinToken forwards the inner NoneOf's
    // MustNotBeIn fail-set { "\r\n" } unchanged, so the shortcut sees
    // the peek cluster IS in the fail-set and skips the branch — even
    // though the branch would have matched.
    var rule = Or(WithinToken(OneOrMore(NoneOf(crlfCluster))), Literal("ZZ"));
    var result = rule.Parse("\r\n");

    Assert.That(result.Success, Is.True, result.ErrorMessage);
}
```

Run with:

```
dotnet test --filter "FullyQualifiedName~WithinTokenRuleTests.Or_WithinToken_does_not_skip_when_inner_negative_rule_walks_a_multi_rune_cluster"
```

Pre-fix the first assertion (`standalone`) passes — the WithinToken matches the CRLF cluster on its own — and the second fails (`result.Success` is `False`, expected `True`): the `Or` shortcut-skips the `WithinToken` branch on the CRLF peek, falls through to `Literal("ZZ")`, and reports a parse failure at offset 0.

## Fix

In `src/InductorParser/WithinTokenRule.cs`, change `ComputeRuleStart` so a `MustNotBeIn` inner rule's fail-set is projected to its rune-only part before being published:

```csharp
internal override RuleStartRequirements ComputeRuleStart()
{
    var innerStart = RuleStartRequirements.PassesThroughTo(_innerRule)
        .WithAdvance(Advance.Always);
    if (innerStart.Polarity == Polarity.MustNotBeIn
        && innerStart.FirstConsumedTokens.HasMultiRuneGraphemes)
    {
        return new RuleStartRequirements(
            innerStart.FirstConsumedTokens.RunesOnlyPart,
            Advance.Always,
            Polarity.MustNotBeIn);
    }
    return innerStart;
}
```

`TokenSet.RunesOnlyPart` drops the multi-rune grapheme entries and keeps the single-rune intervals. The projected fail-set only triggers a skip on a single-rune peek cluster (a multi-rune cluster can never `ContainsToken`-match a rune-only set), and for a single-rune cluster the cluster's one rune IS the rune the inner rule checks first — so the skip stays sound. A subset of the actual fail-set is always sound under `MustNotBeIn` (it just skips fewer peeks than possible); the multi-rune entries are the unsound part, and dropping them is exactly the precision loss the shortcut can afford. The `MustBeIn` path is left untouched: `CannotMatchLookahead`'s first-rune check already bridges the rune-vs-cluster coordinate gap there.

While here, the method now reads the inner rule's published triple via `RuleStartRequirements.PassesThroughTo(_innerRule)` instead of re-invoking `_innerRule.ComputeRuleStart()`. Compile's `ComputeRuleStartAll` walk is depth-first post-order and the inner rule is this rule's only child, so its compiled `FirstConsumedTokens` / `Advance` / `Polarity` fields are already populated when this runs — the same guarantee `BetweenInclusiveRule.ComputeRuleStart` relies on. The old direct `ComputeRuleStart()` call also bypassed the walk's cycle-termination handling (a rule left at its pessimistic default because it sits on the recursion stack would get silently recomputed). The stale comment claiming the inner fields "may not yet be populated" is dropped.

## Verify the Fix

Re-run the same command — both assertions pass. Then run the full file and suite:

```
dotnet test --filter "FullyQualifiedName~WithinTokenRuleTests"
```

All `WithinTokenRuleTests` pass, including the existing `Or_WithinToken_skips_when_peek_first_rune_is_outside_inner_set` / `Or_WithinToken_runs_when_peek_first_rune_is_in_inner_set` MustBeIn-shortcut tests (the fix doesn't touch that path). The full `InductorParser.Tests` suite stays green: 5694 passed, 2 skipped, 0 failed, and the same on the state-machine engine (`INDUCTOR_DEFAULT_ENGINE=statemachine`).
