# Unicode Gotchas

Some Unicode surprises cannot be fixed by the parser's lexer choice. Both `RuneLexer` and `GraphemeLexer` hit these identically, because they live outside the "what is a token?" question the lexers answer. The fix is always either caller-side preprocessing (clean the input before parsing) or grammar-design (pick the right `RuneSet`, add explicit tolerance rules).

This doc lists the common gotchas, why they bite, and the idiomatic workaround for each. If you are choosing between `RuneLexer` and `GraphemeLexer`, see [UnicodeInternalsArchitecture.md](UnicodeInternalsArchitecture.md); that is a different decision.

## Case-Insensitive Matching Beyond ASCII

The `LiteralIgnoreAsciiCase` leaf does ASCII case-insensitive matching (A ↔ a) and is all most grammars need. Full Unicode case-insensitive matching has script-specific surprises that neither lexer handles: German `ß` uppercases to `SS` (one character becomes two), Turkish has dotted-i and dotless-i as distinct letters, Greek final sigma (ς) pairs with regular sigma only at word boundaries. The leaf is ASCII-only on purpose. Extending it to full Unicode silently produces wrong results on Turkish, Greek, and German text.

**Fix.** Use the built-in leaf and accept that case-insensitive matching of non-ASCII text is not supported:

```csharp
public static readonly Rule SelectKeyword = LiteralIgnoreAsciiCase("select");
```

Do not try to extend this to full Unicode case-insensitive matching. It will get subtly wrong for Turkish, Greek, and German.

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

## CRLF Under GraphemeLexer

