# Unicode Gotchas

Some Unicode surprises cannot be fixed by the parser's lexer choice. Both `RuneLexer` and `GraphemeLexer` hit these identically, because they live outside the "what is a token?" question the lexers answer. The fix is always either caller-side preprocessing (clean the input before parsing) or grammar-design (pick the right `RuneSet`, add explicit tolerance rules).

This doc lists the common gotchas, why they bite, and the idiomatic workaround for each. If you are choosing between `RuneLexer` and `GraphemeLexer`, see [UnicodeInternalsArchitecture.md](UnicodeInternalsArchitecture.md); that is a different decision.

## Case Folding Beyond ASCII

The `LiteralIgnoreCase` helper does ASCII case-insensitive matching (A ↔ a) and is all most grammars need. Full Unicode case folding has script-specific surprises that neither lexer handles: German `ß` uppercases to `SS` (one character becomes two), Turkish has dotted-i and dotless-i as distinct letters, Greek final sigma (ς) folds to regular sigma only at word boundaries. The helper is labeled ASCII-only on purpose; extending it to full Unicode silently produces wrong results on Turkish, Greek, and German text.

**Fix.** Use the ASCII-only helper and accept that case-insensitive matching of non-ASCII text is not supported:

```csharp
// One character that matches either case (ASCII only)
static Rule AnyCase(char c) =>
    char.IsLetter(c)
        ? RuneIn(RuneSet.Single(char.ToLower(c)) | RuneSet.Single(char.ToUpper(c)))
        : Char(c);

// A literal string where each letter matches either case
static Rule LiteralIgnoreCase(string s) =>
    And(s.Select(AnyCase).ToArray());

public static readonly Rule SelectKeyword = LiteralIgnoreCase("select");
```

Do not try to extend this to full Unicode case folding. It will get subtly wrong for Turkish, Greek, and German.

## BOM At File Start

Byte Order Mark, U+FEFF. Windows tools and some editors prepend it to text files as an encoding hint, so the first character of the file is invisible but present. A grammar that expects the file to start with a specific keyword like `<?xml` or `function` sees the BOM as an unexpected character at position 0 and fails to match. Both lexers see it identically because the BOM is a single rune and a single grapheme.

**Fix.** The caller strips it before parsing:

```csharp
var cleaned = input.TrimStart('\uFEFF');
var result = grammar.Parse(cleaned);
```

## Zero-Width and Invisible Format Characters

U+200B (zero-width space), U+200C (zero-width non-joiner), U+200D (zero-width joiner), U+00AD (soft hyphen), and similar runes appear as characters in the input but render as nothing or render conditionally. A string like `"ap\u00ADple"` looks like `"apple"` in an editor but does not match `Literal("apple")` because the soft hyphen is a real character in the token stream.

`GraphemeLexer` handles ZWJ correctly inside emoji sequences (it groups them into one grapheme per UAX #29), but bare ZWJs and other format characters outside emoji contexts still come through as their own tokens under both lexers.

**Fix.** The caller strips them before parsing, or the grammar's character classes tolerate them explicitly. For stripping:

```csharp
static readonly HashSet<int> InvisibleFormat = new()
{
    0x200B,  // zero-width space
    0x200C,  // zero-width non-joiner
    0x200D,  // zero-width joiner — keep if you care about emoji ZWJ sequences!
    0x00AD,  // soft hyphen
    0xFEFF,  // BOM / zero-width no-break space
};

var cleaned = string.Concat(input.EnumerateRunes()
    .Where(r => !InvisibleFormat.Contains(r.Value)));

var result = grammar.Parse(cleaned);
```

If your grammar uses `GraphemeLexer` and processes emoji sequences, do not strip ZWJ (U+200D) indiscriminately; you will break 👨‍👩‍👧‍👦 and similar sequences.

## Homoglyph Confusables

Cyrillic `а` (U+0430) and Latin `a` (U+0061) render identically in most fonts but are different code points. A grammar using `RuneSet.Ascii.Letters` rejects Cyrillic `а` even though the user "sees" a Latin `a`. A grammar using `RuneSet.Letters` accepts both and does not distinguish them. Both lexers treat the code points identically because they really are different runes.

This is a grammar-design decision. For security-sensitive grammars (mixed-script identifier detection, phishing-resistance) it is a *feature*: refusing homoglyphs protects against visual-spoofing attacks. For forgiving grammars it is a gotcha.

**Fix.** Pick the character class that matches your threat model:

```csharp
// Latin script only: Basic Latin letters plus Latin-1 Supplement letters.
// Rejects Cyrillic а, Greek ο, and other confusables.
static readonly RuneSet LatinLetters =
    RuneSet.Ascii.Letters |
    RuneSet.Range(new Rune(0x00C0), new Rune(0x00FF));

// Greek and Coptic block only
static readonly RuneSet Greek =
    RuneSet.Range(new Rune(0x0370), new Rune(0x03FF));

public static readonly Rule LatinIdentifier =
    OneOrMore(RuneIn(LatinLetters | RuneSet.Ascii.Digits | RuneSet.Runes("_")));
```

For full UAX #31 Script_Extensions-based detection (the standard algorithm for "is this identifier mixing scripts in a suspicious way"), use a dedicated library; the parser's `RuneSet` is the coarse-grained control.

## Variation Selectors

U+FE00..U+FE0F and U+E0100..U+E01EF are invisible runes that select alternate glyph forms for the preceding character. U+FE0F is the one you are most likely to encounter: it flips emoji between text-style (`❤`) and emoji-style (`❤️`) rendering. Two strings that visually look identical can contain or omit a variation selector, which makes byte-equality matching fail. Neither lexer strips them.

**Fix.** The caller strips them if the grammar does not care about glyph selection:

```csharp
var cleaned = string.Concat(input.EnumerateRunes()
    .Where(r => r.Value is not (>= 0xFE00 and <= 0xFE0F)
             && r.Value is not (>= 0xE0100 and <= 0xE01EF)));

var result = grammar.Parse(cleaned);
```

If you are doing emoji-sensitive parsing, be careful: variation selectors are part of the encoded form of some emoji (the emoji-style heart, some keycap sequences), and stripping them can change which emoji the user sees.

## The Common Thread

All of these are Unicode surprises that live *outside* the lexer's tokenization decision. They fix either upstream (caller-side input preprocessing) or sideways (grammar-design choice of character classes and tolerance rules). None of them are fixed by switching lexer mode.

If you want the parser to handle any of these natively someday, the "Open Questions" section of [UnicodeInternalsArchitecture.md](UnicodeInternalsArchitecture.md) tracks which ones might eventually become first-class.
