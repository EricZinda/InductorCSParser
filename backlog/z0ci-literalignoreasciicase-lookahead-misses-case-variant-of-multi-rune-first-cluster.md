# LiteralIgnoreAsciiCase lookahead skips opposite-case input when first grapheme is multi-rune

`Rules.LiteralIgnoreAsciiCase` is supposed to accept ASCII letters in either case at parse time: `LiteralIgnoreAsciiCase("éclair").Parse("Éclair")` should match because the ASCII-letter half ('e' vs 'E') case-folds under the rule's `AsciiCaseEquals` runtime compare. That holds when the precomposed form survives compile (FormC / FormKC, where 'é' stays as one rune). It breaks under FormD / FormKD, where `Compile` decomposes `_expected` from `"éclair"` to `"éclair"`, because the rule's `FirstConsumedTokens` is then a `TokenSet` containing the cluster `"é"` as a multi-rune entry with no rune-level information about the ASCII-letter first rune.

`Rule.CannotMatchLookahead` (the shortcut consulted by `OrRule` and `BetweenInclusiveRule` before dispatching into a child) checks `FirstConsumedTokens.Contains(peekFirstRune)` first, then `FirstConsumedTokens.ContainsToken(peekToken)`. For a peek cluster `"É"` (FormD-decomposed `"Éclair"` input), `peekFirstRune` is 'E' (0x45) but `FirstConsumedTokens` only carries the multi-rune entry `"é"`. Neither the rune check (no single-rune entries in the set) nor the cluster check (`"É"` is bit-exact-not-equal to `"é"`) sees a match, so the shortcut declares "cannot match this peek" and skips the rule.

User-visible consequence: `Or(LiteralIgnoreAsciiCase("éclair"), fallback).Compile(NormalizationForm.FormD).Parse("Éclair")` and `OneOrMore(LiteralIgnoreAsciiCase("éclair")).Compile(NormalizationForm.FormD).Parse("Éclair")` both fail, even though running the case-insensitive branch would have succeeded. Any grammar that wraps a case-insensitive literal whose first grapheme is a multi-rune cluster starting with an ASCII letter, under a compile form that produces decomposed clusters, is silently rejected by the lookahead before the runtime compare gets to fold the case.

The relevant code is `LiteralIgnoreAsciiCaseRule.ComputeRuleStart` in [src/InductorParser/LiteralIgnoreAsciiCaseRule.cs](../src/InductorParser/LiteralIgnoreAsciiCaseRule.cs). The original branch only handled `firstElement.Length == 1 && IsAsciiLetter(firstElement[0])`, and the fallthrough went to `FirstTokenMustBeFirstGraphemeOf(_expected)` which carries the cluster in its original case only.

## Verify the Bug (Write Test First)

Two new tests added to [src/InductorParser.Tests/Rules/LiteralIgnoreAsciiCaseRuleTests.cs](../src/InductorParser.Tests/Rules/LiteralIgnoreAsciiCaseRuleTests.cs), one per shortcut site:

```csharp
[Test]
public void Or_admits_case_variant_when_first_grapheme_is_multi_rune_under_FormD()
{
    var literalBranch = LiteralIgnoreAsciiCase("éclair").As("literalBranch");
    var fallback = AnyToken().As("fallback");
    var rule = Or(literalBranch, fallback);

    rule.Compile(NormalizationForm.FormD);
    var result = rule.Parse("Éclair");

    Assert.That(result.Success, Is.True, result.ErrorMessage);
    Assert.That(result.Find(literalBranch), Is.Not.Null,
        "Or should have committed to the LiteralIgnoreAsciiCase branch " +
        "instead of skipping it via the lookahead shortcut.");
}

[Test]
public void OneOrMore_admits_case_variant_when_first_grapheme_is_multi_rune_under_FormD()
{
    var rule = OneOrMore(LiteralIgnoreAsciiCase("éclair"));

    rule.Compile(NormalizationForm.FormD);
    var result = rule.Parse("Éclair");

    Assert.That(result.Success, Is.True, result.ErrorMessage);
}
```

Run:

```
dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj -c Debug --no-build --filter FullyQualifiedName~LiteralIgnoreAsciiCaseRuleTests
```

Before the fix both tests fail. The Or test fails at offset 1 ('c'): the shortcut skips the case-insensitive branch, the `AnyToken` fallback consumes the first cluster, then trailing-input from the top-level `Parse` rejects the rest. The OneOrMore test fails at offset 0 ('É'): `BetweenInclusiveRule`'s shortcut path skips `Inner`, sees `AtLeast >= 1`, and records a "count=0" failure.

## Fix

In `LiteralIgnoreAsciiCaseRule.ComputeRuleStart`, extend the ASCII-letter branch to cover multi-rune first graphemes whose first rune is an ASCII letter. Both case variants of the first rune go into the set as single-rune entries (so the `Contains(peekFirstRune)` check admits either case); the original cluster also goes in as a multi-rune entry (the strict `ContainsToken` path still works for the original-case cluster):

```csharp
string firstElement = System.Globalization.StringInfo.GetNextTextElement(_expected, 0);
if (firstElement.Length >= 1 && IsAsciiLetter(firstElement[0]))
{
    int lower = firstElement[0] | 0x20;
    int upper = lower & ~0x20;
    TokenSet caseSet = TokenSet.Single(lower) | TokenSet.Single(upper);
    if (firstElement.Length == 1)
        return RuleStartRequirements.FirstTokenMustBeInSet(caseSet);
    return RuleStartRequirements.FirstTokenMustBeInSet(
        caseSet | TokenSet.Graphemes(firstElement));
}
```

The result is a sound superset of the actual first-consumed tokens. Under `MustBeIn` polarity a superset is the safe direction (rule is attempted in slightly more cases than strictly necessary; never wrongly skipped). See `RuleStartRequirements` line "A superset of actual first-consumed tokens is safe under MustBeIn".

## Verify the Fix

Re-run the same command. Both new tests pass. The existing 481 `LiteralIgnoreAsciiCaseRuleTests` (compile-form normalization matrix, FlattenType matrix, SourceRange matrix) still pass.

Full-suite run (`dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj`) shows 5614 passed; the only 2 failures are pre-existing `RecipesExamples` failures around `.As(string)` reuse that the previous bug hunt (`i1nt`) already documented as out-of-scope.
