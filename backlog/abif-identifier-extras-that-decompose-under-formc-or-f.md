# Identifier extras that decompose under FormC or FormD silently never match

Found by a first-principles review of IdentifierRule (July 8, 2026).
The existing test suite doesn't catch it.

## The behavior

Opt a character into an identifier via extraStartRunes or
extraBodyRunes, compile under a canonical form, and if the character
decomposes under that form it silently never matches. No compile
error, no parse-time hint. The same grammar under FormKC/FormKD throws
at Compile with a fix-it message, and under Compile(null) it just
matches:

```csharp
var id = Identifier(extraBodyRunes: TokenSet.Single(0x2260)); // ≠

id.Compile(null);
id.Parse("a≠");                        // matches

id.Compile(NormalizationForm.FormD);
id.Parse("a≠");   // FAILS: Unexpected '≠' at line 1, column 2

id.Compile(NormalizationForm.FormKD);  // throws at Compile:
// Identifier can't compile under FormKD: the extraBodyRunes entry
// "≠" becomes "≠", and "=" in there isn't allowed in the body of an
// identifier. Every piece it becomes has to be valid in the body.
// Add "=" to extraBodyRunes, or remove "≠" from extraBodyRunes.
```

The default form is affected too, just with rarer characters: any
composition exclusion whose pieces aren't XID. U+2ADC FORKING
normalizes to U+2ADD U+0338 even under FormC, so
`Identifier(extraBodyRunes: TokenSet.Single(0x2ADC))` compiles clean
under the default form and then never matches `"a⫝̸"`.

## Why it happens

IdentifierRule.ValidateNormalization returns immediately for anything
but FormKC/FormKD, so the extras never get the expand-or-reject
treatment under canonical forms. The embedded start/body OneOfs then
project their sets at parse setup via TokenSet.NormalizedFor: a
single-rune entry whose normalized form is one grapheme of several
runes (≠ becomes = plus U+0338) is converted into a multi-rune
grapheme member, and no offender is reported for that (only
multi-grapheme conversions report). Identifier matches inside
WithinToken's one-rune-per-token sub-lexer, where every token is
exactly one rune, so a multi-rune grapheme member can never match
anything. It's a dead entry, precisely the thing the Rules.Identifier
factory comment says gets rejected up front so it can't happen
silently.

## The fix, probably

Give FormC/FormD the same per-form treatment FormKC/FormKD already
gets in IdentifierRule.ValidateNormalization: expand what expands into
legal pieces, and report a Compile offender with the add-these-runes
guidance where a piece isn't legal in that position. The machinery all
exists, it's just skipped for canonical forms.

Small message polish while in there: the FormKD error prints
`"≠" becomes "≠"` because the decomposed cluster renders identically
to the composed character. Printing the pieces as code points
(U+003D U+0338) would make it read as intended.
