# TokenSet.ContainsToken NREs on default(TokenSet) with multi-rune input

`TokenSet.ContainsToken(ReadOnlySpan<char>)` in src/InductorParser/TokenSet.cs at line 172 reads `_multiRuneGraphemes.Length` directly without a null guard. The TokenSet 2-arg constructor coalesces a null `multiRuneGraphemes` to `Array.Empty<string>()`, so every TokenSet built by the public factories (`Single`, `Range`, `Runes`, `Category`, the `|` / `&` / `~` operators, `NormalizedFor`, etc.) has a non-null array and the read is safe. But `default(TokenSet)` skips the constructor entirely, leaving the field null, and `default(TokenSet)` is the value behind the public `TokenSet.Empty` constant. A user-facing rule like `OneOf(TokenSet.Empty)` or `NoneOf(TokenSet.Empty)` (a programmatically-built stop list that nothing got added to, an intersection that came out empty, an explicit Empty placeholder) reads `_set.ContainsToken(token.Chars)` at parse time. When the next token is a multi-rune grapheme that isn't a surrogate pair (decomposed `é` as `"é"`, a ZWJ family emoji, a regional-indicator flag, CRLF), `TrySingleRune` returns false and execution falls into the multi-rune branch, where `_multiRuneGraphemes.Length` throws NullReferenceException. The user sees an unhandled exception bubble out of `Parse`, not a parse failure, and there's no obvious hint that the empty TokenSet is the cause.

The bug surface is every caller of `ContainsToken` whose `_set` (or whose `FirstConsumedTokens`) can be `default(TokenSet)`. That's `OneOfRule.TryParseRule` (line 81), `NoneOfRule.TryParseRule` (line 51), `ScanUntilRule` (line 287), the lexer's scanner-skip fast path (`Lexer.FastPaths.cs` line 216), and `Rule.CannotMatchLookahead` (line 171, 177). Any of those reached with a default TokenSet and a multi-rune token NREs.

## Verify the Bug (Write Test First)

Add this test to `src/InductorParser.Tests/Rules/NoneOfRuleTests.cs` next to the existing multi-rune Token coverage. With the buggy line 172, the multi-rune branch dereferences a null `_multiRuneGraphemes` and throws.

```csharp
[Test]
public void NoneOf_with_TokenSet_Empty_admits_a_multi_rune_grapheme_without_NRE()
{
    // Programmatically-constructed sets sometimes wind up empty (a
    // conditional stop list that nothing got added to, an
    // intersection that came out empty, etc.). NoneOf(TokenSet.Empty)
    // is then the "match any token" rule. TokenSet.Empty is the
    // public name for default(TokenSet), whose internal
    // _multiRuneGraphemes field is null because nothing ever ran
    // the constructor that coalesces null to Array.Empty<string>().
    // ContainsToken's multi-rune branch reads _multiRuneGraphemes
    // .Length directly, so a multi-rune grapheme arriving as the
    // next token throws NullReferenceException instead of returning
    // false. Compile(null) keeps the decomposed grapheme out of NFC
    // composition so the lexer hands the rule a true two-char token.
    var rule = NoneOf(TokenSet.Empty);
    rule.Compile(null);

    var result = rule.Parse(LatinEAcuteGrapheme);

    Assert.That(result.Success, Is.True, result.ErrorMessage);
    Assert.That(result.Tree!.ToString(), Is.EqualTo(LatinEAcuteGrapheme));
}
```

A parallel OneOf test pins the same shape on the other consumer of `_set.ContainsToken`. Add to `src/InductorParser.Tests/Rules/OneOfRuleTests.cs`:

```csharp
[Test]
public void OneOf_with_TokenSet_Empty_rejects_a_multi_rune_grapheme_without_NRE()
{
    // OneOf(TokenSet.Empty) is the "match nothing" rule, which is
    // what a programmatically-built set lands on when nothing got
    // added. The cluster doesn't match (the set is empty), but the
    // membership probe must return false instead of NRE'ing on
    // _multiRuneGraphemes.Length when the underlying TokenSet is
    // default(TokenSet) (the public TokenSet.Empty alias).
    var rule = OneOf(TokenSet.Empty);
    rule.Compile(null);

    var result = rule.Parse(LatinEAcuteGrapheme);

    Assert.That(result.Success, Is.False);
}
```

Run:

```
dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj --filter "FullyQualifiedName~with_TokenSet_Empty"
```

Expected pre-fix: both fail with `System.NullReferenceException : Object reference not set to an instance of an object.` originating at `TokenSet.ContainsToken` line 172.

## Fix

In `src/InductorParser/TokenSet.cs`, swap the unguarded `_multiRuneGraphemes.Length` read in `ContainsToken` for the same cache-to-local-and-null-check shape that `Contains(int codepoint)` already uses for `_ranges`. That makes the method tolerate `default(TokenSet)` the same way every other entry point on the struct does.

```csharp
internal bool ContainsToken(ReadOnlySpan<char> grapheme)
{
    if (grapheme.Length == 0) return false;
    if (grapheme.Length == 1 && char.IsSurrogate(grapheme[0]))
        return Contains((int)grapheme[0]);
    if (TrySingleRune(grapheme, out int runeValue))
        return Contains(runeValue);
    // Cache to local + null-check, matching the Contains(int) shape.
    // default(TokenSet) (= TokenSet.Empty) leaves _multiRuneGraphemes
    // null because the field-coalescing constructor never ran on it.
    var multi = _multiRuneGraphemes;
    return multi != null && multi.Length > 0 && BinarySearchMultiRune(grapheme) >= 0;
}
```

The fix is in TokenSet itself, so every caller of `ContainsToken` benefits without having to change. The single-test regression matrix is already enough on the membership side; the OneOf parallel keeps both consumer rules in the test bench so a future refactor that re-introduces the NRE on either path surfaces.

## Verify the Fix

Re-run:

```
dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj --filter "FullyQualifiedName~with_TokenSet_Empty"
```

Expected: both pass. Run the full suite (`dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj`) to confirm nothing else regressed: 4645 passed / 2 pre-existing skips at the baseline, 4647 passed / 2 skips with both new tests and the fix in place.
