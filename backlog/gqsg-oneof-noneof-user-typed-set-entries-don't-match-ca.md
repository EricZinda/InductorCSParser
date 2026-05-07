# OneOf / NoneOf: user-typed set entries don't match canonically-equivalent input + lone surrogates in rune intervals don't match either


Two related membership-test bugs in `OneOfRule` and `NoneOfRule`.

## Bug 1: canonically-equivalent input doesn't match user-typed entries

When a grammar contains `OneOf("é")` (user-typed precomposed) or `OneOf("é")` (user-typed decomposed), the rule fails to match input in the OTHER canonical form because the lexer and the set use different representations:

  - `NoneOf("é").Compile(FormD).Parse("é")` returns `Success = true` — the WRONG answer. The decomposed multi-rune token isn't in the set, the membership probe returns false, NoneOf inverts that into a false-positive match.
  - `OneOf("é").Compile().Parse("é")` (default FormC, decomposed in source) similarly fails: the user typed `"é"` so the set has a multi-rune entry "é"; the lexer normalizes input to U+00E9 (single-rune); the multi-rune array doesn't have that single rune.
  - `OneOf("é").Compile(System.Text.NormalizationForm.FormD).Parse("é")` returns `Success = false`. The set has U+00E9 stored as a single-rune entry; the lexer normalizes input "é" to "é" (multi-rune cluster); the rune-only set can't match a multi-rune token.
  - `OneOf(TokenSet.Letters).Compile().Parse("དྷ")` (Tibetan composite letter U+0F52, NFC-decomposable to "U+0F51U+0FB7") fails because Letters has U+0F52 as a single-rune interval but doesn't have the decomposed multi-rune cluster as a multi-rune entry. Most BCL category sets share this blind spot for any precomposed character whose NFD form is a multi-rune cluster.

Singleton decompositions show the same shape: U+2126 OHM SIGN canonically decomposes to U+03A9 GREEK CAPITAL LETTER OMEGA (different single rune). Without a fix `OneOf("Ω").Compile().Parse("Ω")` silently fails.

Root cause: a `TokenSet` stores rune intervals (single Unicode scalars) and multi-rune entries (specific cluster strings). When the lexer normalizes input to a different canonical form than the user typed, the membership test sees a token shape the set doesn't have, even though the entry "is" the same character semantically.

## Bug 2: lone surrogates in user-typed rune intervals don't match

`TokenSet.Range(0, 0x10FFFF)` is documented to include the surrogate gap as a legal interior of the interval. But `OneOf(TokenSet.Range(0, 0x10FFFF)).Compile(null).Parse("\uD800")` returns `Success = false` even though the surrogate IS in the set's `_ranges` and IS in the input. Same shape on `NoneOf`, where the bug inverts to a false-positive match.

Root cause in [OneOfRule.TokenInSet](src/InductorParser/OneOfRule.cs):

```csharp
int runeValue = token.RuneValue;
if (runeValue >= 0) return _set.Contains(runeValue);   // skipped: RuneValue is -1 for surrogate tokens
if (!_set.HasMultiRuneGraphemes) return false;          // returns here without consulting _ranges
return _set.ContainsToken(token.Chars);
```

`Token.RuneValue` collapses three different cases into `-1`: EOF, multi-rune cluster, AND stray surrogate. The membership test doesn't disambiguate, so it never queries `_ranges` for a surrogate token even when the user explicitly put surrogates there. `NoneOfRule` has the same shape inline in `TryParseRule`.

## Verify Both Bugs (Write Tests First)

Add to [src/InductorParser.Tests/Core/UnexpectedUnicodeTests.cs](src/InductorParser.Tests/Core/UnexpectedUnicodeTests.cs):

