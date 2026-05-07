- `Single(0xE9)`: one IsNormalized call, one Normalize call, one allocation. Microseconds.

`Compile(null)` callers pay the same micro-costs (the augmentation is harmless because it just adds entries the input never produces). No probe table, no global walk, no startup latency.

**Bug 2 (lone surrogates): disambiguate the three RuneValue == -1 cases in `OneOfRule` and `NoneOfRule`'s membership checks.**

[OneOfRule.cs](src/InductorParser/OneOfRule.cs):

```csharp
private bool TokenInSet(Lexing.Token token)
{
    int runeValue = token.RuneValue;
    if (runeValue >= 0) return _set.Contains(runeValue);
    // Stray surrogate: query rune intervals using the surrogate's UTF-16
    // code unit value so a user-typed Range(0, 0x10FFFF) (or similar
    // surrogate-bearing interval) can match WTF-8 / unpaired-surrogate
    // input under Compile(null).
    if (token.Length == 1 && char.IsSurrogate(token.Chars[0]))
        return _set.Contains((int)token.Chars[0]);
    if (!_set.HasMultiRuneGraphemes) return false;
    return _set.ContainsToken(token.Chars);
}
```

[NoneOfRule.cs](src/InductorParser/NoneOfRule.cs)'s inline membership check gets the same three-branch shape.

## Verify the Fix

Run the seven tests above; they should all pass. Also run the full suite to confirm no regression:

```
dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj --nologo
dotnet test ExperimentalSrc/InductorParser.Tests/InductorParser.StateMachine.Tests.csproj --nologo
```

Expected: 1848 pass + 2 skipped in src; 94 pass in StateMachine. The existing `OneOf_universe_rejects_lone_surrogate_under_null_normalization` test continues to pass — Universe is built via `~default(TokenSet)` which splits around the surrogate gap, so Universe doesn't include surrogates by construction. Only `Range(...)` intervals that span across the gap include them, and the surrogate-aware membership check matches them correctly when they do.