Unicode text segmentation treats `\r\n` as a single grapheme cluster (UAX #29 rule GB3), so `GraphemeLexer` hands the parser one two-char token whenever it sees a Windows line ending. This bites any line-based grammar that tries to match or stop on a bare `\n`:

- `Token('\n')` matches a one-grapheme token whose content is exactly `'\n'`. The CRLF grapheme has content `"\r\n"`, so `Token('\n')` does *not* match it.
- `RuneIn(RuneSet.Runes("\n"))` or `RuneIn(RuneSet.Runes("\r\n"))` matches a single-rune token whose rune is in the set. A CRLF grapheme is two runes, so it matches no single-rune set — it fails `RuneIn` regardless of what runes you put in the set.
- `RuneNotIn(RuneSet.Runes("\n"))` does the opposite: multi-rune tokens pass `RuneNotIn` unconditionally. `ZeroOrMore(RuneNotIn(stopSet))` used to scan "everything up to a newline" will greedily swallow the terminating CRLF as body content instead of stopping at it, then the terminator fails because there is nothing left.

`RuneLexer` doesn't have this problem; it emits `'\r'` and `'\n'` as separate tokens. The bite is `GraphemeLexer`-specific, which is the default.

**Fix.** Add an explicit `Literal("\r\n")` alternative anywhere the grammar cares about line breaks. One helper covers the three idiomatic uses:

```csharp
// Match any of LF, CR, or the CRLF grapheme.
private static readonly Rule LineBreak = Or(
    Literal("\r\n"),
    RuneIn(RuneSet.Runes("\r\n"))
);

// Whitespace that includes newlines: put the Literal first so the
// longer alternative commits before the single-rune fallback.
public static readonly Rule OptionalWhitespace = ZeroOrMore(Or(
    Literal("\r\n"),
    RuneIn(RuneSet.Ascii.Whitespace)
));

// Scanning "up to end of line" — use a rule-based stop with Not(LineBreak),
// not RuneNotIn. RuneNotIn would silently eat the CRLF grapheme.
public static readonly Rule LineComment = And(
    Token('%'),
    ZeroOrMore(And(Not(LineBreak), AnyToken())),
    Or(OneOrMore(LineBreak), Eof())
);
```

The three anti-patterns to avoid in any line-based grammar:

```csharp
// BROKEN on Windows line endings under GraphemeLexer.
And(..., Token('\n'))                             // fails on CRLF input
ZeroOrMore(RuneIn(RuneSet.Runes("\r\n")))        // skips zero CRLF graphemes
ZeroOrMore(RuneNotIn(RuneSet.Single('\n')))      // swallows the CRLF terminator
```

If a grammar is a port of regex semantics that explicitly targets LF-only (some Markdown-style formats, for instance), the failure on CRLF is faithful to the source and you can leave `Token('\n')` as-is. Mark the grammar with a comment so the next reader knows the LF-only behavior is intentional, not an oversight.

## The Common Thread

All of these are Unicode surprises that live *outside* the lexer's tokenization decision. They fix either upstream (caller-side input preprocessing) or sideways (grammar-design choice of character classes and tolerance rules). None of them are fixed by switching lexer mode.

If you want the parser to handle any of these natively someday, the "Open Questions" section of [UnicodeInternalsArchitecture.md](UnicodeInternalsArchitecture.md) tracks which ones might eventually become first-class.

## Pre-.NET 5 Grapheme Segmentation

This one is different from the gotchas above. It isn't an input-side surprise the caller can preprocess away, and it isn't a grammar-design choice. It's the runtime under your feet behaving differently depending on which .NET you're on, and it only bites `GraphemeLexer`.

`GraphemeLexer` calls `System.Globalization.StringInfo.GetNextTextElement` to find the next grapheme boundary. On .NET 5 and later this is UAX #29 conformant, because the BCL switched to ICU for globalization. On .NET Framework, .NET Core 3.x, and the Mono runtime that Unity ships (which IL2CPP compiles from), `StringInfo` still uses an algorithm Microsoft wrote before UAX #29 stabilized. It's roughly "Unicode 3.x grapheme cluster": base character plus combining marks, surrogate pairs as one unit, Hangul syllable basics. It's not extended-grapheme-cluster aware.

What still works on the legacy runtimes:

- ASCII.
- Latin with combining diacritics. `é` as `e` + U+0301 is one grapheme.
- Single-rune emoji like 🎸. One rune, one grapheme.
- Most simple consonant-plus-mark sequences in Devanagari, Arabic, Hebrew.

What breaks:

- Emoji ZWJ sequences. The family 👨‍👩‍👧‍👦 splits at every ZWJ.
- Emoji plus skin-tone modifier. 👋🏽 splits into two.
- Regional indicator pairs (flag emoji). 🇺🇸 splits into two.
- Thai SARA AM. "kam" (ก + ํา) splits.
- Other extended-grapheme-cluster rules added after about 2003 (Prepend characters, Extended_Pictographic sequences).

The common thread is timing. Combining marks have been in Unicode since the start, so the legacy walker handles them. Everything UAX #29 added later, especially the emoji rules from 2014 onward, the legacy walker doesn't know about. Microsoft updated `StringInfo` to ICU in .NET 5; Unity's Mono didn't follow, and IL2CPP compiles from that Mono.

**Fix.** Three options, in order of effort:

1. If the grammar doesn't actually need to tokenize emoji or complex-script text at the grapheme level, do nothing. ASCII, source code, config files, and most DSLs are unaffected.
2. If a specific input causes trouble, switch that grammar to `RuneLexer` and handle the multi-rune sequence explicitly with a small rule. This trades grapheme convenience for one extra rule and works on every runtime.
3. Vendor a UAX #29 implementation into the parser. Tracked in [backlog/r000](../backlog/r000-vendor-a-uax-#29-grapheme-cluster-implementation.md). Half a day of work, gives full conformance everywhere.

The repo's test suite documents the broken cases explicitly. Look for tests gated behind `#if !UNITY_INCLUDE_TESTS` in [TokenRuleTests.cs](../src/InductorParser.Tests/Rules/TokenRuleTests.cs); each one is a category that the legacy walker mishandles.