```csharp
// --- canonical-equivalence (bug 1) ---

[Test]
public void OneOf_with_precomposed_entry_matches_decomposed_input_under_FormD()
{
    var rule = OneOf("é");  // U+00E9
    rule.Compile(System.Text.NormalizationForm.FormD);

    Assert.That(rule.Parse("é").Success, Is.True);          // U+00E9
    Assert.That(rule.Parse("é").Success, Is.True);    // 'e' + combining acute
}

[Test]
public void OneOf_with_decomposed_entry_matches_precomposed_input_under_FormC()
{
    var rule = OneOf("é");  // 'e' + combining acute
    rule.Compile();  // default FormC

    Assert.That(rule.Parse("é").Success, Is.True);
    Assert.That(rule.Parse("é").Success, Is.True);
}

[Test]
public void NoneOf_with_precomposed_entry_rejects_decomposed_input_under_FormD()
{
    var rule = AllOf(NoneOf("é"), Eof());
    rule.Compile(System.Text.NormalizationForm.FormD);

    Assert.That(rule.Parse("é").Success, Is.False);
    Assert.That(rule.Parse("é").Success, Is.False);
    Assert.That(rule.Parse("a").Success, Is.True);
}

[Test]
public void OneOf_with_singleton_decomposing_rune_matches_normalized_form()
{
    var rule = OneOf("Ω");  // U+2126 OHM SIGN
    rule.Compile();  // default FormC; NFC of U+2126 is U+03A9

    Assert.That(rule.Parse("Ω").Success, Is.True);  // U+2126
    Assert.That(rule.Parse("Ω").Success, Is.True);  // U+03A9
}

// --- lone surrogate (bug 2) ---

[Test]
public void OneOf_with_range_including_surrogates_matches_lone_high_surrogate()
{
    var rule = OneOf(TokenSet.Range(0, 0x10FFFF));
    rule.Compile(null);

    Assert.That(rule.Parse("\uD800").Success, Is.True);
    Assert.That(rule.Parse("\uDFFF").Success, Is.True);
    Assert.That(rule.Parse("a").Success, Is.True);
}

[Test]
public void NoneOf_with_range_including_surrogates_rejects_lone_surrogate()
{
    var rule = AllOf(NoneOf(TokenSet.Range(0, 0x10FFFF)), Eof());
    rule.Compile(null);

    Assert.That(rule.Parse("\uD800").Success, Is.False);
    Assert.That(rule.Parse("\uDFFF").Success, Is.False);
}

[Test]
public void OneOf_with_range_excluding_surrogates_still_rejects_lone_surrogate()
{
    var rule = OneOf(TokenSet.Range('a', 'z'));
    rule.Compile(null);

    Assert.That(rule.Parse("\uD800").Success, Is.False);
    Assert.That(rule.Parse("a").Success, Is.True);
}
```

Before the fix, the canonical-equivalence tests fail (rules report parse failures or false-positive successes), and the surrogate-range tests show the same shape (Range that includes surrogates fails to match them; NoneOf inverts that into a false positive).

## Fix

**Bug 1 (canonical equivalence): construction-time augmentation in `Runes(string)` and `Single(int)`.** When the user types a literal character into a TokenSet via `Runes("é")` or `Single(0xE9)`, the factory walks the user's input grapheme-by-grapheme and adds each grapheme's canonical-equivalent NFD form alongside the original. Single-rune entries with multi-rune NFD forms get a multi-rune entry; entries with singleton-decomposable NFD forms get the other rune as a separate interval; multi-rune entries with single-rune NFC forms get the composed rune as an interval. The set ends up form-agnostic for canonical equivalence and matches whichever form the lexer produces under any non-null Compile.

**Scope limited to user-typed entries (literals).** Category-derived sets like `TokenSet.Letters` are NOT augmented. The BCL's General_Category data already produces the precomposed (NFC) forms for most letters, so under FormC the typical category-set use case works. Augmenting categories would require walking 130K+ runes and probing the Unicode normalization tables on first access (~1-3 seconds startup), and would also surface latent bugs in rare characters (Tibetan U+0F52, Hebrew with niqqud, etc. — characters whose NFC form decomposes to multi-rune clusters) that no existing grammar trips. Filing those as a separate optimization backlog item if anyone needs them; for now, augmentation is restricted to the cases the user explicitly typed in source code.

Construction-time augmentation costs:

  - `Runes("é")` walks one grapheme, calls `IsNormalized(FormD)` (BCL fast path), one allocation for the NFD form. Microseconds.
  - `Runes("hello")`: 5 graphemes, all NFD-stable, no allocations. Microseconds.
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
