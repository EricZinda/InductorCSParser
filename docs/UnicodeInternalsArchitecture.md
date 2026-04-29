# Unicode Internals Architecture

This doc is about how the parser handles Unicode text at its lowest levels. Read this if you need to pick between `GraphemeLexer` and `RuneLexer`, understand what the parser sees when you feed it a string, or debug a Unicode-related issue. If you just want to write grammars, start with [primer1.md](primer1.md) and [primer2.md](primer2.md), then use [InductorParserReference.md](InductorParserReference.md) as the full API reference.

## Unicode In One Page

[If you are already well-versed in Unicode, skip this section]

Unicode has a bunch of concepts a parser could engage with. They fall into three categories, and it helps to separate them because they are not a single stack: they are a stack (representation levels), a set of orthogonal operations (text transformations), and a set of downstream algorithms (text analysis).

### Unicode Representation Hierarchy

You need to pick one as the parser's token. Here is the stack, from the lowest physical layer up, with each layer built from one or more of the layer below:

- **Code units** (physical encoding): the fixed-width pieces a string is stored as. UTF-16 uses 16-bit code units. UTF-8 uses 8-bit code units (bytes). In .NET, `string` is a sequence of UTF-16 code units and `char` holds one code unit. One Unicode code point (see next layer) can span multiple code units (surrogate pairs in UTF-16, multi-byte sequences in UTF-8). In UTF-16 one code point is one or two code units. In UTF-8 one code point is one to four code units.
- **Code points** (the atoms of Unicode): A code point is a number in the Unicode code space: 0 to 0x10FFFF. The code points that are valid standalone characters are called *scalar values*; they exclude UTF-16 surrogate halves (U+D800..U+DFFF). Scalar values are what .NET's `System.Text.Rune` holds. `RuneLexer` emits one token per scalar value for well-formed UTF-16 input, decoding surrogate pairs as one token. If the input contains a stray surrogate half, there is no scalar value to emit, so `RuneLexer` surfaces that code unit as a one-char token with no `RuneValue`.
- **Grapheme clusters** (Human perceived characters): Built from one or more code points via UAX #29 rules. A grapheme is what a human perceives as one character. It can be a single code point, like `p`. Also valid: `é` as `e` + "combining acute" is one grapheme built from two code points. 👨‍👩‍👧‍👦 is one grapheme built from seven code points. 👋🏽 is one grapheme built from two code points. `GraphemeLexer` uses .NET `StringInfo.GetNextTextElement`; on modern .NET those text elements track UAX #29, while legacy runtimes have known gaps.

Each layer is a composition over the one below, so any string has a code-unit count, a code-point count, and a grapheme count, and the counts only diverge when the composition is non-trivial. Some examples:
- For ASCII, all three are equal. 
- For text that stays inside the first 65,536 code points (U+0000..U+FFFF) with no combining marks, all three are still equal. 
- For a rune above U+FFFF with no modifier (a lone 🎸, say), UTF-16 uses a surrogate pair so the code-unit count doubles while the code-point and grapheme counts stay the same. 
- For combining-mark text or emoji sequences (👋🏽, 👨‍👩‍👧‍👦), multiple code points form one grapheme, so the grapheme count falls below the code-point count.

### Text transformations (orthogonal)

Rewrites that produce a different rune sequence. These apply to runes, they are not a higher layer.

- **Normalization**: canonical rewrites so that visually-identical text compares equal regardless of spelling. "café" as one rune and "café" as two Runes (`e` + "combining accent") are different rune sequences but the same normalized text. There are four normalization forms defined by Unicode. The parser uses the *composed* form by default (the one that produces U+00E9 `é` as a single code point rather than `e` + combining acute). See the Normalization section below.
- **Case-insensitive matching (Unicode)**: treating upper and lower case as equivalent across the full Unicode range. Not the same as `ToLower`: German `ß` pairs with `ss`, Turkish dotless-i behaves differently from dotted i, Greek final sigma pairs with regular sigma. The parser does not apply this by default. See the Workarounds section.

### Downstream algorithms (not parser concerns)

These operate on text that has already been parsed or on raw text as standalone operations.

