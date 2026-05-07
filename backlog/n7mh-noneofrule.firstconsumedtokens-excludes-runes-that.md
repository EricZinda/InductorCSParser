- NoneOfRule.FirstConsumedTokens excludes runes that admitted multi-rune clusters could start with

`NoneOfRule.ComputeRuleStart` in [src/InductorParser/NoneOfRule.cs](../src/InductorParser/NoneOfRule.cs) returned `~_set.RunesOnlyPart` (with the rune-only-set complement when the set has no multi-rune entries). That set is the runes whose single-rune token would pass NoneOf, but it ignores that NoneOf also admits multi-rune clusters whose chars aren't in the set's multi-rune part. A multi-rune cluster like `"a" + combining acute` can start with any base rune, including runes that are in the rune-only part of the set. When NoneOf is wrapped in a rule that uses the `CannotMatchLookahead` shortcut (`BetweenInclusiveRule` and `FirstOfRule`), the shortcut peeks ONE rune off the input and skips the rule when that rune isn't in `Inner.FirstConsumedTokens`. With the old `~_set.RunesOnlyPart`, every rune in the rune-only part triggered the skip, even though NoneOf would have accepted a multi-rune cluster starting with that rune.

User-visible: `OneOrMore(NoneOf(TokenSet.Single('a'))).Compile(null).Parse("a" + CombiningAcuteText)` failed with `Parse failed at offset 0: unexpected 'á'.` The cluster IS admitted by NoneOf directly (the test `NoneOf_matches_multi_rune_Token` already proves this for the analogous "é" shape), but the BetweenInclusive shortcut never let NoneOf run. Same shape via `FirstOf(NoneOf(...), other)` where the other alternative doesn't match: FirstOf skips NoneOf and falls through, the parse fails.

## Verify the Bug (Write Test First)

Two regression tests in `src/InductorParser.Tests/Rules/NoneOfRuleTests.cs`:

```csharp
[Test]
public void OneOrMore_NoneOf_admits_multi_rune_cluster_starting_with_a_set_rune()
{
    // BetweenInclusiveRule peeks the next rune and consults
    // Inner.CannotMatchLookahead before iterating. The input here
    // is one grapheme cluster (a + combining acute under
    // Compile(null)). NoneOf admits it, because a multi-rune cluster
    // is rejected only when its full chars are in the set's
    // multi-rune part, and {'a'} has none. So the rune-set
    // lookahead can't soundly exclude 'a' for NoneOf, even though
    // 'a' alone would be rejected as a single-rune token.
    var rule = OneOrMore(NoneOf(TokenSet.Single('a')));
    rule.Compile(null);
    var result = rule.Parse("a" + CombiningAcuteText);

    Assert.That(result.Success, Is.True, result.ErrorMessage);
    Assert.That(result.Tree!.ToString(), Is.EqualTo("a" + CombiningAcuteText));
}

[Test]
public void FirstOf_NoneOf_admits_multi_rune_cluster_starting_with_a_set_rune()
{
    // FirstOfRule has the same CannotMatchLookahead shortcut as
    // BetweenInclusiveRule, so NoneOf has to admit any peeked first
    // rune in this context too. The literal alternative is
    // unreachable here: NoneOf has to be the one that matches.
    var rule = FirstOf(NoneOf(TokenSet.Single('a')), Literal("zzzZZZ"));
    rule.Compile(null);
    var result = rule.Parse("a" + CombiningAcuteText);

    Assert.That(result.Success, Is.True, result.ErrorMessage);
    Assert.That(result.Tree!.ToString(), Is.EqualTo("a" + CombiningAcuteText));
}
```

Run with:

    dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj --filter "FullyQualifiedName~NoneOf_admits_multi_rune_cluster"

Both tests fail before the fix with `Parse failed at offset 0: unexpected 'á'.`

## Fix

In [src/InductorParser/NoneOfRule.cs](../src/InductorParser/NoneOfRule.cs), replace the body of `ComputeRuleStart`:

```csharp
internal override RuleStartRequirements ComputeRuleStart()
{
    // The lookahead peek sees ONE rune. NoneOf, asked "could a token
    // starting with this rune match?", has to admit that yes for any
    // rune. A token here is one grapheme cluster, and a multi-rune
    // cluster like "X<combining mark>" or "X<ZWJ>Y" starts with X
    // for any base rune X. NoneOf admits any cluster whose chars
    // aren't in _set's multi-rune part, so for every rune R there's
    // some multi-rune cluster starting with R that NoneOf would
    // accept (regardless of whether R itself is in the rune-only
    // part of _set, because R-as-a-single-rune-token and "R..."
    // -as-a-multi-rune-cluster are different tokens with different
    // membership tests).
    //
    // The previous tighter `~_set.RunesOnlyPart` answer ignored that
    // second case. OneOrMore(NoneOf({'a'})) parsing "á" (one
    // grapheme under Compile(null), admitted by NoneOf because the
    // cluster isn't a single-rune 'a') wrongly failed: the
    // BetweenInclusive shortcut peeked 'a', saw it wasn't in
    // ~{'a'}, and concluded NoneOf couldn't match. Universe is the
    // soundest answer.
    return new RuleStartRequirements(TokenSet.Universe, Advance.Always);
}
```

The recursive engine's `CannotMatchLookahead` short-circuits to false on `FirstConsumedTokens.Equals(Universe)` paths (every rune is a member), and the state-machine lowerer in `ExperimentalSrc/InductorParser/StateMachine/Lowerer.cs` already gates its alternative-skip optimization on `child.FirstConsumedTokens.Equals(TokenSet.Universe)` returning `false`. Both engines correctly stop applying the skip for NoneOf with the new value.

The optimization loss is small in practice. NoneOf's first-rune lookahead skip would previously fire when the peek WAS in the rejection set (the "skip ahead, no match here" case for ASCII-text scanners like `OneOrMore(NoneOf("\""))` against `"foo"`). With the fix, those calls fall through to the regular `lexer.Read` + `_set.Contains` path, which fails per token instead of per peek. ASCII-only inputs without multi-rune clusters pay one extra `Read` per stopper encountered, which is dwarfed by the BetweenInclusive transaction overhead the loop already pays.

## Verify the Fix

Run the same two tests; both pass.

Also re-run the full test suite for `NoneOf` coverage and the state-machine compare fixtures:

    dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj --filter "FullyQualifiedName~NoneOf"
    dotnet test ExperimentalSrc/InductorParser.Tests/InductorParser.StateMachine.Tests.csproj
