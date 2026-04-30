# Unicode Gotchas

Most Unicode surprises can't be fixed by the parser's lexer choice. They live outside the "what is a token?" question the lexers answer, so the fix is usually caller-side preprocessing (clean the input before parsing) or grammar-design (pick the right `RuneSet`, add explicit tolerance rules). A few gotchas below are lexer-specific, and those sections call that out directly.

This doc lists the common gotchas, why they bite, and the idiomatic workaround for each. If you are choosing between `RuneLexer` and `GraphemeLexer`, see [UnicodeInternalsArchitecture.md](UnicodeInternalsArchitecture.md). That is a different decision.

## Identifier Matching

Matching "an identifier" the way a programming language does is a solved Unicode problem, but each language still gets to define its own profile. UAX #31 defines two useful properties, `XID_Start` and `XID_Continue`, and the default identifier shape is `XID_Start XID_Continue*`. Python and Rust build on this shape; C#, ECMAScript, Java, and Swift have similar but not identical rules. The parser exposes the UAX #31-shaped rule as `Rules.Identifier()`:

```csharp
var name = Identifier().As("name");
```

That accepts `foo`, `café`, `καλημέρα`, `ℼ`, and rejects `2foo`, `_foo` (underscore is not in XID_Start in the base UAX #31 profile), and the Arabic ligature `ﷺ` (U+FDFA, which is a letter by General_Category but excluded because its NFKC decomposition is a full multi-word phrase).

Two quiet wins you get for free:

- **NFC equivalence (UAX #31 R4).** `ParseOptions.NormalizeInput` defaults to `NormalizationForm.FormC`, so `café` precomposed (U+00E9) and `café` as `e` + combining acute (U+0301) normalize to the same string before the lexer sees them, and both parse to the same identifier. You don't write any code for this.

- **Runtime-backed `XID_Start` and `XID_Continue` tables.** Yes: `RuneSet.XidStart` and `RuneSet.XidContinue` are the Unicode XID properties used by UAX #31's default identifier shape, not custom Inductor-specific character classes. The version caveat is where the Unicode data comes from. Most of each set comes from Unicode General_Category data exposed by the .NET runtime: letters and letter numbers for start characters, plus combining marks, decimal digits, and connector punctuation for continuation characters. `RuneSet.Xid.cs` stores only the small UAX #31 add/remove lists needed on top of those categories, such as `U+2118` SCRIPT CAPITAL P and the Arabic ligatures excluded for NFKC stability. Exact code point coverage follows the Unicode version exposed by the runtime's category tables plus those stored exception tables.

Identifier matching works across the scripts covered by the runtime's Unicode data under either lexer, including scripts where a "letter" is a base character plus a vowel mark (Devanagari, Thai, Arabic-with-vowels). When `StringInfo` bundles those clusters into single tokens, `Identifier()` uses [`WithinGrapheme`](#withingrapheme-general-purpose-sub-grapheme-matching) internally to walk each token's runes and check them individually against the identifier rules. No `InputUnit.Rune` switch required:

```csharp
Identifier().Parse("हिन्दी");   // matches under the default grapheme lexer
Identifier().Parse("กำ");        // Thai with SARA AM: also matches
Identifier().Parse("καλημέρα"); // Greek: matches
```

### WithinGrapheme: general-purpose sub-grapheme matching

`WithinGrapheme(innerRule)` is the building block `Identifier()` uses, exposed on its own for other grammar patterns that need to look inside a grapheme. It reads one outer token, runs the inner rule against that token's runes (as a mini rune-lexed stream), and requires the inner rule to consume every rune of the grapheme. Partial matches fail because graphemes are atomic.

Uses beyond identifiers:

```csharp
// Accept any grapheme whose runes are all ASCII letters. Rejects "é"
// (not ASCII) and decomposed "é" (two runes) alike.
var asciiOnlyLetter = WithinGrapheme(OneOf(RuneSet.Ascii.Letters));

// Emoji-with-modifier matcher: one base emoji rune optionally followed
// by skin-tone / ZWJ runes, all as one grapheme.
var emojiCluster = WithinGrapheme(AllOf(
    OneOf(emojiBaseSet),
    ZeroOrMore(OneOf(skinToneOrZwjSet))
));

// Hangul syllable expressed as jamo: leading + medial + optional trailing.
var jamoCluster = WithinGrapheme(AllOf(
    OneOf(leadingJamo),
    OneOf(medialJamo),
    Optional(OneOf(trailingJamo))
));
```

Caveats: the inner rule runs against a fresh sub-lexer that doesn't share trace or budget state with the outer lexer. It's bounded to the current token's span, so ordinary character-consuming rules stay tiny, but avoid arbitrary long-running user code inside it. Inner-rule symbols are discarded. `WithinGrapheme` emits one leaf per grapheme to the outer tree.

### Matching specific languages

`Identifier` takes two optional `RuneSet` parameters, `extraStartRunes` and `extraBodyRunes`, that get unioned into `XID_Start` and `XID_Continue` respectively. UAX #31 calls this a "profile extension." Combined with `ParseOptions.NormalizeInput`, these cover the real-world identifier rules of most languages that are built on UAX #31.

Strict UAX #31 (the reference spec, no language-specific additions). Raku is the closest mainstream match.

```csharp
Identifier();  // base UAX #31-style form
```

Python 3 identifiers, per [PEP 3131](https://peps.python.org/pep-3131/) and the [Language Reference](https://docs.python.org/3/reference/lexical_analysis.html#identifiers). Python adds `_` to Start and uses NFKC (not NFC) for equivalence.

```csharp
var python = Identifier(extraStartRunes: RuneSet.Runes("_"));
var result = python.Parse(input, new ParseOptions
{
    NormalizeInput = NormalizationForm.FormKC,
});
```

Rust identifiers, per the [Rust Reference](https://doc.rust-lang.org/reference/identifiers.html). Same profile as Python 3 (adds `_` to Start, uses NFKC). One Rust-specific rule this recipe does **not** enforce: Rust rejects bare `_` as an identifier, requiring `_ XID_Continue+`. If you need that, wrap the rule in an explicit check for the second character. For most grammars the practical difference is negligible.

```csharp
var rust = Identifier(extraStartRunes: RuneSet.Runes("_"));
var result = rust.Parse(input, new ParseOptions
{
    NormalizeInput = NormalizationForm.FormKC,
});
```

ECMAScript-style identifiers (JavaScript, TypeScript), per [ECMA-262 §12.7](https://tc39.es/ecma262/#sec-names-and-keywords). The shape is right (add `_` and `$` to both positions, no normalization), but note the caveat: ECMAScript officially uses `ID_Start` and `ID_Continue`, not the X variants. The parser only exposes the XID sets, which are a strict subset, so this recipe accepts slightly less than a spec-conformant JS engine would. The difference is a handful of exotic code points that almost never appear in real source.

```csharp
var ecmascript = Identifier(
    extraStartRunes: RuneSet.Runes("_$"),
    extraBodyRunes: RuneSet.Runes("$"));    // "_" is already in XID_Continue
var result = ecmascript.Parse(input, new ParseOptions
{
    NormalizeInput = null,
});
```

C# identifiers, per [ECMA-334 §7.4.3](https://www.ecma-international.org/publications-and-standards/standards/ecma-334/). C# allows `_` in Start and uses category-based rules rather than XID directly. For grammars, `Identifier(extraStartRunes: RuneSet.Runes("_"))` with default NFC is a close approximation for ordinary source. It isn't a spec-exact C# lexer.

Java identifiers use `Character.isJavaIdentifierStart` and `Character.isJavaIdentifierPart`, which are their own rule. Not reproducible via `Identifier` parameters alone; a Java-conforming grammar would compose against a custom `RuneSet` built from those predicates.

Swift has its own enumerated list of ranges that resembles XID but isn't a property reference. Not reproducible via `Identifier` parameters alone.

If you are restricting to a specific script for security reasons (mixed-script phishing, homoglyph attacks), see the Homoglyph Confusables section below. `Identifier()` is the general-purpose match, not a script-restricted one.

## Case-Insensitive Matching Beyond ASCII

The `LiteralIgnoreAsciiCase` leaf does ASCII case-insensitive matching (A ↔ a) and is all most grammars need. Full Unicode case-insensitive matching has script-specific surprises that neither lexer handles: German `ß` uppercases to `SS` (one character becomes two), Turkish has dotted-i and dotless-i as distinct letters, Greek final sigma (ς) pairs with regular sigma only at word boundaries. The leaf is ASCII-only on purpose. Extending it to full Unicode silently produces wrong results on Turkish, Greek, and German text.

**Fix.** Use the built-in leaf and accept that case-insensitive matching of non-ASCII text isn't supported:

```csharp
public static readonly Rule SelectKeyword = LiteralIgnoreAsciiCase("select");
```

Don't try to extend this to full Unicode case-insensitive matching. It'll get subtly wrong for Turkish, Greek, and German.

## BOM At File Start

Byte Order Mark, U+FEFF. Windows tools and some editors prepend it to text files as an encoding hint, so the first character of the file is invisible but present. A grammar that expects the file to start with a specific keyword like `<?xml` or `function` sees the BOM as an unexpected character at position 0 and fails to match. Both lexers see it identically because the BOM is a single rune and a single grapheme.

**Fix.** The caller strips it before parsing:

```csharp
var cleaned = input.TrimStart('\uFEFF');
var result = grammar.Parse(cleaned);
```

## Zero-Width and Invisible Format Characters

U+200B (zero-width space), U+200C (zero-width non-joiner), U+200D (zero-width joiner), U+00AD (soft hyphen), and similar runes appear as characters in the input but render as nothing or render conditionally. A string like `"ap\u00ADple"` looks like `"apple"` in an editor but doesn't match `Literal("apple")` because the soft hyphen is a real character in the token stream.

On modern .NET, `GraphemeLexer` handles ZWJ correctly inside emoji sequences (it groups them into one grapheme per UAX #29). Bare ZWJs and other format characters outside emoji contexts still come through as their own tokens under both lexers. Legacy `StringInfo` runtimes have broader ZWJ gaps covered in [Pre-.NET 5 Grapheme Segmentation](#pre-net-5-grapheme-segmentation).

**Fix.** The caller strips them before parsing, or the grammar's character classes tolerate them explicitly. For stripping:

```csharp
static readonly HashSet<int> InvisibleFormat = new()
{
    0x200B,  // zero-width space
    0x200C,  // zero-width non-joiner
    0x200D,  // zero-width joiner (keep if you care about emoji ZWJ sequences!)
    0x00AD,  // soft hyphen
    0xFEFF,  // BOM / zero-width no-break space
};

var cleaned = string.Concat(input.EnumerateRunes()
    .Where(r => !InvisibleFormat.Contains(r.Value)));

var result = grammar.Parse(cleaned);
```

If your grammar uses `GraphemeLexer` and processes emoji sequences, don't strip ZWJ (U+200D) indiscriminately. You'll break 👨‍👩‍👧‍👦 and similar sequences.

## Homoglyph Confusables

Cyrillic `а` (U+0430) and Latin `a` (U+0061) render identically in most fonts but are different code points. A grammar using `RuneSet.Ascii.Letters` rejects Cyrillic `а` even though the user "sees" a Latin `a`. A grammar using `RuneSet.Letters` accepts both and doesn't distinguish them. Both lexers treat the code points identically because they really are different runes.

This is a grammar-design decision. For security-sensitive grammars (mixed-script identifier detection, phishing-resistance) it's a *feature*: refusing homoglyphs protects against visual-spoofing attacks. For forgiving grammars it's a gotcha.

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
    OneOrMore(OneOf(LatinLetters | RuneSet.Ascii.Digits | RuneSet.Runes("_")));
```

For full UAX #31 Script_Extensions-based detection (the standard algorithm for "is this identifier mixing scripts in a suspicious way"), use a dedicated library. The parser's `RuneSet` is the coarse-grained control.

## Variation Selectors

U+FE00..U+FE0F and U+E0100..U+E01EF are invisible runes that select alternate glyph forms for the preceding character. U+FE0F is the one you are most likely to encounter: it flips emoji between text-style (`❤`) and emoji-style (`❤️`) rendering. Two strings that visually look identical can contain or omit a variation selector, which makes exact string matching fail. Neither lexer strips them.

**Fix.** The caller strips them if the grammar doesn't care about glyph selection:

```csharp
var cleaned = string.Concat(input.EnumerateRunes()
    .Where(r => r.Value is not (>= 0xFE00 and <= 0xFE0F)
             && r.Value is not (>= 0xE0100 and <= 0xE01EF)));

var result = grammar.Parse(cleaned);
```

If you are doing emoji-sensitive parsing, be careful: variation selectors are part of the encoded form of some emoji (the emoji-style heart, some keycap sequences), and stripping them can change which emoji the user sees.

## CRLF Under GraphemeLexer

Unicode text segmentation treats `\r\n` as a single grapheme cluster (UAX #29 rule GB3), so `GraphemeLexer` hands the parser one two-char token whenever it sees a Windows line ending. This bites any line-based grammar that tries to match or stop on a bare `\n`:

- `Grapheme('\n')` matches a one-grapheme token whose content is exactly `'\n'`. The CRLF grapheme has content `"\r\n"`, so `Grapheme('\n')` does *not* match it.
- `OneOf(RuneSet.Runes("\n"))` or `OneOf(RuneSet.Runes("\r\n"))` matches a single-rune token whose rune is in the set. A CRLF grapheme is two runes, so it matches no single-rune set. It fails `OneOf` regardless of what runes you put in the set.
- `NoneOf(RuneSet.Runes("\n"))` does the opposite: multi-rune tokens pass `NoneOf` unconditionally. `ZeroOrMore(NoneOf(stopSet))` used to scan "everything up to a newline" will greedily swallow the terminating CRLF as body content instead of stopping at it, then the terminator fails because there is nothing left.

`RuneLexer` doesn't have this problem. It emits `'\r'` and `'\n'` as separate tokens. The bite is `GraphemeLexer`-specific, which is the default.

**Fix.** Use the built-in `EndOfLine()` rule. It is `FirstOf(Literal("\r\n"), OneOf(RuneSet.SingleRuneLineTerminators))` under the hood, so the CRLF grapheme is tried as a unit before the single-rune terminators (LF, CR, VT, FF, NEL, LINE SEPARATOR, PARAGRAPH SEPARATOR per UAX #18 Annex C). Pass `eofIsEol: true` for the "line terminator here, or end of input" case, and wrap with `Optional` for "line terminator here, or none at all". Anywhere a grammar cares about line breaks, reach for these instead of building one with `Grapheme('\n')` or a `OneOf` over a rune set:

```csharp
// Match a Unicode line terminator (CRLF, LF, CR, NEL, LS, PS, VT, FF).
AllOf(..., EndOfLine())

// Whitespace that includes newlines: EndOfLine first so the CRLF pair
// commits before the single-rune fallbacks pick up the '\r' alone.
public static readonly Rule WhitespaceOrNewline = ZeroOrMore(FirstOf(
    EndOfLine(),
    OneOf(RuneSet.Ascii.Whitespace)
));

// Scanning "up to end of line": use a rule-based stop with Not(EndOfLine()).
// NoneOf over a single-rune set would silently eat the CRLF grapheme.
public static readonly Rule LineComment = AllOf(
    Grapheme('%'),
    ZeroOrMore(AllOf(Not(EndOfLine()), AnyToken())),
    EndOfLine(eofIsEol: true)
);
```

The three anti-patterns to avoid in any line-based grammar:

```csharp
// BROKEN on Windows line endings under GraphemeLexer.
AllOf(..., Grapheme('\n'))                             // fails on CRLF input
ZeroOrMore(OneOf(RuneSet.Runes("\r\n")))        // skips zero CRLF graphemes
ZeroOrMore(NoneOf(RuneSet.Single('\n')))      // swallows the CRLF terminator
```

If a grammar is a port of regex semantics that explicitly targets LF-only (some Markdown-style formats, for instance), the failure on CRLF is faithful to the source and you can leave `Grapheme('\n')` as-is. Mark the grammar with a comment so the next reader knows the LF-only behavior is intentional, not an oversight.

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

The common thread is timing. Combining marks have been in Unicode since the start, so the legacy walker handles them. Everything UAX #29 added later, especially the emoji rules from 2014 onward, the legacy walker doesn't know about. Microsoft updated `StringInfo` to ICU in .NET 5. Unity's Mono didn't follow, and IL2CPP compiles from that Mono.

**Fix.** Three options, in order of effort:

1. If the grammar doesn't actually need to tokenize emoji or complex-script text at the grapheme level, do nothing. ASCII, source code, config files, and most DSLs are unaffected.
2. If a specific input causes trouble, switch that grammar to `RuneLexer` and handle the multi-rune sequence explicitly with a small rule. This trades grapheme convenience for one extra rule and works on every runtime.
3. Add a custom UAX #29 implementation into the parser. Tracked in [xlll-vendor-a-uax-#29-grapheme-cluster-implementation.md](../backlog/xlll-vendor-a-uax-#29-grapheme-cluster-implementation.md). Gives full conformance everywhere, at the cost of maintaining Unicode data in the repository.

The repo's test suite documents the broken cases explicitly. Look for tests gated behind `#if !UNITY_INCLUDE_TESTS` in [GraphemeRuleTests.cs](../src/InductorParser.Tests/Rules/GraphemeRuleTests.cs). Each one is a category that the legacy walker mishandles.
