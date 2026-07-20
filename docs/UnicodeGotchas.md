# Unicode Gotchas

This doc lists the common gotchas, what goes wrong, and the idiomatic workaround for each. For lexer internals (how tokens are detected, how positions are tracked), see [UnicodeInternalsArchitecture.md](UnicodeInternalsArchitecture.md). Unicode used as a deliberate attack (invisible characters, homoglyph lookalikes, variation selectors, combining-mark tricks) is covered in the security primer, [Primer4.md](Primer4.md), with the attack and the defense for each.

## Identifier Matching

The identifier rule every tutorial writes, a letter then letters or digits, fails on real-world input in two ways. Hand-rolled as `And(OneOf(TokenSet.Letters), ZeroOrMore(OneOf(TokenSet.Letters | TokenSet.Digits)))`, it rejects Hindi `हिन्दी` and Thai `กำ`. In those scripts one visible letter is several runes (a base character plus a vowel mark), the lexer hands that letter back as one multi-rune token, and a rune set like `TokenSet.Letters` never contains a multi-rune token. Letter-or-digit is also the wrong test character by character: it turns away `foo_bar` (underscore is legal after the first character in virtually every language) and lets in the Arabic ligature `ﷺ` (U+FDFA), a letter by Unicode's General_Category that identifier specs exclude on purpose, because its compatibility decomposition is a full multi-word phrase.

**Fix.** Use `Rules.Identifier()`. Matching "an identifier" the way a programming language does is a solved Unicode problem, but each programming language still gets to define its own profile. Unicode Standard Annex #31 (UAX #31), the identifier spec, defines two useful properties, `XID_Start` (can begin an identifier) and `XID_Continue` (can appear after the first character), and the default identifier shape is `XID_Start XID_Continue*`. Python and Rust build on this shape. C#, ECMAScript, Java, and Swift have similar but not identical rules. The parser exposes the UAX #31-shaped rule as `Rules.Identifier()`:

```csharp
var name = Identifier().As("name");
```

