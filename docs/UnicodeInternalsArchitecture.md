# Unicode Internals Architecture

This doc is about how the parser handles Unicode text at its lowest levels. Read this if you want to understand what the parser sees when you feed it a string, or debug a Unicode-related issue. If you just want to write grammars, start with [primer1.md](primer1.md), [primer2.md](primer2.md), [primerFailure.md](primerFailure.md), [Primer3.md](Primer3.md), and [Primer4.md](Primer4.md), then use [InductorParserReference.md](InductorParserReference.md) as the full API reference.

## Unicode In One Page

[If you're already well-versed in Unicode, skip this section]

Unicode has a bunch of concepts a parser could engage with. They fall into three categories, and it helps to separate them because they aren't a single stack: they're a stack (representation levels), a set of orthogonal operations (text transformations), and a set of downstream algorithms (text analysis).

### Unicode Representation Hierarchy

You need to pick one as the parser's token. Here is the stack, from the lowest physical layer up, with each layer built from one or more of the layer below:

- **Code units** (physical encoding): the fixed-width pieces a string is stored as. UTF-16 uses 16-bit code units. UTF-8 uses 8-bit code units (bytes). In .NET, `string` is a sequence of UTF-16 code units and `char` holds one code unit. One Unicode code point (see next layer) can span multiple code units (surrogate pairs in UTF-16, multi-byte sequences in UTF-8). In UTF-16 one code point is one or two code units. In UTF-8 one code point is one to four code units.
- **Code points** (the atoms of Unicode): a code point is a number in the Unicode code space: 0 to 0x10FFFF. The code points that are valid standalone characters are called *scalar values*; they exclude UTF-16 surrogate halves (U+D800..U+DFFF). Scalar values are what .NET's `System.Text.Rune` holds, and the parser uses the term *rune* for them throughout.
- **Grapheme clusters** (human-perceived characters): built from one or more code points via UAX #29 rules. A grapheme is what a human perceives as one character. It can be a single code point, like `p`. Also valid: `é` as `e` + "combining acute" is one grapheme built from two code points. 👨‍👩‍👧‍👦 is one grapheme built from seven code points. 👋🏽 is one grapheme built from two code points. The parser calls these *tokens* because each one is what the lexer hands back per `Read()` call.

Each layer is a composition over the one below, so any string has a code-unit count, a code-point count, and a token count, and the counts only diverge when the composition is non-trivial. Some examples:
- For ASCII, all three are equal.
- For text that stays inside the first 65,536 code points (U+0000..U+FFFF) with no combining marks, all three are still equal.
- For a rune above U+FFFF with no modifier (a lone 🎸, say), UTF-16 uses a surrogate pair so the code-unit count doubles while the code-point and token counts stay the same.
- For combining-mark text or emoji sequences (👋🏽, 👨‍👩‍👧‍👦), multiple code points form one token, so the token count falls below the code-point count.

### Four Ways Code Points Build A Grapheme

Every grapheme ends up the same, one user-visible character, but Unicode has four different patterns for building one out of code points:

1. **Single code point graphemes**: like `A` (all of ASCII) have a single rune that represents them (U+0041) and that's the only way you'll ever see them. (Not a formal Unicode term, since Unicode doesn't have a name for this category.)

2. **Combining character sequences**: like `क्ष` don't have a single defining code point but are always represented as the same sequence (U+0915 क, U+094D ्, U+0937 ष).

3. **Canonically equivalent sequences**: characters that can be represented like #1 *or* #2. Like `é`: it has a single rune that represents it (U+00E9) *and* can be represented by a sequence of runes that build the character (U+0065 e, U+0301 combining acute). Unicode says both spellings represent the same thing and should display identically. Normalization (below) is what reconciles them.

4. **Emoji sequences**: "Lego blocks" of graphemes that can be built out of combinations of characters using certain rules. They're an open-ended and growing set, like `👨‍👩‍👧` (U+1F468 👨, U+200D ZWJ, U+1F469 👩, U+200D ZWJ, U+1F467 👧). These are more like a programming language for building graphemes.

All four patterns end at the same place: one character a reader perceives, which the lexer hands back as one token. That's why the parser's rules are designed around graphemes. Note that some code points and code point sequences don't represent a user-perceived character at all (a stray combining accent, an invisible format character). The valid ones split by the same UAX #29 rules and surface as ordinary tokens (see [Primer3.md](Primer3.md) "Unexpected Unicode" for the survey). Ill-formed ones (an unpaired surrogate) fail the parse with a `MalformedInput` result before any rule runs.

### Text transformations (orthogonal)

Rewrites that produce a different rune sequence. These apply to runes, they aren't a higher layer.

- **Normalization**: canonical rewrites so that visually-identical text compares equal regardless of what the bytes look like. The "é" in "café" as one rune or in "café" as two runes (`e` + "combining accent") are different rune sequences but the same normalized text. There are four normalization forms defined by Unicode. The parser uses the *composed* form by default (the one that produces U+00E9 `é` as a single code point rather than `e` + combining acute). See the Normalization section below.
- **Case-insensitive matching (Unicode)**: treating upper and lower case as equivalent across the full Unicode range. Not the same as `ToLower`: German `ß` pairs with `ss`, Turkish dotless-i behaves differently from dotted i, Greek final sigma pairs with regular sigma. The parser doesn't apply this by default. See the Workarounds section.

### Downstream algorithms (not parser concerns)

These operate on text that has already been parsed or on raw text as standalone operations.

- **Collation**: locale-aware sort order.
- **Bidi**: visual ordering for mixed right-to-left and left-to-right text.
- **Line breaking** (UAX #14) and **word segmentation** (UAX #29): where you're allowed to break a paragraph into lines or split it into words.

Nothing in this doc engages with these. They run outside the parser, on the parsed output or on raw text through a dedicated Unicode library.

## The Lexer

The parser has one lexer. It walks the input one .NET text element at a time using `System.Globalization.StringInfo.GetNextTextElement` and hands back one `Token` per `Read()` call. On modern .NET this follows UAX #29 extended-grapheme-cluster behavior. Older .NET / Unity Mono runtimes have known segmentation gaps covered in [UnicodeGotchas.md](UnicodeGotchas.md#pre-net-5-token-segmentation). Each token usually matches what a user would perceive as a single character.

```
Input:  "🎸 = 👋🏽;"
Stream: [🎸] [ ] [=] [ ] [👋🏽] [;]    (6 tokens)
```

A token can be one rune (ASCII, composed-form Latin, most punctuation) or several runes that combine into one human-perceived character (skin-toned emoji, ZWJ family emoji). The rules that compare against tokens (`Token`, `Literal`, `OneOf`, `NoneOf`, `AnyToken`) treat each token as one unit, so a grammar written against them doesn't have to know whether the user-typed character at this position is one code point or seven.

The lexer runs ASCII fast paths inside `AdvanceUntilRuneIn` and `AdvanceWhileRuneIn` for the common case where a rule is scanning over a rune-only `TokenSet` and most of the input is plain ASCII. Those helpers fall through to the general per-token walk when the set has multi-rune entries, when the input contains non-ASCII text, or when the scan needs to inspect token content beyond a single-rune membership check. There's no separate lexer for the rune-level case. One token-aware lexer covers both shapes by switching helpers based on what the rule asked for.

When a rule needs to look *inside* one token (inspect combining marks individually, walk the runes of a grapheme), it uses the `WithinToken(innerRule)` rule. `WithinToken` runs an internal sub-lexer that walks one rune per `Read()`, bounded to the runes of the outer token. The inner rule must consume every rune of the token. Outside `WithinToken`, every rule sees the same one-token-per-`Read()` stream.

## Position Tracking

The lexer indexes by UTF-16 char offset because that's the unit a .NET string uses. `ParseResult` and `Symbol.SourceRange` derive other position units from that char index when a caller asks for them:

- **Char index**: UTF-16 code unit offset into the original input (matches `string[i]`, `Substring`, and LSP).
- **Token index**: text-element offset into the input, using the same `StringInfo` logic the lexer uses.
- **Line and column**: zero-based, LSP convention. Column is in chars.

The char index is stored on the parse result. The token index is computed lazily from the original input, so the common char/line/column path doesn't pay for a counter it never reads.

`ParseResult` exposes the minimum useful set:

```csharp
public readonly struct ParseResult
{
    public int  ErrorCharIndex   { get; }   // UTF-16 char index; use for input[...]
    public int  ErrorLine        { get; }   // 0-based line number (LSP)
    public int  ErrorCharColumn  { get; }   // 0-based column in UTF-16 chars (LSP)

    // For callers that count in tokens. Derived from the char index.
    public int  ErrorTokenIndex  { get; }

    // The error position bundled into one SourcePosition. Null on success.
    public SourcePosition? ErrorPosition { get; }
}
```

Three fields cover the common cases: `ErrorCharIndex` indexes into the input string directly, `ErrorLine` + `ErrorCharColumn` give the editor-ready position (in UTF-16 chars, 0-based, following the Language Server Protocol end-to-end. See [InductorParserDesignDecisions.md](InductorParserDesignDecisions.md) "LSP Position Semantics" for the full rationale). `ErrorTokenIndex` is there for callers whose mental model counts tokens (the `^^^` underline a human will look at and recognize as covering one thing). It is computed lazily from the char index the one time it's asked for, so it costs nothing unless used.

`Symbol.SourceRange` reuses the same conversion routines for any node in the parse tree. The leaf's `ReadOnlyMemory<char>` carries an offset back into the input string (recovered via `MemoryMarshal.TryGetString`). A composite walks to its leftmost and rightmost leaves and stitches their ends. The result is a `SourceRange` with `Start` and `End` `SourcePosition`s, each carrying `CharIndex`, `TokenIndex`, `Line`, and `CharColumn`. So "where in the source is this symbol?" and "where in the source did the parse fail?" answer in the same vocabulary.

## Encoding Happens First

The parser takes a `string`. Encoding is handled before the parser is ever called. If your document lives on disk as UTF-8, UTF-16, or some legacy codepage, decode it into a `string` with the appropriate `Encoding` class (`File.ReadAllText(path, Encoding.UTF8)`, `Encoding.Unicode.GetString(bytes)`, `Encoding.GetEncoding("Windows-1252").GetString(bytes)`, etc.) before calling `.Parse(...)`. By the time the parser sees the input it is a .NET `string` with no encoding tag. Everything below is about how the lexer iterates those characters.

```
Disk/network                             Caller                                             Parser
-----------                              -----------                                        -----------
UTF-8 bytes    ─→ Encoding.UTF8                        ─→ string (UTF-16) ─→ .Parse(...) ─→ Lexer ─→ token stream
UTF-16 bytes   ─→ Encoding.Unicode                     ─→ string (UTF-16) ─→ .Parse(...) ─→ Lexer ─→ token stream
Win-1252 bytes ─→ Encoding.GetEncoding("Windows-1252") ─→ string          ─→ .Parse(...) ─→ Lexer ─→ token stream
```

Unicode is the character set, a numbered list of characters. UTF-8, UTF-16, and UTF-32 are different ways to represent those numbers as bytes. A document stored as UTF-8 and a document stored as UTF-16 can carry the same Unicode content. They differ only in how the text is laid out on disk. By the time the parser sees a `string` the original on-disk encoding is gone and irrelevant. .NET's `string` type holds Unicode content internally as UTF-16 code units.

If the caller doesn't know the encoding of a file, they figure it out upstream (BOM sniffing, content-type headers, ask the user) and feed the parser a properly-decoded `string`.

## Normalization

The parser normalizes input to the composed form by default. This is because most people don't want to write their grammars anticipating all the different ways a single Unicode character might be built, they want to depend on the ultimate form of it. Most input is already composed (web content, modern source files, anything produced by normal typing on modern OSes), so the normalization pass is usually a cheap scan with no textual rewrite. It fixes potential bugs for denormalized input when grammars don't account for it.

Unicode defines four normalization forms. The library uses the .NET enum directly to access the Unicode forms (`NormalizationForm.FormC` is composed, `FormD` is decomposed, `FormKC` and `FormKD` are the "compatibility" variants that also convert things like superscripts and ligatures into their canonical equivalents). `FormC` (composed) is what almost every grammar wants.

The form is a grammar-level decision committed at `Compile` time, not a per-parse option. Pick it once when you compile the grammar:

```csharp
var grammar = And(...).Compile();                              // FormC default
var grammar = And(...).Compile(NormalizationForm.FormKC);      // explicit FormKC
var grammar = And(...).Compile(null);                          // no normalization
```

Why grammar-level instead of per-parse: the moment you write `Token("é")` you've committed to a specific Unicode form for that literal. If a later `Parse` ran the grammar against decomposed input under FormD, the lexer would hand back the two-rune `e + U+0301` form and your `Token("é")` rule (looking for the single-rune U+00E9) would silently never match. The form is part of the grammar's identity, so it lives on the grammar, not the call.

`Compile(form)` converts every literal-bearing rule's stored text (`Token`, `Literal`, `LiteralIgnoreAsciiCase`) into the chosen form in place, so a literal typed in a different form still matches the normalized input. A literal that can't be represented in the form (an unpaired surrogate that `string.Normalize` rejects, or a single-grapheme slot whose conversion produces more than one grapheme, like the ligature `ﬁ` becoming the two-grapheme "fi" under `FormKC`) gets reported in a single `InvalidOperationException` listing every offender, so the author fixes them all in one pass. The pass is skipped when the form is `null`.

When you need the exact characters the user typed back out, read `Symbol.SourceText` (or just keep the string you passed to `Parse`). `SourceText` returns the verbatim original section a rule matched no matter which form the grammar compiled under, because it translates the match back to the original input. `tree.ToString()` isn't the verbatim accessor: it rebuilds text only from the nodes left in the tree, so it drops whatever the `Delete` rules matched (and `Token` / `Literal` default to `Delete`), and under a normalizing form the characters it does keep come back normalized. What `Compile(null)` buys you is that the tree's leaf text (and so `ToString()` on a grammar that preserves the content it matches) carries the original characters instead of the normalized ones. The tradeoff is that input in the "wrong" form will silently fail exact-match rules that are written for a specific composition.

Positions reported in `ParseResult` (`ErrorCharIndex` and its derived line/column/token properties) are always into the caller's original input string, never into the normalized form. The parser normalizes internally for the lexer to operate on, then translates any failure offset back to original coordinates at the boundary. When normalization is a no-op and .NET returns the original string reference, translation is skipped. When input got rewritten, or when the runtime returns a distinct but equivalent string, the parser maps the failure position back to the original string. This mapping is paid only on failure or budget-abort paths, not on success.

One consequence to know about: when the failure lands inside a combining character sequence that got composed (or vice-versa), the reported position is the start of that sequence in the original string, not a phantom position mid-sequence. That matches what an editor wants for highlight-the-bad-token diagnostics anyway. You can't put a caret between an 'e' and its combining acute in any reasonable UI. This inherits the pre-.NET 5 `StringInfo` caveat: a handful of real grapheme clusters segment incorrectly on legacy runtimes, and the translator uses the same primitive, so whatever the lexer saw, the translator sees.

## Problems The Lexer Doesn't Solve

Some Unicode surprises can't be fixed by the lexer's tokenization. Case-insensitive matching beyond ASCII, BOMs, zero-width and invisible format characters, homoglyph confusables, variation selectors. They are caller-side preprocessing concerns or grammar-design concerns, not lexer concerns. See [UnicodeGotchas.md](UnicodeGotchas.md) for the list and the idiomatic workaround for each.

## Going Below The Token: WithinToken

When a grammar needs to walk the runes inside one token (inspect combining marks individually, validate that every rune of a grapheme satisfies some predicate, match a specific multi-rune cluster shape), it uses the `WithinToken(innerRule)` factory. The outer parse reads one full token. The inner rule then runs against a sub-lexer that walks one rune per `Read()`, bounded to the runes of that one token. The inner rule must consume every rune of the token. Partial matches fail.

```csharp
// Match this specific two-rune emoji cluster as one token.
var wavedHandWithSkinTone = Token("👋🏽");

// Or, walk the runes inside whatever token is at the cursor.
var asciiOnlyLetter = WithinToken(OneOf(TokenSet.Ascii.Letters));
```

`Token(string)` covers the common case where the grammar knows the exact multi-rune cluster it wants. `WithinToken` is the escape hatch for "any token whose runes match this pattern," which is what `Identifier()` uses internally to handle Devanagari and Thai conjunct letters. See [UnicodeGotchas.md](UnicodeGotchas.md#withintoken-general-purpose-sub-grapheme-matching) for more `WithinToken` recipes.

## Open Questions

Three Unicode-adjacent questions the first real grammar will need to answer.

**Fixing the Unicode version for the lexer.** Token boundaries are defined by UAX #29, which Unicode updates with every release (new emoji, new ZWJ rules, occasional boundary changes). The lexer uses `StringInfo.GetNextTextElement`, which pulls its behavior from the runtime. Same grammar parsing the same input can produce different trees on different .NET / Unity versions. For most grammars this is tolerable. For a grammar that wants cross-host determinism (a language spec, a shared file format), we would need to bundle our own UAX #29 tables fixed to a specific Unicode version. That is a real maintenance burden to take on but a real need for some callers.

**Full Unicode case-insensitive matching.** The ASCII `LiteralIgnoreAsciiCase` leaf covers HTTP headers, SQL keywords, HTML tag names, and most real needs. A full-Unicode version would handle Turkish dotless-i, German `ß`, Greek final sigma, and the rest of the locale-specific edge cases, at the cost of a big lookup table and locale awareness.

**Grapheme-level character classes with marks.** `OneOf(TokenSet.Letters)` matches single-rune letter tokens. Multi-rune letters (Devanagari conjuncts, decomposed-form combinations with no precomposed equivalent) don't match because a single-rune set isn't satisfied by a multi-rune token. `Identifier()` already uses `WithinToken(...)` internally for the broader "start rune plus continuation marks" shape. A first-class factory for "any token whose base rune is in this set, accepting allowed trailing marks" might still be useful for grammars outside identifier syntax, but it would need precise semantics before becoming a built-in.
