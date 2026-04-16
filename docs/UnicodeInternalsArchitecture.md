# Unicode Internals Architecture

This doc is about how the parser handles Unicode text at its lowest levels. Read this if you need to pick between `GraphemeLexer` and `RuneLexer`, understand what the parser sees when you feed it a string, or debug a Unicode-related issue. If you just want to write grammars, the programming model lives in [ProgrammingAGrammar.md](ProgrammingAGrammar.md).

## Unicode In One Page

Unicode has a bunch of concepts a parser could engage with. They fall into three categories, and it helps to separate them because they are not a single stack: they are a stack (representation levels), a set of orthogonal operations (text transformations), and a set of downstream algorithms (text analysis).

### Unicode Representation Hierarchy

You need to pick one as the parser's token. The stack, from the lowest physical layer up, with each layer built from one or more of the layer below:

- **Code units** (physical encoding): the fixed-width pieces a string is stored as. UTF-16 uses 16-bit code units; UTF-8 uses 8-bit code units (bytes). In .NET, `string` is an array of UTF-16 code units and `char` holds one code unit. One Unicode character (i.e. Code Point below) can span multiple code units (surrogate pairs in UTF-16, multi-byte sequences in UTF-8). In UTF-16 one code point is one or two code units; in UTF-8 one code point is one to four code units.  
- **Code points** (The atoms of Unicode): A code point is the actual number that represents one Unicode character: 0 to 0x10FFFF. The subset of unicode numbers that are actually valid Unicode characters are called *scalar values*. This is what .NET's `System.Text.Rune` holds. The others are used for code unit encoding by UTF-16. This is the level `RuneLexer` works at. 
- **Grapheme clusters** (Actual Characters): Built from one or more code points via UAX #29 rules. A grapheme is what a human perceives as one character. It can be a single code point, like `p`. Also valid: `é` as `e` + "combining acute" is one grapheme built from two code points. 👨‍👩‍👧‍👦 is one grapheme built from seven code points. 👋🏽 is one grapheme built from two code points. This is the level `GraphemeLexer` works at and what the parser uses by default.

Each layer is a composition over the one below, so any string has a code-unit count, a code-point count, and a grapheme count, and the counts only diverge when the composition is non-trivial. Some examples:
- For ASCII, all three are equal. 
- For text that stays inside the first 65,536 code points (U+0000..U+FFFF) with no combining marks, all three are still equal. 
- For a character above U+FFFF with no modifier (a lone 🎸, say), UTF-16 uses a surrogate pair so the code-unit count doubles while the code-point and grapheme counts stay the same. 
- For combining-mark text or emoji sequences (👋🏽, 👨‍👩‍👧‍👦), multiple code points form one grapheme, so the grapheme count falls below the code-point count.

### Text transformations (orthogonal)

Rewrites that produce a different rune sequence. These apply to runes; they are not a higher layer.

- **Normalization**: canonical rewrites so that visually-identical text compares equal regardless of spelling. "café" as one precomposed rune and "café" as `e` + "combining accent" are different rune sequences but the same normalized text. There are four normalization forms defined by Unicode; the parser uses the *composed* form by default (the one that produces U+00E9 `é` as a single code point rather than `e` + combining acute). See the Normalization section below.
- **Case folding**: Unicode-aware case-insensitive comparison. Not the same as `ToLower`: German `ß` folds to `ss`, Turkish dotless-i behaves differently from dotted i, Greek final sigma folds to sigma. The parser does not apply this by default; see the Workarounds section.

### Downstream algorithms (not parser concerns)

These operate on text that has already been parsed or on raw text as standalone operations.