- **Collation**: locale-aware sort order.
- **Bidi**: visual ordering for mixed right-to-left and left-to-right text.
- **Line breaking** (UAX #14) and **word segmentation** (UAX #29): where you are allowed to break a paragraph into lines or split it into words.

Nothing in this doc engages with these. They run outside the parser, on the parsed output or on raw text through a dedicated Unicode library.

## The Two Lexers

The parser exposes two lexers. Both produce one "token" per `Read()` call. They differ in what counts as a token.

### GraphemeLexer (default)

The token is one .NET text element. The lexer walks the input with `System.Globalization.StringInfo.GetNextTextElement` and returns one text element per `Read()`, regardless of how many runes that element spans. On modern .NET this follows UAX #29 extended-grapheme-cluster behavior; older .NET / Unity Mono runtimes have known segmentation gaps covered in [UnicodeGotchas.md](UnicodeGotchas.md#pre-net-5-grapheme-segmentation). It is designed so each token usually matches what a user would perceive as a single character.

```
Input:  "🎸 = 👋🏽;"
Stream: [🎸] [ ] [=] [ ] [👋🏽] [;]    (6 graphemes)
```

How individual rules react to this stream (and when to switch to `RuneLexer`) lives in [InductorParserReference.md](InductorParserReference.md).

### RuneLexer (opt-in)

The token is one rune for well-formed UTF-16 input. Set `ParseOptions.InputUnit = InputUnit.Rune` to select it. The lexer decodes one Unicode scalar value at a time from the .NET string's UTF-16 code units, including surrogate pairs, and returns one rune per `Read()`. Stray surrogate halves surface as one-code-unit tokens with no `RuneValue`. Multi-rune graphemes come through as separate tokens.

```
Input:  "🎸 = 👋🏽;"
Stream: [🎸] [ ] [=] [ ] [👋] [🏽] [;]    (7 runes)
```

### When the two lexers differ

For ASCII input, and for text where accented letters are stored as single precomposed code points (e.g. `é` as U+00E9 rather than as `e` + combining acute, which is what the default composition normalization produces), both lexers produce identical streams. The differences are at:

| Input                                  | GraphemeLexer emits              | RuneLexer emits                                  |
|----------------------------------------|----------------------------------|--------------------------------------------------|
| `café` (precomposed `é`)               | 4 tokens: `c`, `a`, `f`, `é`     | 4 tokens: same                                    |
| `café` (decomposed, no normalization)  | 4 tokens: `c`, `a`, `f`, `é`     | 5 tokens: `c`, `a`, `f`, `e`, combining acute     |
| `🎸`                                   | 1 token: `🎸`                   | 1 token: `🎸`                                     |
| `👋🏽`                                   | 1 token: `👋🏽`                   | 2 tokens: `👋`, `🏽`                               |
| `🇺🇸`                                    | 1 token: `🇺🇸`                   | 2 tokens: `🇺`, `🇸`                                 |
| `👨‍👩‍👧‍👦`                                   | 1 token: the whole family emoji  | 7 tokens: `👨`, ZWJ, `👩`, ZWJ, `👧`, ZWJ, `👦` |

After composition normalization (default), most combining-mark cases collapse to precomposed single-rune forms, so GraphemeLexer and RuneLexer produce the same stream for almost all Latin, Greek, Cyrillic, and Han/Kana text. The two diverge reliably only for emoji sequences (which have no precomposed forms) and for scripts that have no precomposed forms for their combining sequences (Devanagari conjuncts, some Arabic combinations).

### Swapping the lexer is a config change, not a grammar change

Grammars are written against the `Rule` API and do not know which lexer is driving them. Changing `ParseOptions.InputUnit` swaps the lexer for the whole parse, and the same grammar works either way.

For typical input where each user-visible character is already a single Unicode scalar value (ASCII, precomposed Latin, ordinary CJK, most punctuation), the two lexers produce identical token streams and every rule behaves identically. The rules whose behavior *can* diverge directly are the leaves that inspect token contents: `Token`, `OneOf`, `NoneOf`, and `Literal`. Composite rules, including lookahead wrappers like `Peek` and `Not`, only differ when a leaf inside them sees a different token stream.

Where the two lexers actually diverge, the `RuneLexer` behavior is usually the buggy one: it was matching part of a grapheme as if it were a standalone character. A grammar rule that consumes one rune from `👨‍👩‍👧‍👦` matches just the first 👨 under `RuneLexer` and leaves the other six runes (three ZWJs and three people emoji) dangling for subsequent rules to trip over, which is rarely what the grammar author intended. `GraphemeLexer` avoids this by using `StringInfo` to group the sequence as one text element. The framing is less "`GraphemeLexer` broke my grammar" and more "`GraphemeLexer` revealed that my grammar was silently wrong on multi-rune input." `RuneLexer` is the right tool when you specifically want to see inside a grapheme (walking combining marks individually, rune-level Unicode category analysis, implementing a Unicode library on top of the parser), not for normal text processing.

## Position Tracking

Switching the lexer's token type does not force callers to give up the other position units. The lexer itself advances by UTF-16 char offset because that is the unit a .NET string uses. `ParseResult` and `Symbol.SourceRange` derive the other units from that char index when a caller asks for them:

- **Char index**: UTF-16 code unit offset into the original input (matches `string[i]`, `Substring`, and LSP).
- **Rune index**: code-point offset into the input.
- **Grapheme index**: text-element offset into the input, using the same `StringInfo` logic as `GraphemeLexer`.

The char index is stored on the parse result. Rune and grapheme indexes are computed lazily from the original input, so the common char/line/column path does not pay for counters it never reads.

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

    // The error position bundled into one SourcePosition. Null on success.
    public SourcePosition? ErrorPosition { get; }
}
```

Three fields cover the common cases: `ErrorCharIndex` indexes into the input string directly, `ErrorLine` + `ErrorColumn` give the editor-ready position (in UTF-16 chars, 0-based, following the Language Server Protocol end-to-end. See [InductorParserDesignDecisions.md](InductorParserDesignDecisions.md) "LSP Position Semantics" for the full rationale). The two extra index properties are there for callers that count in runes or graphemes instead. They are computed lazily from the char index the one time they are asked for, so they cost nothing unless used. Column in rune or grapheme units is deliberately not exposed as a field because callers who need it can derive it from the corresponding index cheaply and the combinatorial expansion was not worth it.

`Symbol.SourceRange` reuses the same conversion routines for any node in the parse tree. The leaf's `ReadOnlyMemory<char>` carries an offset back into the input string (recovered via `MemoryMarshal.TryGetString`); a composite walks to its leftmost and rightmost leaves and stitches their ends. The result is a `SourceRange` with `Start` and `End` `SourcePosition`s, each carrying the same five units the error position does. So "where in the source is this symbol?" and "where in the source did the parse fail?" answer in the same vocabulary.

## Encoding Happens First

The parser takes a `string`. Encoding is handled before the parser is ever called. If your document lives on disk as UTF-8, UTF-16, or some legacy codepage, decode it into a `string` with the appropriate `Encoding` class (`File.ReadAllText(path, Encoding.UTF8)`, `Encoding.Unicode.GetString(bytes)`, `Encoding.GetEncoding("Windows-1252").GetString(bytes)`, etc.) before calling `.Parse(...)`. By the time the parser sees the input it is a .NET `string` with no encoding tag. Everything below is about how the lexer iterates those characters.

```
Disk/network                             Caller                                             Parser
-----------                              -----------                                        -----------
UTF-8 bytes    ─→ Encoding.UTF8                        ─→ string (UTF-16) ─→ .Parse(...) ─→ GraphemeLexer ─→ token stream
UTF-16 bytes   ─→ Encoding.Unicode                     ─→ string (UTF-16) ─→ .Parse(...) ─→ GraphemeLexer ─→ token stream
Win-1252 bytes ─→ Encoding.GetEncoding("Windows-1252") ─→ string          ─→ .Parse(...) ─→ token stream
```

Unicode is the character set, a numbered list of characters. UTF-8, UTF-16, and UTF-32 are different ways to represent those numbers as bytes. A document stored as UTF-8 and a document stored as UTF-16 can carry the same Unicode content. They differ only in how the text is laid out on disk. By the time the parser sees a `string` the original on-disk encoding is gone and irrelevant. .NET's `string` type holds Unicode content internally as UTF-16 code units.

If the caller does not know the encoding of a file, they figure it out upstream (BOM sniffing, content-type headers, ask the user) and feed the parser a properly-decoded `string`.

## Normalization

The parser normalizes input to the composed form by default. This is because most people don't want to write their grammars anticipating all the different ways a single Unicode character might be built, they want to depend on the ultimate form of it. Most input is already composed (web content, modern source files, anything produced by normal typing on modern OSes), so the normalization pass is usually a cheap scan with no textual rewrite. It fixes potential bugs for denormalized input when grammars don't account for it.

Unicode defines four normalization forms. The library uses the .NET enum directly to access the Unicode forms (`NormalizationForm.FormC` is composed, `FormD` is decomposed, `FormKC` and `FormKD` are the "compatibility" variants that also fold things like superscripts and ligatures into their canonical equivalents). `FormC` (composed) is what almost every grammar wants.

```csharp
public sealed class ParseOptions
{
    // Default normalization form. Composed form is usual. Set to null to disable.
    public NormalizationForm? NormalizeInput { get; set; } = NormalizationForm.FormC;
    // ... other options
}
```

Callers who want character-exact round-trippability (where `tree.ToString()` must match the original input string character for character) set `NormalizeInput = null`. The tradeoff is that input in the "wrong" form will silently fail exact-match rules that are written for a specific composition.

Positions reported in `ParseResult` (`ErrorCharIndex` and its derived line/column/rune/grapheme properties) are always into the caller's original input string, never into the normalized form. The parser normalizes internally for the lexer to operate on, then translates any failure offset back to original coordinates at the boundary. When normalization is a no-op and .NET returns the original string reference, translation is skipped. When input got rewritten, or when the runtime returns a distinct but equivalent string, the parser maps the failure position back to the original string. This mapping is paid only on failure or budget-abort paths, not on success.

One consequence to know about: when the failure lands inside a combining character sequence that got composed (or vice-versa), the reported position is the start of that sequence in the original string, not a phantom position mid-sequence. That matches what an editor wants for highlight-the-bad-grapheme diagnostics anyway. You can't put a caret between an 'e' and its combining acute in any reasonable UI. This inherits the pre-.NET 5 `StringInfo` caveat noted on `GraphemeLexer`: a handful of real grapheme clusters segment incorrectly on legacy runtimes, and the translator uses the same primitive, so whatever the lexer saw, the translator sees.

## Problems The Lexer Does Not Solve

Some Unicode surprises cannot be fixed by choosing a different tokenization. Both lexers hit them identically: case-insensitive matching beyond ASCII, BOMs, zero-width and invisible format characters, homoglyph confusables, variation selectors. They are caller-side preprocessing concerns or grammar-design concerns, not lexer concerns. See [UnicodeGotchas.md](UnicodeGotchas.md) for the list and the idiomatic workaround for each.

## RuneLexer-Specific: No Grapheme Atom

If you are running under `RuneLexer`, the lexer has deliberately stopped preserving grapheme boundaries. There is no `Grapheme()` leaf that can recover "the next UAX #29 cluster" from the rune stream today. Use `GraphemeLexer` for grammars that need graphemes as the parse unit, or write an explicit rule for the multi-rune sequence you care about.

```csharp
// Match this specific two-rune emoji cluster while running in RuneLexer mode.
var wavedHandWithSkinTone = Token("👋🏽");
```

`WithinGrapheme(innerRule)` solves the opposite problem: while running under `GraphemeLexer`, it reads one outer text-element token and lets the inner rule inspect that token's runes. It does not collect several `RuneLexer` tokens back into a grapheme.

## Open Questions

Three Unicode-adjacent questions the first real grammar will need to answer.

**Unicode version pinning for GraphemeLexer.** Grapheme boundaries are defined by UAX #29, which Unicode updates with every release (new emoji, new ZWJ rules, occasional boundary changes). `GraphemeLexer` uses `StringInfo.GetNextTextElement`, which pulls its behavior from the runtime. Same grammar parsing the same input can produce different trees on different .NET / Unity versions. For most grammars this is tolerable; for a grammar that wants cross-host determinism (a language spec, a shared file format), we would need to bundle our own UAX #29 tables pinned to a specific Unicode version. That is a real maintenance burden to take on but a real need for some callers.

**Full Unicode case-insensitive matching.** The ASCII `LiteralIgnoreAsciiCase` leaf covers HTTP headers, SQL keywords, HTML tag names, and most real needs. A full-Unicode version would handle Turkish dotless-i, German `ß`, Greek final sigma, and the rest of the locale-specific edge cases, at the cost of a big lookup table and locale awareness.

**Grapheme-level character classes.** `OneOf(RuneSet.Letters)` under `GraphemeLexer` matches only single-rune letter graphemes. `Identifier()` already uses `WithinGrapheme(...)` for the broader "start rune plus continuation marks" shape. A first-class factory for "any grapheme whose base rune is in this set, accepting allowed trailing marks" might still be useful for grammars outside identifier syntax, but it would need precise semantics before becoming a built-in.
