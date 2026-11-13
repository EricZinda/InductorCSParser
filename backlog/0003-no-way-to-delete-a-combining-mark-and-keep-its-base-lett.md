# No way to delete a combining mark and keep its base letter in a grammar

A real app (the GuitarTabs song list search) wants to compare text without caring about accents, punctuation or symbols, so that "beyonce" finds "Beyoncé" and "acdc" finds "AC/DC". The natural way to write that with InductorParser is a grammar: convert the input to FormKD so "é" becomes "e" plus U+0301, `Delete()` everything in a `TokenSet` of marks, punctuation and symbols, keep everything else, and read the result back with `ParseResult.ToString()`. That gets punctuation and symbols right but can't remove the accent, and there doesn't seem to be a way to write it that does.

## What happens

Here's the grammar, run against InductorParser 1.0.1067 on net8.0. The input is normalized up front because `Compile(FormKD)` rejects the category sets (see below).

```csharp
TokenSet dropped =
    TokenSet.Category(UnicodeCategory.NonSpacingMark)
    | TokenSet.Category(UnicodeCategory.OtherPunctuation)
    | TokenSet.Category(UnicodeCategory.DashPunctuation)
    | TokenSet.Category(UnicodeCategory.OtherSymbol);

// Delete whatever is in the dropped set, keep every other token
Rule plain = And(ZeroOrMore(Or(OneOf(dropped).Delete(), AnyToken())), Eof()).Compile(null);

// Same, but looking inside each token so its marks can be deleted
Rule withinToken = And(
    ZeroOrMore(Or(OneOf(dropped).Delete(),
                  WithinToken(And(NoneOf(dropped), ZeroOrMore(OneOf(dropped).Delete()))),
                  AnyToken())),
    Eof()).Compile(null);

string normalized = NormalizationHelpers.Normalize(input, NormalizationForm.FormKD);
string text = plain.Parse(normalized).ToString();
```

The output, with anything outside ASCII shown as its code point:

```
AC/DC        plain: ACDC                         withinToken: ACDC
Don't        plain: Dont                         withinToken: Dont
Beyoncé      plain: Beyonce<U+0301>              withinToken: Beyonce<U+0301>
Beyoncé     plain: Beyonce<U+0301>              withinToken: Beyonce<U+0301>
```

(The last row is the input already typed decomposed, as "e" plus U+0301.)

Punctuation goes away as expected. The accent stays in both versions.

## Why

After FormKD, "e" + U+0301 is one grapheme cluster, so it's one token. `OneOf(dropped)` tests the whole token, which isn't in the set, so `AnyToken()` keeps it, accent and all. `Delete()` can only drop or keep a whole token.

`WithinToken` looks like the way in, since it walks the runes inside a token. But its docs say "One leaf Symbol is emitted per successful match, representing the whole token. Inner-rule symbols are discarded," so a `Delete()` inside it has no effect on the output. The leaf is still "e" + U+0301.

That leaves no grammar-level way to say "keep this rune of the token and drop that one." The app ended up writing a loop instead: normalize with `NormalizationHelpers.Normalize(text, FormKD)`, then walk the code points and keep the ones not in the `TokenSet` (`ContainsRune`). That's the same recipe as the "Combining marks" section of docs/Primer4.md. It works, but under netstandard2.1 (Unity) there's no `string.EnumerateRunes`, so the app has to step through surrogate pairs by hand, and the grammar version would have been shorter and clearer.

## A related gotcha found along the way

`Compile(NormalizationForm.FormKD)` on a rule using the category sets above throws, listing hundreds of members like U+2026 HORIZONTAL ELLIPSIS that convert to several graphemes. The message is clear and points at `WithCompatibilityEquivalents`, so this isn't a bug, but it means the "compile with FormKD and let the parser normalize" route isn't available for a rule built from whole categories. Normalizing the input yourself and compiling with `Compile(null)` works.

Also, with this grammar `ParseResult.Tree` is null (the root is flattened, so there are several top-level symbols) and `ParseResult.ToString()` is the call that gives the text back. That's documented, but it was the first thing tried and it threw a NullReferenceException in the trial program.

## What's needed

A way for a grammar to drop some runes of a multi-rune token and keep the others, so "strip accents, punctuation and symbols" can be one short rule whose `ToString()` is the cleaned text. This item doesn't pick how. It needs a design that fits the token model (one token is one grapheme cluster) and the existing `WithinToken` and `Delete()` behavior.

## Verify

Whatever the design, this should pass:

- "Beyoncé" (precomposed) and "Beyonce" + U+0301 both come out as "Beyonce".
- "AC/DC" comes out as "ACDC" and "Don't" as "Dont".
- It works on netstandard2.1 (Unity), not just net8.0.