- **Collation**: locale-aware sort order.
- **Bidi**: visual ordering for mixed right-to-left and left-to-right text.
- **Line breaking** (UAX #14) and **word segmentation** (UAX #29): where you are allowed to break a paragraph into lines or split it into words.

Nothing in this doc engages with these. They run outside the parser, on the parsed output or on raw text through a dedicated Unicode library.

## The Two Lexers

The parser exposes two lexers. Both produce one "token" per `Read()` call; they differ in what counts as a token.

### GraphemeLexer (default)

The token is one grapheme cluster. The lexer walks the input using UAX #29 grapheme boundary rules (via `System.Globalization.StringInfo`) and returns one grapheme per `Read()`, regardless of how many runes that grapheme spans. It is designed so each token matches what a user would perceive as a single character.

```
Input:  "🎸 = 👋🏽;"
Stream: [🎸] [ ] [=] [ ] [👋🏽] [;]    (6 graphemes)
```

How individual rules react to this stream (and when to switch to `RuneLexer`) lives in [ProgrammingAGrammar.md](ProgrammingAGrammar.md).

### RuneLexer (opt-in)

The token is one rune. Set `ParseOptions.InputUnit = InputUnit.Rune` to select it. The lexer walks the input with `string.EnumerateRunes()` and returns one rune per `Read()`. Multi-rune graphemes come through as separate tokens.

```
Input:  "🎸 = 👋🏽;"
Stream: [🎸] [ ] [=] [ ] [👋] [🏽] [;]    (7 runes)
```

### When the two lexers differ

For ASCII input, and for text where accented letters are stored as single precomposed code points (e.g. `é` as U+00E9 rather than as `e` + combining acute, which is what the default composition normalization produces), both lexers produce identical streams. The differences are at:

| Input                                   | GraphemeLexer          | RuneLexer              |
|-----------------------------------------|------------------------|------------------------|
| `café` (precomposed)                    | 4 tokens               | 4 tokens               |
| `café` (decomposed, no normalization)   | 4 tokens               | 5 tokens               |
| `🎸`                                    | 1 token                | 1 token                |
| `👋🏽`                                  | 1 token                | 2 tokens               |
| `🇺🇸`                                  | 1 token                | 2 tokens               |
| `👨‍👩‍👧‍👦`                          | 1 token                | 7 tokens               |

After composition normalization (default), most combining-mark cases collapse to precomposed single-rune forms, so GraphemeLexer and RuneLexer produce the same stream for almost all Latin, Greek, Cyrillic, and Han/Kana text. The two diverge reliably only for emoji sequences (which have no precomposed forms) and for scripts that have no precomposed forms for their combining sequences (Devanagari conjuncts, some Arabic combinations).

### Swapping the lexer is a config change, not a grammar change

Grammars are written against the `Rule` API and do not know which lexer is driving them. Changing `ParseOptions.InputUnit` swaps the lexer for the whole parse, and the same grammar works either way.

For typical input (ASCII, or text inside the first 65,536 code points with default composition normalization on), the two lexers produce identical token streams and every rule behaves identically. The rules whose behavior *can* diverge are the ones that compare against a token directly: `Char`, `RuneIn`, `RuneNotIn`, `Literal`, `Peek`, `Not`. Composite rules (`And`, `Or`, `OneOrMore`, etc.) only differ by inheritance from a primitive rule inside them.

Where the two lexers actually diverge, the `RuneLexer` behavior is usually the buggy one: it was matching part of a grapheme as if it were a standalone character. A grammar rule that consumes one rune from `👨‍👩‍👧‍👦` matches just the first 👨 under `RuneLexer` and leaves the other six runes (three ZWJs and three people emoji) dangling for subsequent rules to trip over, which is rarely what the grammar author intended. `GraphemeLexer` fixes this by treating the whole sequence as one token. The framing is less "`GraphemeLexer` broke my grammar" and more "`GraphemeLexer` revealed that my grammar was silently wrong on multi-rune input." `RuneLexer` is the right tool when you specifically want to see inside a grapheme (walking combining marks individually, rune-level Unicode category analysis, implementing a Unicode library on top of the parser), not for normal text processing.

## Position Tracking

Switching the lexer's token type does not force the parser to give up the other counters. Both lexers maintain three positions simultaneously throughout parsing:

- **Token index**: how many tokens consumed (graphemes for GraphemeLexer, runes for RuneLexer).
- **Rune offset**: code-point offset into the input.
- **Char offset**: UTF-16 code unit offset (matches `string[i]` behavior).

For GraphemeLexer, the token index is the grapheme count. For RuneLexer, the token index and the rune offset are the same counter. In both cases, all three values are cheap to maintain because the lexer is already walking the input rune by rune internally.

`ParseResult` exposes the minimum useful set:

```csharp
public readonly struct ParseResult
{
    public int  ErrorCharIndex         { get; }   // UTF-16 char index; use for input[...]
    public int  ErrorLine              { get; }   // 0-based line number (LSP)
    public int  ErrorColumn            { get; }   // 0-based column in UTF-16 chars (LSP)

    // For callers that measure in other units. Derived from the char index.
    public int  ErrorRuneIndex         { get; }
    public int  ErrorGraphemeIndex     { get; }
}
```

Three fields cover the common cases: `ErrorCharIndex` indexes into the input string directly, `ErrorLine` + `ErrorColumn` give the editor-ready position (in UTF-16 chars, 0-based, following the Language Server Protocol end-to-end; see [ProgrammingModel.md](ProgrammingModel.md) "LSP Position Semantics" for the full rationale). The two extra index properties are there for callers that count in runes or graphemes instead; they are computed lazily from the char index the one time they are asked for, so they cost nothing unless used. Column in rune or grapheme units is deliberately not exposed as a field because callers who need it can derive it from the corresponding index cheaply and the combinatorial expansion was not worth it.

## Encoding Happens Firsts

The parser takes a `string`. Encoding is handled before the parser is ever called. If your document lives on disk as UTF-8, UTF-16, or some legacy codepage, decode it into a `string` with the appropriate `Encoding` class (`File.ReadAllText(path, Encoding.UTF8)`, `Encoding.Unicode.GetString(bytes)`, `Encoding.GetEncoding("Windows-1252").GetString(bytes)`, etc.) before calling `.Parse(...)`. By the time the parser sees the input it is a .NET `string` with no encoding tag; everything below is about how the lexer iterates those characters.

```
Disk/network                             Caller                          Parser
-----------                              -----------                     -----------
UTF-8 bytes    ─→ Encoding.UTF8        ─→ string (UTF-16) ─→ .Parse(...) ─→ GraphemeLexer ─→ token stream
UTF-16 bytes   ─→ Encoding.Unicode     ─→ string (UTF-16) ─→ .Parse(...) ─→ GraphemeLexer ─→ token stream
Win-1252 bytes ─→ Encoding.GetEncoding("Windows-1252") ─→ string ─→ .Parse(...) ─→ token stream
```

Unicode is the character set, a numbered list of characters. UTF-8, UTF-16, and UTF-32 are different ways to represent those numbers as bytes. A document stored as UTF-8 and a document stored as UTF-16 carry the same Unicode content; they differ only in how the text is laid out on disk. By the time the parser sees a `string` the original on-disk encoding is gone and irrelevant. .NET's `string` type holds Unicode content internally in UTF-16, 

If the caller does not know the encoding of a file, they figure it out upstream (BOM sniffing, content-type headers, ask the user) and feed the parser a properly-decoded `string`.

## Normalization

The parser normalizes input to the composed form by default. This is because most people don't want to write their grammars anticipating all the different ways a single Unicode character might be built, they want to depend on the ultimate form of it. Furthermore, most input is already composed (web content, modern source files, anything produced by normal typing on modern OSes), and the cost of `String.IsNormalized()` is an O(n) scan that short-circuits when the input is already normalized. The default is effectively free for normalized input and fixes potential bugs for denormalized input when grammars don't account for it.

Unicode defines four normalization forms. The library uses the .NET enum directly (`NormalizationForm.FormC` is composed, `FormD` is decomposed, `FormKC` and `FormKD` are the "compatibility" variants that also fold things like superscripts and ligatures into their canonical equivalents). `FormC` (composed) is what almost every grammar wants.

```csharp
public sealed class ParseOptions
{
    // Default normalization form. Composed form is usual. Set to null to disable.
    public NormalizationForm? NormalizeInput { get; set; } = NormalizationForm.FormC;
    // ... other options
}
```

Callers who want byte-exact round-trippability (where `tree.ToString()` must match the original input character for character) set `NormalizeInput = null`. The tradeoff is that input in the "wrong" form will silently fail exact-match rules that are written for a specific composition.

A consequence worth documenting: when normalization is on (the default), positions reported in `ParseResult` are into the *normalized* string, not the original. For input already in composed form (most input) the two are identical, so the distinction never matters. For genuinely decomposed input, positions drift from the original. Callers who set `NormalizeInput = null` skip normalization entirely and get positions into the original string directly, at the cost of losing the normalization safety net. See the Open Questions section for the "provide a position map" idea that would close the gap without forcing the opt-out.

## Problems The Lexer Does Not Solve

Some Unicode surprises cannot be fixed by choosing a different tokenization. Both lexers hit them identically: case folding beyond ASCII, BOMs, zero-width and invisible format characters, homoglyph confusables, variation selectors. They are caller-side preprocessing concerns or grammar-design concerns, not lexer concerns. See [UnicodeGotchas.md](UnicodeGotchas.md) for the list and the idiomatic workaround for each.

## RuneLexer-Specific: Grapheme Matching

If you are running under `RuneLexer` but need one grapheme to act as a single token at specific rules, the library provides a `Grapheme()` rule primitive that consumes runes until the next UAX #29 boundary:

```csharp
// In RuneLexer mode, match one grapheme as a single unit at this point.
var emojiAtom = Grapheme();
```

In `GraphemeLexer` mode this is redundant (the lexer already groups graphemes). In `RuneLexer` mode it gives you a scalpel where you need grapheme semantics without switching the entire lexer.

## Open Questions

Four Unicode-adjacent questions the first real grammar will need to answer.

**Unicode version pinning for GraphemeLexer.** Grapheme boundaries are defined by UAX #29, which Unicode updates with every release (new emoji, new ZWJ rules, occasional boundary changes). `GraphemeLexer` uses `StringInfo.GetTextElementEnumerator`, which pulls the Unicode version from the runtime. Same grammar parsing the same input can produce different trees on different .NET / Unity versions. For most grammars this is tolerable; for a grammar that wants cross-host determinism (a language spec, a shared file format), we would need to bundle our own UAX #29 tables pinned to a specific Unicode version. That is a real maintenance burden to take on but a real need for some callers. Defer until asked.

**Normalization position mapping.** `ParseResult` positions are into the normalized string, not the original. When input is already in the composed form the two are identical; when it is not, they drift. `.NET`'s `String.Normalize` does not provide a map. Building one is ~50 lines (walk the input by "starter" runes, record `(origOffset, normalizedOffset)` checkpoints) and gives callers a `MapToOriginal(int)` helper. Do it later if someone hits the gap; defaulting to the normalized-position behavior is fine for v1 with documentation.

**Full Unicode case folding.** The ASCII `LiteralIgnoreCase` helper covers HTTP headers, SQL keywords, HTML tag names, and most real needs. A full-Unicode version would handle Turkish dotless-i, German `ß`, Greek final sigma, and the rest of the locale-specific edge cases, at the cost of a big lookup table and locale awareness. Add when a grammar actually needs it.

**Grapheme-level character classes.** `RuneClass.Letters` on GraphemeLexer matches single-rune letter graphemes. A more permissive rule ("any grapheme whose base rune is a letter, accepting trailing combining marks as part of the match") would work better for Devanagari and other scripts with genuine multi-rune letter graphemes that have no precomposed form. Requires deciding the semantics once and documenting them; worth doing if a real Devanagari-aware grammar ships.
