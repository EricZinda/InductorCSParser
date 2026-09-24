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

Rust identifiers, per the [Rust Reference](https://doc.rust-lang.org/reference/identifiers.html). Adds `_` to Start the same way Python 3 does, but the form differs: Rust normalizes identifiers with NFC, not NFKC ([RFC 2457](https://rust-lang.github.io/rfcs/2457-non-ascii-idents.html)), so the form on `Compile` is `FormC`, not `FormKC`. That difference is real. Under NFC, Rust keeps compatibility-distinct spellings apart where Python 3's NFKC merges them: the `ﬁ` ligature stays separate from `fi`, fullwidth `ｆｏｏ` stays separate from `foo`, and a character whose only identifier-shaped form is its NFKC expansion (the circled digit `①`, category No, not an identifier character on its own) is rejected rather than quietly turned into `1`. One Rust-specific rule this recipe does **not** enforce: Rust rejects bare `_` as an identifier (the Reference's token grammar accepts it, then excludes it by listing `_` among the keywords). If you need that, wrap the rule in an explicit check for the second character. For most grammars the practical difference is negligible.

```csharp
var rust = Identifier(extraStartRunes: TokenSet.Runes("_"))
    .Compile(NormalizationForm.FormC);
var result = rust.Parse(input);
```

ECMAScript-style identifiers (JavaScript, TypeScript), per [ECMA-262 §12.7](https://tc39.es/ecma262/#sec-names-and-keywords). The shape is close (add `_` and `$` to both positions, no normalization), but note two caveats. ECMAScript officially uses `ID_Start` and `ID_Continue`, not the X variants. The parser only exposes the XID sets, which are a strict subset, so this recipe accepts slightly less than a spec-conformant JS engine would. The difference is a couple dozen exotic code points that almost never appear in real source. ECMAScript also allows ZWNJ (U+200C) and ZWJ (U+200D) after the first character. This recipe used to miss them, and older ECMAScript editions listed them as explicit extras beyond the identifier properties, but Unicode 15.1 added both to `Other_ID_Continue`, so they now come with `XID_Continue` for free (and the current spec draft dropped the explicit mention for the same reason).

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

When a grammar genuinely needs case-insensitive matching of non-ASCII text, do it with the real algorithm. The Unicode Standard defines full case-insensitive matching in [section 5.18 "Case Mappings" of the core spec](https://www.unicode.org/versions/latest/core-spec/chapter-5/) (the operation it calls caseless matching), and .NET ships the tables: `CompareInfo.IsPrefix(remaining, pattern, CompareOptions.IgnoreCase, out int matchLength)` (.NET 5+) reports whether the input starts with the pattern ignoring case, and how many chars the match consumed. A user-defined `Rule` subclass can build a leaf on that primitive (see the `Rule` class docs for how to subclass).

`CompareInfo` is .NET's culture-aware string comparer, and the culture you take it from decides which characters count as the same letter in different cases. Take it from `CultureInfo.InvariantCulture` by default. Those rules are culture-neutral and identical on every machine. Don't use the machine's current culture, or the same grammar parses differently depending on the OS language of whoever runs it. The one reason to pick a specific culture is a grammar for that language's text. Turkish is the classic example: it pairs `i` with dotted `İ` and dotless `ı` with `I`, so a case-insensitive Turkish keyword only matches correctly with the Turkish culture's `CompareInfo`, while the invariant culture pairs `i` with `I` the English way.

## BOM At File Start

Byte Order Mark, U+FEFF. Windows tools and some editors prepend it to text files as an encoding hint, so the first character of the file is invisible but present. A grammar that expects the file to start with a specific keyword like `<?xml` or `function` sees the BOM as an unexpected character at position 0 and fails to match. Both lexers see it identically because the BOM is a single rune and a single grapheme.

**Fix.** The caller strips it before parsing:

```csharp
var cleaned = input.TrimStart('\uFEFF');
var result = grammar.Parse(cleaned);
```

## U+FFFE: Rejected Under Normalization To Match .NET

U+FFFE is the byte-swapped twin of the BOM from the previous section, and where the BOM is merely invisible, this one gets your input rejected. Under the default `Compile(NormalizationForm.FormC)`, input containing U+FFFE fails with a `MalformedInput` outcome positioned at the offending code unit, and a `Literal` or `Token` containing it fails at `Compile` with an error naming the character. Every other noncharacter passes through as an ordinary token: U+FFFF, the U+FDD0..U+FDEF block, and the 32 supplementary-plane noncharacters like U+1FFFE. Only U+FFFE is singled out.

That looks arbitrary, and by the Unicode standard it is. U+FFFE is a noncharacter but still a valid scalar value. [Corrigendum #9](https://www.unicode.org/versions/corrigendum9.html) (2013) says noncharacters don't make text ill-formed, and UAX #15 requires all four normalization forms to leave U+FFFE unchanged. A strictly conformant normalizer accepts it.

The parser rejects it anyway because .NET does. `string.Normalize` and `IsNormalized` throw `ArgumentException` on exactly U+FFFE and no other noncharacter. Surprisingly, the rejection isn't in the normalizer itself: modern .NET hands normalization to ICU, and ICU accepts U+FFFE just fine. The throw comes from a managed pre-scan (`HasInvalidUnicodeSequence` in the runtime's [Normalization.Icu.cs](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Globalization/Normalization.Icu.cs)) that .NET added when it moved from Windows NLS to ICU, so the exception behavior wouldn't change out from under existing apps. The comment in the .NET source says it plainly: "ICU does not signal an error during normalization if the input string has invalid unicode, unlike Windows (which uses the ERROR_NO_UNICODE_TRANSLATION error value to signal an error). We walk the string ourselves looking for these bad sequences so we can continue to throw ArgumentException in these cases."<!-- style-lint-ok: verbatim quote from the .NET runtime source -->

Follow the trail one step further back and you reach the Win32 `NormalizeString` function, which returns ERROR_NO_UNICODE_TRANSLATION when it sees U+FFFE. Windows flags it because U+FFFE is the byte-swapped byte order mark: when it shows up in decoded UTF-16 text, the likely explanation is that the bytes were decoded with the wrong endianness, which means the rest of the string is probably garbage too. So this was never a policy about noncharacters. It's a "your decoder was probably misconfigured" heuristic from the Unicode 4.0 era, before Corrigendum #9 settled that noncharacters are legal in interchange, and it's why exactly one of the 66 noncharacters gets rejected. The check also looks only at the single UTF-16 code unit 0xFFFE, which is why supplementary noncharacters like U+1FFFE (encoded as a surrogate pair) sail through.

So why does the parser copy a twenty-year-old Windows heuristic instead of following the standard? Because the parser promises identical behavior on every runtime, and .NET's behavior is the one it matches. The Runtime implementation is a thin call to `string.Normalize`, and .NET throws. Matching .NET keeps the implementation simple and the compatibility claim exact: the built-in normalizer matches .NET 10, throw behavior included, and the parser's own pre-parse scan makes the rejection identical even on runtimes whose `string.Normalize` wouldn't throw at all (Unity's Mono, or .NET under invariant globalization). The cost is a documented standards exception: the UAX #15 conformance suite's "everything unlisted normalizes to itself" corollary runs with U+FFFE skipped, and the skip's comment in [NormalizationConformanceTests.cs](../src/InductorParser.Tests/Lexing/UnicodeConformance/NormalizationConformanceTests.cs) points back at this same story.

**Fix.** If your input can legitimately contain U+FFFE, strip it before parsing the way the previous section strips the BOM, or compile without normalization, where U+FFFE is an ordinary one-char token:

```csharp
var cleaned = input.Replace("\uFFFE", "");   // strip it up front
// or
var grammar = rule.Compile(null);            // no normalization: U+FFFE is a plain token
```

And if U+FFFE genuinely shows up in your decoded text, consider taking the hint the heuristic was built to give: check the byte order of whatever decoded the stream, because the real problem is usually upstream of the parser.

## CRLF Line Endings

Unicode text segmentation treats `\r\n` as a single grapheme cluster (UAX #29 rule GB3), so the lexer hands the parser one two-char token whenever it sees a Windows line ending. This breaks any line-based grammar that tries to match or stop on a bare `\n`:

- `Token('\n')` matches one token whose text is exactly `\n`. The CRLF token's text is `\r\n`. No match.
- `OneOf(TokenSet.Runes("\n"))` matches one token that's a single rune from the set. The CRLF token is two runes, so it's never in a rune set. No match.
- `NoneOf(TokenSet.Runes("\n"))` fails the other way around: CRLF isn't in the set, so `NoneOf` *matches* it. A scan like `ZeroOrMore(NoneOf(...))` that's supposed to stop at the line break swallows the CRLF as content, and the line-terminator rule that was supposed to match next finds it already eaten.

**Fix.** Use the built-in `EndOfLine()` rule. It's `Or(Literal("\r\n"), OneOf(TokenSet.LineTerminators))` under the hood. `TokenSet.LineTerminators` holds the single-rune terminators (LF, CR, VT, FF, NEL, LINE SEPARATOR, PARAGRAPH SEPARATOR per UTS #18 §1.6, RL1.6) plus CRLF as a multi-rune member, so CRLF is consumed as one terminator. Pass `eofIsEol: true` for the "line terminator here, or end of input" case, and wrap with `Optional` for "line terminator here, or none at all". Anywhere a grammar cares about line breaks, use these instead of building one with `Token('\n')` or a `OneOf` over a rune set:

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

Under the default `Compile(NormalizationForm.FormC)` you never see this, because the parser runs its own rejection scan before normalizing. Any input with a lone surrogate (which is ill-formed UTF-16) or the noncharacter U+FFFE (valid Unicode, but .NET's `string.Normalize` rejects it and the parser [matches that behavior](#ufffe-rejected-under-normalization-to-match-net)) fails the parse with a `MalformedInput` outcome before tokenization, positioned at the offending code unit, as a result you can localize. The scan is the parser's own, so the behavior is identical on every runtime and doesn't depend on whether that runtime's `string.Normalize` rejects malformed text (.NET's does, Unity's Mono doesn't). The gotcha only shows up under `Compile(null)`, which skips normalization and lets the lexer surface a lone surrogate as a one-char token (with no scalar value).

When that token reaches the parser, `NoneOf(set)` admits it:

```csharp
string loneSurrogate = "\uD800";   // the \uD800 escape is plain ASCII in source (a raw pasted surrogate could get mangled to U+FFFD)

// NoneOf is a direct non-membership test: the lone surrogate isn't in
// Letters, so NoneOf admits it.
NoneOf(TokenSet.Letters).Compile(null).Parse(loneSurrogate).Success;   // True
```

`NoneOf(set)` matches any token whose value isn't in `set`. A lone surrogate isn't in `set`, so it passes.

**Fix.** Decide whether you actually want lone surrogates. If you're sweeping raw or possibly-malformed content under `Compile(null)` (WTF-8 round-tripping, lenient handling of unpaired surrogates), `NoneOf` admitting them is usually exactly what you want, so leave it. If you want to exclude them, match against the surrogate-free scalar universe instead: `OneOf(TokenSet.ScalarUniverse - stopSet)`. `ScalarUniverse` leaves out the surrogate block, so a lone surrogate isn't a member and the rule won't match it. And remember it only matters under `Compile(null)`: the default `FormC` compile rejects the malformed input upstream, before the rule runs.


## Token Segmentation Across Runtimes

The whole story is short: on Unity the parser segments with its own included segmenter, everywhere else it uses the runtime's `StringInfo`, and one global, `UnicodeEnvironment.Implementation`, can switch that choice on demand. The same global also governs normalization (the next section), so there is no way to pick a segmenter and a normalizer that read from different Unicode data.

Unity is the odd one out because the `StringInfo` in its Mono runtime (and in .NET Framework and .NET Core 3.x) predates UAX #29, the current Unicode rules for where one user-visible character ends and the next begins. Under the old rules, modern text comes apart: a skin-toned emoji like 👋🏽 splits into two tokens, a flag like 🇺🇸 splits into its two halves, a family emoji splits into one token per family member, and a Windows line ending (carriage return plus line feed) splits into two tokens. That misparses real input and fails hundreds of the library's grapheme tests. The included segmenter, [GraphemeSegmentation.cs](../src/InductorParser/Lexing/Unicode/GraphemeSegmentation.cs), is a port of the MIT-licensed implementation inside .NET's `StringInfo` (taken from .NET 8, and .NET 10 still runs the same algorithm), with its break-property table generated from the Unicode 16.0 data files, and everything that segments (`Token(...)` and `Graphemes(...)` validation, the lexer, error positions) uses it, so Unity segments the way modern .NET does. Every other runtime's `StringInfo` is already correct, and using it keeps token boundaries in sync with `string.Normalize` and the character-category data, which all ship together at the runtime's Unicode version.

What's left to know:

- The built-in segmenter is a deliberate match for .NET 10's `StringInfo`, same Unicode 16.0 data and same rule set, and differential tests verify the two agree (every scalar one at a time, a curated corpus with its pairwise concatenations, and a hundred thousand randomized sequences). How the suite enforces that, and how the table gets regenerated at the next Unicode version bump, is test machinery, covered in [TestArchitecture.md](TestArchitecture.md).
- The first known rule-level gap is Unicode 15.1's rule GB9c, which keeps Indic conjuncts (Devanagari क्षि, for example) together as one grapheme cluster where the older rules split them. As of mid-2026 .NET hasn't implemented GB9c either ([dotnet/runtime#111546](https://github.com/dotnet/runtime/issues/111546)), so the built-in segmenter and every current .NET runtime still agree. Environments that have moved to the newer rules (ICU 74 and later, browsers, Swift) segment those conjuncts differently, so a grammar that tokenizes Indic text will see different token boundaries here than in those environments.
- A CoreCLR server that must agree on parse trees with a Unity client can opt into the built-in implementations at startup, before building grammars:

  ```csharp
  UnicodeEnvironment.Implementation = UnicodeImplementation.Bundled;
  ```

- Setting `UnicodeImplementation.Runtime` on Unity brings back the broken splitting this section is about. `Token("👋🏽")` even throws at grammar build, because the old rules read the emoji as two characters. Don't set `Runtime` on Unity.

## Normalization Across Runtimes

Normalization follows the same per-runtime pattern that segmentation just did: on Unity the parser normalizes with its own included UAX #15 normalizer, everywhere else it uses the runtime's `string.Normalize`. And the switch is the same `UnicodeEnvironment.Implementation` global from the previous section: one choice governs segmentation and normalization together. Normalizing with one Unicode implementation and segmenting with another is exactly the inconsistency the per-build policy exists to prevent.

Unity is the odd one out again because its Mono runtime's `string.Normalize` is both incomplete and too permissive. It leaves compatibility mappings unapplied (the ﬁ ligature stays one character under FormKC instead of becoming "fi", and supplementary-plane characters like mathematical-bold letters never convert at all), misses at least one canonical mapping (≠ doesn't decompose under FormD), and accepts ill-formed UTF-16 instead of rejecting it. That breaks FormKC/FormKD grammars outright and quietly weakens the canonical forms. The included normalizer, [UnicodeNormalization.cs](../src/InductorParser/Lexing/Unicode/UnicodeNormalization.cs), implements UAX #15 with tables generated from the Unicode 16.0 data files, and everything that normalizes (input at `Parse`, literals and token sets at `Compile`, position mapping afterward) uses it, so Unity normalizes the way modern .NET does.

What's left to know:

- The built-in normalizer matches what .NET does. `string.Normalize` hands the work to ICU (the Unicode library most operating systems supply), so matching .NET means matching the industry-standard implementation. How the test suite verifies that, including the pinned ICU oracle and the official Unicode conformance file, is test machinery, covered in [TestArchitecture.md](TestArchitecture.md).
- On the Runtime implementation the two operations can sit at different Unicode versions, because .NET gets segmentation from tables compiled into the runtime (Unicode 16.0 on .NET 10) and normalization from the OS's ICU (Unicode 15.0 on current Windows). That's how every .NET 10 app on Windows behaves, not a parser choice. `Bundled` never mixes versions, both its tables come from the same Unicode 16.0 UCD. So on .NET 10, `Runtime` and `Bundled` differ in exactly one place: the characters Unicode 16.0 added normalize differently, because `Bundled` knows them and a current Windows host's ICU doesn't yet. On .NET 8 or 9 those characters segment differently too.
- A CoreCLR server that must agree on parse trees with a Unity client can opt into the built-in implementations with the one line of code:

  ```csharp
  UnicodeEnvironment.Implementation = UnicodeImplementation.Bundled;
  ```

- Setting `UnicodeImplementation.Runtime` on Unity brings back Mono's broken normalization this section is about, along with the broken segmentation from the previous one. Don't set `Runtime` on Unity.
- Input the normalizers reject is caught by the parser's own scan before either normalizer runs, so `MalformedInput` reporting is identical on every runtime regardless of this setting. The scan rejects two things: an unpaired surrogate, which is ill-formed UTF-16, and U+FFFE, a noncharacter that pure UAX #15 would normalize to itself but .NET's `string.Normalize` refuses. Rejecting U+FFFE is a deliberate match for .NET, not a Unicode requirement. The [U+FFFE section](#ufffe-rejected-under-normalization-to-match-net) above has the full story.

## Host Globalization Settings

Everything the previous two sections said about the Runtime implementation assumed the runtime's globalization is in its normal state. .NET has process-wide settings that change it, and because the parser normalizes with `string.Normalize` on every non-Unity build by default, and `string.Normalize` hands the work to ICU (International Components for Unicode, the open-source library most operating systems supply for Unicode algorithms), these settings change what the parser does. None of them are parser settings. They belong to the host app, so a parser embedded in someone else's app inherits whatever that app chose.

The parser checks for the two damaging settings instead of trusting the host. When the Runtime implementation is chosen and the process is running invariant globalization or Windows NLS, the first normalizing `Compile` or `Parse` throws an `InvalidOperationException` that names the detected mode and both ways out: opt into the built-in implementations, or accept the host's globalization deliberately. The rest of this section walks through the settings themselves, exactly when the check fires, and which way out fits which situation.

Three of the settings matter:

- Invariant globalization strips ICU out of the process entirely. It exists for deployments that want small images and no ICU dependency (trimmed containers, machines with no ICU installed), where the app promises it never does culture-aware work. It's turned on with the `InvariantGlobalization` project property, the `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` environment variable, or the `System.Globalization.Invariant` runtimeconfig switch. Under it, `string.Normalize` returns its input unchanged and `IsNormalized` always says true. Here's what that looks like on .NET 10:

  ```
  Normalize(FormC) of A + U+0301:  A + U+0301   (unchanged, should be U+00C1)
  IsNormalized(FormD) of U+00C1:   True         (should be False)
  Normalize(FormC) of U+FFFE:      U+FFFE       (should throw)
  ```

  So a default-normalizing grammar silently stops normalizing: decomposed input no longer matches precomposed literals, and FormKC/FormKD grammars stop applying compatibility mappings. This is the setting the check mainly exists for. Without the check nothing would throw, and the parse would just quietly produce different results.

- Windows NLS (National Language Support, Windows' native globalization API) makes `string.Normalize` use Windows' own normalization data instead of ICU. It's a backward-compatibility escape hatch: .NET used NLS on Windows until .NET 5 switched to ICU, and that switch changed comparison and sort results, so apps with data sorted under the old rules (database indexes, for example) can ask for the old behavior back with `DOTNET_SYSTEM_GLOBALIZATION_USENLS=1` or the `System.Globalization.UseNls` runtimeconfig switch. It behaves correctly on the common cases, but its data moves with Windows servicing and can lag the ICU everyone else uses, so two machines can disagree on edge cases. The check throws for NLS too, because machines quietly disagreeing about the same input is exactly what it exists to catch. If NLS is what you want, the opt-out below keeps it.

- App-local ICU makes the app supply a specific ICU instead of using the OS one: a `Microsoft.ICU.ICU4C.Runtime` package reference plus the `System.Globalization.AppLocalIcu` runtimeconfig option. This is the one that makes normalization *more* predictable, and it's what the parser's own test project does to keep its comparison target from moving (see the comment in InductorParser.Tests.csproj).

Two things stay unaffected. Segmentation: `StringInfo`'s Unicode data is compiled into the runtime itself, so token boundaries are identical under all three settings. Malformed-input reporting: the parser's own scan rejects unpaired surrogates and [U+FFFE](#ufffe-rejected-under-normalization-to-match-net) before any normalizer runs, so `MalformedInput` errors don't change even under invariant globalization, where `string.Normalize` itself would accept U+FFFE.

The check only fires where the settings actually matter: when the Runtime implementation is active and a grammar actually normalizes. `Bundled` never consults it, segmentation-only grammars never consult it, and a grammar compiled with `Compile(null)` turned normalization off, so it never consults the check either. Detection is mostly plain behavior: normalize a decomposed e plus combining acute to FormC and see whether it composes. That catches invariant globalization directly, and it keeps working in trimmed apps, where reflection can find nothing. NLS normalizes that example correctly, so for NLS the parser reads the runtime's internal GlobalizationMode flags instead, which also lets the exception name the exact mode (there's no public .NET API for these modes). The code is [HostGlobalizationCheck.cs](../src/InductorParser/Lexing/Unicode/HostGlobalizationCheck.cs).

What to do about the exception depends on which setting is on. Under invariant globalization a normalizing grammar is simply broken, so opt into the built-in implementations at startup, the same line from the previous sections:

```csharp
UnicodeEnvironment.Implementation = UnicodeImplementation.Bundled;
```

The built-in normalizer and segmenter run from their own tables and never touch the host's globalization, so the parser behaves the same no matter what the app or container chose.

Under NLS you have a real choice because normalization still works. Opt into `Bundled` if you'd rather have the parser parse identically on every machine than match the host's NLS data. Or keep the parser on Runtime if you turned NLS on deliberately and want the parser to agree with the rest of your process:

```csharp
UnicodeEnvironment.AcceptHostGlobalization = true;
```

That line says the host's globalization is understood and the runtime implementations are wanted anyway. It suppresses the check under both modes, including invariant globalization, where it deliberately buys back the silent do-nothing normalization described above, so use it under invariant mode only if the grammars in the process genuinely don't depend on normalization. Both properties follow the same freeze rule: set them at startup, before building grammars or parsing. By the time the exception fires, the first normalization query has already frozen the choice, so the fix goes in startup code, not in a catch block around the parse.