That accepts `foo`, `foo_bar`, `café`, `καλημέρα`, `ℼ`, and rejects `2foo`, `_foo` (underscore can continue an identifier but not start one in the base UAX #31 profile), and the ligature `ﷺ`.

It also accepts the Hindi and Thai identifiers the hand-rolled rule rejected. The identifier properties are defined per rune, not per token, so `Identifier()` uses [`WithinToken`](#withintoken-general-purpose-sub-grapheme-matching) internally to look inside each token and check its runes one at a time:

```csharp
Identifier().Parse("हिन्दी");   // matches: Devanagari conjunct as one token
Identifier().Parse("กำ");        // Thai with SARA AM: also matches
Identifier().Parse("καλημέρα"); // Greek: matches
```
To understand why this is necessary, see the comments on IdentifierRule.cs. 

### WithinToken: general-purpose sub-grapheme matching

`WithinToken(innerRule)` is the building block `Identifier()` uses, exposed on its own for other grammar patterns that need to look inside a grapheme. It reads one outer token, runs the inner rule against that token's runes (as a mini rune-lexed stream), and requires the inner rule to consume every rune of the grapheme. Partial matches fail because graphemes are atomic.

Uses beyond identifiers:

```csharp
// Accept any grapheme whose runes are all ASCII letters. Rejects "é"
// (not ASCII) and decomposed "é" (two runes) alike.
var asciiOnlyLetter = WithinToken(OneOf(TokenSet.Ascii.Letters));

// Emoji-with-modifier matcher: one base emoji rune optionally followed
// by skin-tone / ZWJ runes, all as one grapheme.
var emojiCluster = WithinToken(And(
    OneOf(emojiBaseSet),
    ZeroOrMore(OneOf(skinToneOrZwjSet))
));

// Hangul syllable expressed as jamo: leading + medial + optional trailing.
var jamoCluster = WithinToken(And(
    OneOf(leadingJamo),
    OneOf(medialJamo),
    Optional(OneOf(trailingJamo))
));
```

Caveats: the inner rule runs against a fresh sub-lexer that doesn't share trace state with the outer lexer (so trace output from the inner doesn't show up in the outer trace), but does inherit the caller's budget limits (`MaxDepth`, `RuleCountLimit`, `Timeout`, `Cancellation`) so a recursive inner rule on a cluster with many combining marks can't crash the host with a stack overflow. It's bounded to the current token's span, so ordinary character-consuming rules stay tiny. Inner-rule symbols are discarded. `WithinToken` emits one leaf per grapheme to the outer tree.

### Matching specific languages

`Identifier` takes two optional `TokenSet` parameters, `extraStartRunes` and `extraBodyRunes`, that get unioned into `XID_Start` and `XID_Continue` respectively. UAX #31 calls this a "profile." Combined with the normalization form chosen at `Compile` time, these cover the real-world identifier rules of most languages that are built on UAX #31.

Strict UAX #31 (the reference spec, no language-specific additions). No mainstream language ships this exact profile (nearly all add at least the underscore), so treat it as the baseline the recipes below extend.

```csharp
Identifier();  // base UAX #31-style form
```

Python 3 identifiers, per [PEP 3131](https://peps.python.org/pep-3131/) and the [Language Reference](https://docs.python.org/3/reference/lexical_analysis.html#identifiers). Python adds `_` to Start and uses NFKC (not NFC) for equivalence.

```csharp
var python = Identifier(extraStartRunes: TokenSet.Runes("_"))
    .Compile(NormalizationForm.FormKC);
var result = python.Parse(input);
```

The normalization form lives on `Compile`. `Identifier` defers its form-aware expansion of `XID_Start` and `XID_Continue` to Compile time, so compatibility equivalents (ligatures, fullwidth Latin, math-bold) whose NFKC conversion is a multi-grapheme sequence get expanded into their grapheme pieces by `IdentifierRule` before the form-validation pass runs.

Rust identifiers, per the [Rust Reference](https://doc.rust-lang.org/reference/identifiers.html). Adds `_` to Start the same way Python 3 does, but the form differs: Rust normalizes identifiers with NFC, not NFKC ([RFC 2457](https://rust-lang.github.io/rfcs/2457-non-ascii-idents.html)), so the form on `Compile` is `FormC`, not `FormKC`. That difference is real. Under NFC, Rust keeps compatibility-distinct spellings apart where Python 3's NFKC merges them: the `ﬁ` ligature stays separate from `fi`, fullwidth `ｆｏｏ` stays separate from `foo`, and a character whose only identifier-shaped form is its NFKC expansion (the circled digit `①`, category No, not an identifier character on its own) is rejected rather than quietly turned into `1`. One Rust-specific rule this recipe does **not** enforce: Rust rejects bare `_` as an identifier, requiring `_ XID_Continue+`. If you need that, wrap the rule in an explicit check for the second character. For most grammars the practical difference is negligible.

```csharp
var rust = Identifier(extraStartRunes: TokenSet.Runes("_"))
    .Compile(NormalizationForm.FormC);
var result = rust.Parse(input);
```

ECMAScript-style identifiers (JavaScript, TypeScript), per [ECMA-262 §12.7](https://tc39.es/ecma262/#sec-names-and-keywords). The shape is close (add `_` and `$` to both positions, no normalization), but note two caveats. ECMAScript officially uses `ID_Start` and `ID_Continue`, not the X variants. The parser only exposes the XID sets, which are a strict subset, so this recipe accepts slightly less than a spec-conformant JS engine would. The difference is a couple dozen exotic code points that almost never appear in real source. ECMAScript also allows ZWNJ (U+200C) and ZWJ (U+200D) after the first character, which this recipe doesn't cover.

```csharp
var ecmascript = Identifier(
    extraStartRunes: TokenSet.Runes("_$"),
    extraBodyRunes: TokenSet.Runes("$"))    // "_" is already in XID_Continue
    .Compile(null);
var result = ecmascript.Parse(input);
```

C# identifiers, per [ECMA-334 §6.4.3](https://www.ecma-international.org/publications-and-standards/standards/ecma-334/). C# allows `_` in Start and uses category-based rules rather than XID directly. For grammars, `Identifier(extraStartRunes: TokenSet.Runes("_"))` with default NFC is a close approximation for ordinary source. It isn't a spec-exact C# lexer.

Java identifiers use `Character.isJavaIdentifierStart` and `Character.isJavaIdentifierPart`, which are their own rule, not reproducible via `Identifier` parameters alone. A Java-conforming grammar would compose against a custom `TokenSet` built from those predicates.

Swift has its own enumerated list of ranges that resembles XID but isn't a property reference, so it isn't reproducible via `Identifier` parameters alone either.

If you're restricting to a specific script for security reasons (mixed-script phishing, homoglyph attacks), see the "Homoglyphs" section of the security primer, [Primer4.md](Primer4.md#homoglyphs). `Identifier()` is the general-purpose match, not a script-restricted one.

## Case-Insensitive Matching Beyond ASCII

The `LiteralIgnoreAsciiCase` leaf does ASCII case-insensitive matching (A ↔ a) and is all most grammars need. Full Unicode case-insensitive matching has script-specific surprises the lexer doesn't handle: German `ß` uppercases to `SS` (one character becomes two), Turkish has dotted-i and dotless-i as distinct letters, Greek capital sigma (Σ) lowercases to final sigma (ς) at the end of a word and to regular sigma (σ) everywhere else. The leaf is ASCII-only on purpose. Extending it to full Unicode is a much bigger piece of work that was out of scope for now.

**Fix.** For ASCII keywords, the built-in leaf is the cheap path:

```csharp
public static readonly Rule SelectKeyword = LiteralIgnoreAsciiCase("select");
```

When a grammar genuinely needs case-insensitive matching of non-ASCII text, do it with the real algorithm. The Unicode Standard defines full case-insensitive matching in section 5.18 "Case Mappings" of the core spec (the operation it calls caseless matching), and .NET ships the tables: `CompareInfo.IsPrefix(remaining, pattern, CompareOptions.IgnoreCase, out int matchLength)` (.NET 5+) answers "does the input start with this pattern, ignoring case, and how many chars did it consume". A user-defined `Rule` subclass can build a leaf on that primitive (see the `Rule` class docs for how to subclass).

`CompareInfo` is .NET's culture-aware string comparer, and the culture you take it from decides which characters count as the same letter in different cases. Take it from `CultureInfo.InvariantCulture` by default. Those rules are culture-neutral and identical on every machine. Don't use the machine's current culture, or the same grammar parses differently depending on the OS language of whoever runs it. The one reason to pick a specific culture is a grammar for that language's text. Turkish is the classic example: it pairs `i` with dotted `İ` and dotless `ı` with `I`, so a case-insensitive Turkish keyword only matches correctly with the Turkish culture's `CompareInfo`, while the invariant culture pairs `i` with `I` the English way.

## BOM At File Start

Byte Order Mark, U+FEFF. Windows tools and some editors prepend it to text files as an encoding hint, so the first character of the file is invisible but present. A grammar that expects the file to start with a specific keyword like `<?xml` or `function` sees the BOM as an unexpected character at position 0 and fails to match. Both lexers see it identically because the BOM is a single rune and a single grapheme.

**Fix.** The caller strips it before parsing:

```csharp
var cleaned = input.TrimStart('\uFEFF');
var result = grammar.Parse(cleaned);
```

## CRLF Line Endings

Unicode text segmentation treats `\r\n` as a single grapheme cluster (UAX #29 rule GB3), so the lexer hands the parser one two-char token whenever it sees a Windows line ending. This breaks any line-based grammar that tries to match or stop on a bare `\n`:

- `Token('\n')` matches one token whose text is exactly `\n`. The CRLF token's text is `\r\n`. No match.
- `OneOf(TokenSet.Runes("\n"))` matches one token that's a single rune from the set. The CRLF token is two runes, so it's never in a rune set. No match.
- `NoneOf(TokenSet.Runes("\n"))` fails the other way around: CRLF isn't in the set, so `NoneOf` *matches* it. A scan like `ZeroOrMore(NoneOf(...))` that's supposed to stop at the line break swallows the CRLF as content, and the line-terminator rule that was supposed to match next finds it already eaten.

**Fix.** Use the built-in `EndOfLine()` rule. It's `Or(Literal("\r\n"), OneOf(TokenSet.LineTerminators))` under the hood, so the CRLF token is tried as a unit before the single-rune terminators (LF, CR, VT, FF, NEL, LINE SEPARATOR, PARAGRAPH SEPARATOR per UTS #18 §1.6, RL1.6). Pass `eofIsEol: true` for the "line terminator here, or end of input" case, and wrap with `Optional` for "line terminator here, or none at all". Anywhere a grammar cares about line breaks, use these instead of building one with `Token('\n')` or a `OneOf` over a rune set:

```csharp
// Match a Unicode line terminator (CRLF, LF, CR, NEL, LS, PS, VT, FF).
And(..., EndOfLine())

// Whitespace that includes newlines: AnyWhitespace() is the built-in
// for this, and it composes EndOfLine() first so the CRLF pair commits
// before the single-rune fallbacks pick up the '\r' alone.
public static readonly Rule WhitespaceOrNewline = Optional(AnyWhitespace());

// Strict intra-line whitespace (rejects every line terminator,
// including CRLF) is InlineWhitespace().
public static readonly Rule HorizontalSpace = Optional(InlineWhitespace());

// Scanning "up to end of line": use a rule-based stop with Not(EndOfLine()).
// NoneOf over a single-rune set would silently eat the CRLF token.
public static readonly Rule LineComment = And(
    Token('%'),
    ZeroOrMore(And(Not(EndOfLine()), AnyToken())),
    EndOfLine(eofIsEol: true)
);
```

The three anti-patterns to avoid in any line-based grammar:

```csharp
// BROKEN on Windows line endings.
And(..., Token('\n'))                             // fails on CRLF input
OneOf(TokenSet.Runes("\n"))                         // CRLF is two runes, never in a rune set
ZeroOrMore(NoneOf(TokenSet.Single('\n')))           // swallows the CRLF terminator
```

If a grammar is a port of regex semantics that explicitly targets LF-only (some Markdown-style formats, for instance), the failure on CRLF is faithful to the source and you can leave `Token('\n')` as-is. Mark the grammar with a comment so the next reader knows the LF-only behavior is intentional, not an oversight.

## Lone Surrogates: `NoneOf` Admits Them Under `Compile(null)`

A lone surrogate is an unpaired UTF-16 code unit in U+D800..U+DFFF, the kind you get from truncated or malformed UTF-16 (a high surrogate with no low surrogate after it). It isn't a Unicode scalar value, so it can't be a member of any `TokenSet` you build with `Single` / `Range` / `Runes` (those reject surrogate arguments). The only way one enters a set is through the explicit `TokenSet.Surrogates` constant or `TokenSet.SurrogateRange`.

Under the default `Compile(NormalizationForm.FormC)` you never see this, because .NET's `string.Normalize` rejects malformed UTF-16. `Parse` normalizes before the lexer runs, so any input with a lone surrogate fails the parse with a `MalformedInput` outcome before tokenization (the parser catches the rejection and reports it as a result you can localize, rather than letting an `ArgumentException` escape). The gotcha only shows up under `Compile(null)`, which skips normalization and lets the lexer surface a lone surrogate as a one-char token (with no scalar value).

When that token reaches the parser, `NoneOf(set)` admits it:

```csharp
string loneSurrogate = "\uD800";   // the \uD800 escape is plain ASCII in source (a raw pasted surrogate could get mangled to U+FFFD)

// NoneOf is a direct non-membership test: the lone surrogate isn't in
// Letters, so NoneOf admits it.
NoneOf(TokenSet.Letters).Compile(null).Parse(loneSurrogate).Success;   // True
```

`NoneOf(set)` matches any token whose value isn't in `set`. A lone surrogate isn't in `set`, so it passes.

**Fix.** Decide whether you actually want lone surrogates. If you're sweeping raw or possibly-malformed content under `Compile(null)` (WTF-8 round-tripping, lenient handling of unpaired surrogates), `NoneOf` admitting them is usually exactly what you want, so leave it. If you want to exclude them, match against the surrogate-free scalar universe instead: `OneOf(TokenSet.Universe - stopSet)`. `Universe` leaves out the surrogate block, so a lone surrogate isn't a member and the rule won't match it. And remember it only matters under `Compile(null)`: the default `FormC` compile rejects the malformed input upstream, before the rule runs.


## Token Segmentation Across Runtimes

This one is different from the gotchas above, and it's mostly a non-gotcha now. It used to be the runtime behaving differently depending on which .NET you're on.

The rule the parser follows is that each build's Unicode environment stays internally consistent. The net8.0 build takes token boundaries from the runtime's `StringInfo`, the same place `string.Normalize` and the character-category data come from, so everything Unicode moves together at whatever version the runtime ships. Unity's runtime is the exception, and the problem is simple staleness: its `StringInfo` predates UAX #29, so it segments modern text wrong, which misparses real input and fails hundreds of the library's grapheme tests. So the netstandard2.1 build, the one Unity's IL2CPP player loads, ships its own segmenter instead: [GraphemeSegmentation.cs](../src/InductorParser/Lexing/GraphemeSegmentation.cs), a port of the MIT-licensed implementation inside .NET 8's `StringInfo`, with its break-property table generated from the Unicode 15.0 data files. That's an opt-in build option enabled by definining the symbol (`INDUCTORPARSER_USE_BUNDLED_SEGMENTATION`), not a hard requirement: build the netstandard2.1 target without it and the library still loads and runs on Unity, it just tokenizes by the legacy rules and all those tests fail again. 

What the legacy rules get wrong, concretely: on .NET Framework, .NET Core 3.x, and the Mono runtime Unity ships (which IL2CPP compiles from), `StringInfo` uses an algorithm Microsoft wrote before UAX #29 stabilized, roughly "Unicode 3.x grapheme cluster": base plus combining marks, surrogate pairs, Hangul basics. On those runtimes emoji ZWJ sequences split at every ZWJ, skin-toned emoji split in two, flag pairs split, Thai SARA AM split, and CRLF split into two tokens (the legacy walker predates rule GB3). With the bundled segmenter, none of that reaches the parser: `Token(...)` and `Graphemes(...)` validation, the lexer, and error positions all answer from the same UAX #29 implementation.

What's left to know:

- The bundled segmenter implements UAX #29 revision 35 at Unicode 15.0, deliberately the same revision and data version as .NET 8's `StringInfo`. That makes the two directly comparable, and the differential tests in [GraphemeSegmentationTests.cs](../src/InductorParser.Tests/Lexing/GraphemeSegmentationTests.cs) hold them equal across the whole corpus, every code point, and randomized sequences. So today the two builds also agree with each other, and they only drift apart once a newer runtime's Unicode data moves past 15.0.
- Rule GB9c (Indic conjunct clusters) arrived in Unicode 15.1 and the bundled segmenter doesn't implement it, matching .NET 8. The conformance suite tracks the seven affected test lines in its `KnownRuntimeSkips`.
- Upgrading the bundled segmenter's Unicode version is a deliberate step: regenerate [GraphemeSegmentation.Data.cs](../src/InductorParser/Lexing/GraphemeSegmentation.Data.cs) from newer UCD files (`GraphemeSegmentationDataTests.cs` verifies it and can re-emit it), add any new rules like GB9c to the processor, and swap in the newer `GraphemeBreakTest` data file. When a newer runtime becomes the test target, the differential tests fail on exactly the diverging cases, which is the prompt to do this.
- Which build uses which implementation is one symbol in `InductorParser.csproj`: `INDUCTORPARSER_USE_BUNDLED_SEGMENTATION` opts a build into the bundled segmenter, and only the netstandard2.1 build defines it. The default is the runtime's `StringInfo`, so a newly added target framework tracks its own runtime without extra wiring. Building from source you can define the symbol for every build when identical boundaries across mixed runtimes matter more than tracking the runtime. Don't take it off the Unity build, though: Unity's `StringInfo` is the legacy one this whole section is about.
