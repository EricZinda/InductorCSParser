
# Security-Related Concerns
## Pathological Input

Regex expressions can sometimes introduce [denial-of-service attacks](https://en.wikipedia.org/wiki/ReDoS) (or just plain poor user experiences) when they encounter adversarial or unexpected text. Here's a classic that looks reasonable in code review: 

`^([a-zA-Z0-9]+)*@example.com$`

A simple email-ish validator. Feed it `"aaaaaaaaaaaaaaaaaaaaa!"` and .NET Regex will happily burn a couple hundred milliseconds trying to find a match. The problem is the nested `+` inside `*`: when the match fails, the engine has to try every way to split the a's across the two quantifiers before giving up. Add another a or two and the time doubles.

The Inductor Parser version avoids this and is more readable as well:

```csharp
var validator = And(
    OneOrMore(OneOf(TokenSet.Ascii.Letters | TokenSet.Ascii.Digits)),
    Literal("@example.com"),
    Eof()
);
```

`OneOrMore` greedily consumes all the a's in one pass, sees the `!`, fails cleanly. It takes linear time no matter what you throw at it.

Even the textbook ReDos example `^(a+)+$` is safe using the Inductor Parser:

```csharp
var pattern = And(OneOrMore(OneOrMore(Token('a'))), Eof());
```

Even written in this contrived shape with one composite rule wrapping another, it still runs in linear time. 

Backtracking isn't the only way to hang. A 100 MB input file, a grammar that recurses 10,000 levels deep on nested parenthesis, or untrusted input in a web handler can all do it, too. 

The parser has three ways to handle these scenarios:

- `RuleCountLimit` (default 10M) caps how much work a parse can do: each rule invocation and each bulk-scan step counts as one unit. The benchmark's JSON parser on a 1 MB input (1,081,666 chars) uses about 4.1M units and parses in ~220 ms, so the default has comfortable headroom for well-formed input. Deterministic across hardware, so the same input trips at the same count on every machine.
- `MaxDepth` (default 1000) caps the recursion depth. 10,000 nested open parenthesis fail cleanly instead of killing the process with an uncatchable `StackOverflowException`.
- `Timeout` (default off) caps wall-clock time spent (done without a thread to support WebGL). Off by default because the other two handle safety issues and wall-clock limits make tests flaky across hardware. Still worth turning on for untrusted input in a request handler.

## Unicode Attacks
Unicode opens up a few classic ways to attack a parser. The good news is that grammars written naturally already block most of them. 

When thinking about these attacks and how to defend against them, it's useful to think about whether your grammar defines what's *allowed* (your grammar must match for input to be accepted) or what's *blocked* (your grammar must match for input to be rejected). The default Inductor Parser behavior protects you for "allowed" rules. For "blocked" rules, you sometimes need to do a little extra work.

Two pieces of background make the rest of this section easier. 

First, normalization form: for storing and comparing text, [Normalization Form C (NFC) is the standard recommendation](https://www.unicode.org/reports/tr15/), the form W3C and most specs assume, and this parser's default. The compatibility form `FormKC` is a heavier, lossy hammer for when you specifically want styled and compatibility variants collapsed together, so choose it on purpose, not by default. 

Second, if the thing you're validating is a username or password, you don't have to invent the rules from scratch. The IETF PRECIS framework ([RFC 8264](https://www.rfc-editor.org/rfc/rfc8264), with [RFC 8265](https://www.rfc-editor.org/rfc/rfc8265) for usernames and passwords) already specifies the allowed characters, the NFC normalization, and the case handling, and it's the modern replacement for the older stringprep approach. The techniques in this section are how you'd enforce a profile like that inside a grammar.

### Trojan Source
Attack: An attacker hides a bidi-direction character (like `U+202E`) in input so an editor renders the text in one order while the parser sees a different one. The same source code can look like one thing to a reviewer and mean another to a compiler.

For how this could be used, let's say you use the Inductor Parser for a small access-control language whose rules are code-reviewed before they deploy, and where `#` starts a comment. An attacker submits a rule that the review tool displays as:

```
grant("read")   # grant("write") only for admins
```

The reviewer reads the second `grant` as a commented-out note, approves read-only access, and it ships. But the attacker wrapped the line in a few bidi control characters (a RIGHT-TO-LEFT OVERRIDE and the isolates that keep each word readable), and the raw code points the parser actually consumes, in logical order, are:

```
grant("read")   grant("write") # only for admins
```

The `#` isn't where the reviewer saw it. `grant("write")` is a live rule now, and only the trailing text is the comment. The parser reads the bytes in the order they were submitted and never does the visual reshuffle the editor did, so it builds the policy the attacker wanted, not the one the reviewer signed off on. 

Defense: This one's different from the rest, because the parser was never the thing being fooled. It reads the raw code points in logical order and builds exactly the rule the bytes describe, the live `grant("write")` included. The victim is the human who reviewed the visual rendering and signed off on a rule that reads one way and parses another. So "the grammar sees the true order" isn't the defense here, it's the problem: the parser faithfully carries out an intent the reviewer never saw.

That also means you can't fix it at the grammar level the way you fix lookalikes (shown below). A strict "allowed" grammar rejects a bidi control that lands somewhere it isn't permitted, but these characters hide inside comments and string literals, which accept almost anything, and there the parser takes them without complaint. To fix it, close the gap between what the reviewer sees and what the parser does. [UAX #9, the Unicode Bidirectional Algorithm](https://www.unicode.org/reports/tr9/), enumerates the bidi formatting characters, and they're all General Category `Cf` (Format), the same category as the other invisibles, so one `TokenSet` recognizes the whole class, and you can refuse any input that contains one before it reaches a reviewer or the parser:

```csharp
static readonly TokenSet FormatControls = TokenSet.Category(UnicodeCategory.Format);

static bool ContainsFormatControls(string source) =>
    source.EnumerateRunes().Any(rune => FormatControls.ContainsRune(rune));

// Refuse the input instead of parsing a version the reviewer never saw
if (ContainsFormatControls(source))
    throw new FormatException("input contains invisible format control characters");
```

Refusing is usually the right call for text you're going to show a human, since there's rarely a legitimate reason for an override character in an access rule. If you'd rather accept the input and clean it instead, that same Format category is what the `StripInvisibles` filter in the invisibles section removes, so running the input through it drops the `U+202E`, lines the visual order back up with the logical one, and the reviewer finally sees the real `grant("write")`.

One caveat on rejecting the whole `Format` category: it's the right move for an ASCII-ish access-control language, where none of those characters belong, but it's too broad for human-language text. It also removes ZERO WIDTH NON-JOINER (`U+200C`) and ZERO WIDTH JOINER (`U+200D`), which Persian, Indic scripts, and emoji sequences genuinely need. If your input is natural language, reject just the twelve bidi controls rather than all of `Format`:

```csharp
static readonly TokenSet BidiControls = TokenSet.FromRanges(new (int Low, int High)[]
{
    (0x202A, 0x202E),   // LRE, RLE, PDF, LRO, RLO
    (0x2066, 0x2069),   // LRI, RLI, FSI, PDI
    (0x200E, 0x200F),   // LRM, RLM
    (0x061C, 0x061C),   // ALM
});
```

The [Trojan Source authors](https://trojansource.codes/) recommend a narrower rule still: allow bidi controls but reject *unterminated* ones inside a comment or string literal, so any reordering stays trapped where it started. Compilers added checks along these lines: [GCC 12's `-Wbidi-chars`](https://gcc.gnu.org/gcc-12/changes.html) warns on misleading bidi controls, and [Rust 1.56.1](https://blog.rust-lang.org/2021/11/01/cve-2021-42574/) shipped deny-by-default lints for them in string literals and comments. The underlying issue is tracked as CVE-2021-42574.

### Lookalike characters
Attack: Some characters look almost identical to common letters but are different code points underneath. An attacker writes `𝐚dmin` (math-bold), `ａｄｍｉｎ` (fullwidth), or `аdmin` (with a Cyrillic `а`) to register an account that looks like `admin`, or types `ｓｅｌｅｃｔ` to slip past a SQL filter.

There are two kinds of lookalike, described next.

#### Unicode Compatibility Lookalikes
Attack: A site reserves the name `admin` for public posts to a forum so nobody can impersonate staff, and enforces it by checking each new username against a blocklist. An attacker signs up as `𝐚dmin`, spelling the first letter with math-bold `𝐚` (`U+1D41A`). The blocklist compares raw code points, sees a string that isn't `admin`, and lets the registration through. Now every comment and support reply the attacker posts shows a name that reads as `admin`, and they can pose as staff to anyone who trusts the label.

Unicode compatibility lookalikes are stylistic or formatting variants of the same base character. Math-bold `𝐀` (`U+1D400`), fullwidth `Ａ` (`U+FF21`), superscripts, ligatures: Unicode declares them compatibility-equivalent to the plain letters they represent, and `FormKC` [normalization](Primer3.md#compatibility-vs-canonical) converts them back to those plain letters. Whether you want that conversion depends on your rule. For "allowed" rules the default `FormC` rejects the versions you didn't write down explicitly, which is what you want:

```csharp
// A rule that accepts only the exact "admin" you wrote down
var allowed = And(Literal("admin"), Eof()).Compile();

allowed.Parse("admin").Success;      // true: the exact letters you wrote
allowed.Parse("𝐚dmin").Success;      // false: math-bold 𝐚 isn't the plain 'a' the rule matches
allowed.Parse("ａｄｍｉｎ").Success;  // false: fullwidth letters aren't the plain ones either
```

For "blocked" rules, compile with `FormKC` instead and they all convert to plain letters before the rule runs:

```csharp
// A rule that blocks all forms of "admin"
var blocker = And(Literal("admin"), Eof()).Compile(NormalizationForm.FormKC);

blocker.Parse("admin").Success;      // true
blocker.Parse("𝐚dmin").Success;      // true: FormKC converts 𝐚 (math-bold) to plain a
blocker.Parse("ａｄｍｉｎ").Success;  // true: FormKC converts fullwidth letters to ASCII
```
#### Homoglyphs
Attack: The same site now blocks the styled variants too, by normalizing every username to plain letters before the blocklist check (the `FormKC` fix from the last section). An attacker signs up as `аdmin`, this time with the first letter a Cyrillic `а` (`U+0430`) instead of Latin `a` (`U+0061`). Normalization leaves it alone, because Unicode says the Cyrillic letter is a genuinely different letter and not a styled `a`, so the username clears the blocklist and renders identically to `admin` in nearly every font. Even a moderator who pulls up the raw string and reads it character by character sees `admin`. It's the same mechanism behind IDN homograph phishing, where a link that reads as `apple.com` is registered with Cyrillic letters and leads somewhere else.

Cross-script homoglyphs are different from compatibility lookalikes and need a different defense. They aren't stylistic variants of one base character, they're independently encoded letters from independently-developed alphabets that happen to share a glyph shape. Unicode considers them distinct because they mean different things in their respective scripts, so FormKC won't convert one into the other. No [normalization form](Primer3.md#compatibility-vs-canonical) will, so the defense moves up a level. The industry standard for "is this name confusable with that one" is the skeleton algorithm in [UTS #39, Unicode Security Mechanisms](https://www.unicode.org/reports/tr39/): reduce each string to a skeleton by mapping every character through Unicode's confusables data, and two strings are confusable exactly when their skeletons match. Computing that against the names you already have is how you catch a homoglyph of an existing account, and it's more than a grammar does on its own. What the grammar can do directly is narrow which letters it accepts in the first place. Anywhere it takes letters from arbitrary scripts (`Identifier()`, `OneOf(TokenSet.Letters)`, or any other rule with a broad letter set) an attacker can mix scripts. Restrict your grammar to one script's letters, use `TokenSet.Ascii.Letters` for ASCII-only, or build a custom set covering the script(s) you actually want to support:

```csharp
var username = And(OneOrMore(OneOf(TokenSet.Ascii.Letters)), Eof()).Compile();

username.Parse("admin").Success;      // true
username.Parse("аdmin").Success;      // false: Cyrillic 'а' (U+0430) isn't in the ASCII range
username.Parse("𝐚dmin").Success;      // false: math-bold 𝐚 isn't either
username.Parse("ａｄｍｉｎ").Success;  // false: fullwidth letters aren't either
```

This catches both kinds at once: Cyrillic `а`, math-bold `𝐚`, and fullwidth `ａ` all fall outside `Ascii.Letters` (the range `U+0041..U+005A` and `U+0061..U+007A`), so the rule rejects them under default `FormC`. For a field that only legitimately holds ASCII letters, this is a complete defense and no `FormKC` is needed.

Script restriction only works when the field really is one script, and even then it has a gap worth knowing. An attacker can spell a whole word in one non-Latin script that reads as a Latin one: all-Cyrillic `ѕсоре` looks like Latin `scope`, and each string is internally single-script, so a "one script only" check passes both. [UTS #39](https://www.unicode.org/reports/tr39/) calls these *whole-script confusables*. Restricting to ASCII closes the gap, because it rejects Cyrillic and Greek outright, so the lookalike letters can't appear at all. Restricting to Cyrillic doesn't, because the letters that make a Cyrillic word read as Latin (`а`, `е`, `о`, `р`, `с`) *are* Cyrillic letters, so a Cyrillic-only rule accepts them. And a field that legitimately spans many scripts (someone wants to register `김철수`) can't use script restriction at all. There, compile with `FormKC` to catch the compatibility lookalikes, and use UTS #39 confusable skeletons to catch the cross-script ones, comparing each new name's skeleton against the names already registered. That's the piece normalization alone can't give you.

### Invisible characters
Attack: Zero-width spaces, soft hyphens, BOMs, and similar characters don't render but still take up a position in the text. An attacker writes `ki<ZWS>ll` to slip a banned word past a profanity filter, or registers a name that displays as `admin` but compares as different because it has invisible characters in it. 

Defense: For "allowed" rules, the default is good since the invisibles don't match. For "blocked" rules, no [normalization form](Primer3.md#compatibility-vs-canonical) strips invisibles, so you have to filter them out yourself before parsing. The filter is short:

```csharp
static readonly TokenSet Invisibles = TokenSet.Category(UnicodeCategory.Format);

static string StripInvisibles(string input) =>
    string.Concat(input.EnumerateRunes()
        .Where(r => !Invisibles.ContainsRune(r)));

// "ki<ZWS>ll" becomes "kill" before the rule sees it
var result = blockedWords.Parse(StripInvisibles(userInput));
```

`TokenSet.Category(UnicodeCategory.Format)` catches most invisibles (zero-width space, the joiners, word joiner, soft hyphen, BOM, the bidi controls) but not all of them. The Unicode property for "invisible" is `Default_Ignorable_Code_Point`, and it covers more than the Format category. The biggest issue is `U+3164` HANGUL FILLER: it renders as blank width but is category *Letter*, so it slips past a Format filter and a "letters only" rule alike. .NET doesn't expose that property, so add the strays to the Format set yourself:

```csharp
static readonly TokenSet Invisibles =
    TokenSet.Category(UnicodeCategory.Format)
    | TokenSet.FromRanges(new (int Low, int High)[]
      {
          (0x034F, 0x034F),    // COMBINING GRAPHEME JOINER
          (0x115F, 0x1160),    // Hangul choseong / jungseong fillers
          (0x3164, 0x3164),    // HANGUL FILLER
          (0xFFA0, 0xFFA0),    // halfwidth Hangul filler
          (0xFE00, 0xFE0F),    // variation selectors 1-16
          (0xE0100, 0xE01EF),  // variation selectors 17-256
      });
```

Use that in place of the `Category(Format)`-only version above. Better yet, generate the set from the `Default_Ignorable_Code_Point` property in the [Unicode Character Database](https://www.unicode.org/Public/UCD/latest/ucd/DerivedCoreProperties.txt) so it stays correct as Unicode grows. (The variation selectors and COMBINING GRAPHEME JOINER in the list are category Mark, so the combining-marks filter in the next section catches them too.)

The same `Invisibles` value plugs into a real grammar rule. An "allowed"-rule username that flat-out refuses any invisible in the input:

```csharp
var safeUsername = And(
    OneOrMore(NoneOf(Invisibles)),
    Eof()
).Compile();

safeUsername.Parse("admin").Success;             // true
safeUsername.Parse("ad\u200Bmin").Success;       // false: the ZWS fails NoneOf(Invisibles)
```

One thing to know about `Format`: it includes `U+200D` (ZWJ), so emoji families like `👨‍👩‍👧` split into their components after the filter. That's fine for a banned-text check. If your input can contain emoji you want to keep whole, take the `Format` category and subtract ZWJ with the `-` (difference) operator:

```csharp
static readonly TokenSet Invisibles =
    TokenSet.Category(UnicodeCategory.Format) - TokenSet.Single(0x200D);
```

### Combining marks
The invisibles filter above has a blind spot, and it's worth calling out because it looks like it should be covered and isn't. A combining mark attaches to the character in front of it. Most are visible accents (the two dots of an umlaut, a cedilla), but a few draw nothing at all. `U+034F` COMBINING GRAPHEME JOINER is the cleanest: it takes up a position, joins to whatever precedes it, and renders as nothing.

Attack: the same profanity filter from the last section blocks `kill`. An attacker submits `ki<CGJ>ll` with a COMBINING GRAPHEME JOINER wedged between the `i` and the first `l`. It reads as `kill` to every human. It clears a `FormKC` blocker, because no normalization form removes a COMBINING GRAPHEME JOINER (it's built to survive all of them). And it clears `StripInvisibles`, because that filter is the Format category (`Cf`) and a combining mark is a different category, Nonspacing Mark (`Mn`). The one filter that looks like it should catch invisible characters doesn't catch this one.

Defense: widen the strip set to include combining marks. Unicode's Mark group is exactly three General Categories, `Mn`, `Mc`, and `Me`. The [UCD PropertyValueAliases](https://www.unicode.org/Public/UCD/latest/ucd/PropertyValueAliases.txt) file spells that out: `gc ; M ; Mark ; Combining_Mark # Mc | Me | Mn`. .NET exposes them as the [`UnicodeCategory`](https://learn.microsoft.com/en-us/dotnet/api/system.globalization.unicodecategory) values `NonSpacingMark`, `SpacingCombiningMark`, and `EnclosingMark`. You normalize to a decomposed form first so a precomposed `ï` splits into `i` plus a mark you can then remove:

```csharp
static readonly TokenSet InvisiblesAndMarks =
    TokenSet.Category(UnicodeCategory.Format)
    | TokenSet.Category(UnicodeCategory.NonSpacingMark)
    | TokenSet.Category(UnicodeCategory.SpacingCombiningMark)
    | TokenSet.Category(UnicodeCategory.EnclosingMark);

// FormKD first so 'ï' decomposes to 'i' + mark, then drop every mark.
// "ki<CGJ>ll" and "kïll" both collapse to "kill" before the rule runs.
static string StripMarks(string input) =>
    string.Concat(input.Normalize(NormalizationForm.FormKD).EnumerateRunes()
        .Where(rune => !InvisiblesAndMarks.ContainsRune(rune)));
```

The `FormKD` step is what makes this work. A precomposed letter like `é` (`U+00E9`) has no separate mark to strip until you decompose it into `e` plus a combining accent, so without that step the accented forms slip through. After it, every accented form collapses to its base letter and `kïll`, `ki<CGJ>ll`, and plain `kill` all become the same string.

This is the right hammer for a banned-word check, where you want `résumé` and `resume` to compare equal and you don't care about losing the accents. It's the wrong hammer for a field where the marks have meaning. Stripping marks from Arabic, Hebrew, or Indic text destroys it, and a name like `José` turns into `Jose`. Use it only where the field is supposed to be mark-free, the same way you'd restrict a username to ASCII letters.

The other thing combining marks enable is bulk. Unicode lets you stack an unbounded number of marks on one base character. Stack a few dozen and you get what the internet calls "Zalgo" text: every letter sprouts a tower of accents above it and another below, spilling over the neighboring lines until the word looks like it's melting down the page. A single letter with a few hundred marks is a small denial-of-service against anything that has to render or shape it, and a way to run up the character count behind a length check. The mark filter above drops all of them, and the parser's own `RuleCountLimit` from the pathological-input section caps the parsing work regardless.

### Case-insensitive matching tricks
Attack: a signup system reserves the name `admin`. It lowercases the input first so `ADMIN` and `Admin` are caught too, then checks the blocklist. On a server whose culture is Turkish, `"ADMIN".ToLower()` is `admın`, with a dotless `ı` (`U+0131`), because Turkish `I` lowercases to the dotless letter. `admın` isn't `admin`, so the check misses it, and the attacker registers a capitalized `ADMIN` that everyone else's machine lowercases straight back to `admin`. The mirror image is just as bad: the Kelvin sign `K` (`U+212A`) lowercases to an ordinary `k`, so "lowercase then compare" makes two different strings equal. That canonicalize-then-compare collision is a known source of real password-reset account takeovers: request a reset for a name spelled with a colliding character, the backend canonicalizes it to the victim's name, and the reset email lands in the attacker's inbox.

Defense: the trap is `String.ToLower` and `String.ToUpper`, which depend on the ambient culture and can turn a character into a different letter entirely. The parser only supplies one case insensitive rule:  `LiteralIgnoreAsciiCase`, which matches case-insensitively across exactly the 26 ASCII letters and nothing else:

```csharp
var blocker = And(LiteralIgnoreAsciiCase("admin"), Eof()).Compile();

blocker.Parse("admin").Success;   // true
blocker.Parse("ADMIN").Success;   // true: ASCII A-Z matched case-insensitively
blocker.Parse("Admin").Success;   // true
```

Because the case-insensitivity is scoped to ASCII, there's no culture to configure and no cross-script collision to exploit. If you genuinely need case-insensitive matching over non-ASCII letters, know that there's no correct one-liner in .NET. `String.ToLowerInvariant` (which does at least sidestep the culture-sensitive `ToLower`'s Turkish-`ı` trap) is only a lowercase mapping, not case-insensitive matching: it leaves `ß` as `ß` while `SS` lowercases to `ss`, so the two never match, and it keeps the Greek final sigma `ς` distinct from medial `σ`. Even .NET's culture-aware `StringComparison.InvariantCultureIgnoreCase` is uneven, treating `ς` and `σ` as equal but still not `ß` and `ss`. Full Unicode case-insensitive matching (where `ß` equals `ss`) isn't something .NET exposes, so you either use a library like ICU or apply Unicode's mappings yourself. The [Unicode case-mapping FAQ](https://unicode.org/faq/casemap_charprop.html) lays out the traps. Normalization matters on top of case, too, since canonically equivalent spellings like the Angstrom sign `Å` (`U+212B`) and precomposed `Å` (`U+00C5`) are the same letter and only compare equal once you normalize.

### Digits from other scripts
Attack: a checkout form reads a quantity with a digit rule and multiplies it by a price. The rule uses `TokenSet.Digits`, which by design is every Unicode decimal digit, not just `0-9`. An attacker types the quantity in Arabic-Indic digits, `٢٣` (that's 23). The grammar matches, because those really are decimal digits. Then the downstream arithmetic goes wrong: `int.Parse("٢٣")` throws no matter the culture (.NET's number parsing only recognizes the ASCII digits `0-9`), which is an unhandled exception on attacker-controlled input. A hand-rolled `rune - '0'` is worse, it silently computes nonsense, because `٢` is nowhere near `2` in code-point order.

Defense: if the value flows into ASCII arithmetic or an ASCII-only parser downstream, match ASCII digits at the grammar so the mismatch never forms:

```csharp
var quantity = And(OneOrMore(OneOf(TokenSet.Ascii.Digits)), Eof()).Compile();

quantity.Parse("23").Success;    // true
quantity.Parse("٢٣").Success;    // false: Arabic-Indic digits aren't ASCII 0-9
```

`TokenSet.Ascii.Digits` is exactly `0-9`. If you do want to accept the world's digits (a search box, a display field), keep `TokenSet.Digits`, but convert each rune by its real Unicode numeric value (`CharUnicodeInfo.GetDecimalDigitValue`) instead of subtracting `'0'`, and never hand the raw string to a consumer that assumes ASCII. Digits from different scripts can even share a shape but have different values, so a rule that mixes digit scripts is a spoofing surface on its own (see [UTR #36 §2.7](https://www.unicode.org/reports/tr36/tr36-15.html), which gives a digit string that reads as `89` but evaluates to 42).

### Normalization ordering
The last few defenses lean on normalization, so it's worth being clear about a trap that comes from doing a check on one form of a string and consuming another.

Compatibility normalization doesn't only strip styling, it can manufacture characters the raw input never contained, including ASCII. Fullwidth `＜` (`U+FF1C`) becomes a plain `<`. Fullwidth digits `１２３` become `123`. So a filter that scans the raw bytes for `<script`, finds nothing, and passes the string to a consumer that normalizes first has handed the real tag straight through. The rule of thumb is to validate the same form you consume. If the downstream normalizes, validate after normalizing, not before. The parser makes this easy to get right, because it normalizes the whole input up front and runs the entire grammar against that one form. Compile your blocked rule with the same form your storage and display layers use, and there's no second form hiding behind the first.

The mirror trap is to validate one form and store another. When you compile an allowed rule with `FormKC`, the rule matches the normalized text, but the parser hands you back the original. `Symbol.SourceText` is deliberately the user's original input, not the normalized copy, so you can keep exactly what they typed. That's usually what you want, but it re-opens the lookalike hole if you're not careful. An allowed-username rule compiled with `FormKC` accepts `𝐚dmin` because the normalized form is `admin`, and if you then store and display `SourceText`, the math-bold impostor is what everyone sees. If the reason you normalized was to canonicalize the identity, store the normalized form, not the original. You can get it two ways: re-normalize with `input.Normalize(NormalizationForm.FormKC)`, or read `Symbol.ToString()`, which renders the normalized text the parser matched (where `Symbol.SourceText` gives the original). The catch with `ToString()` is that it only covers the leaves left in the tree, so a rule built from `Literal` or `Token`, which delete their leaves by default, renders empty rather than the normalized word. Decide which string is the identity and use that one everywhere.

One more consequence of normalizing up front: compatibility forms can grow the input. The worst single character, `U+FDFA`, expands to 18 characters under `FormKC`. So a length cap you check on the raw input before calling `Parse` can undercount the string the parser actually walks by up to that factor. If you're bounding input size as a safety limit, cap after normalizing, or keep the raw cap and treat it as a loose upper bound rather than a tight one.

### Malformed and tampered encodings
This is the pair where the parser does the work for you, worth knowing so you don't reimplement it.

A .NET `string` is just a sequence of UTF-16 code units with no validity rule attached, so a caller who builds strings from raw bytes (or round-trips through WTF-8) can hand you one containing a lone surrogate, half of a character that can't stand on its own. Any grammar compiled with a normalization form (that's every form except `Compile(null)`) routes the input through `String.Normalize` before the lexer runs, and `Normalize` rejects ill-formed UTF-16. The parser turns that rejection into a `MalformedInput` result positioned at the bad character. No grammar rule ever runs against the input, so a corrupt string can't accidentally match:

```csharp
var grammar = And(Literal("hello"), Eof()).Compile();   // default FormC

// A string with a lone high surrogate never reaches the grammar
grammar.Parse(loneSurrogateInput).Outcome;   // ParseOutcome.MalformedInput
```

The related attack is decoder tampering. Every .NET decoder is permissive by default. Feed `Encoding.UTF8.GetString` some malformed bytes and it doesn't throw, it substitutes `U+FFFD` (the replacement character) for the bad bytes and returns a string that looks fine but had data silently dropped. Depending on what was dropped, the survivor might match a grammar that would have rejected the original bytes. The parser doesn't strip or rewrite `U+FFFD`, it surfaces each one as an ordinary character, so you can refuse tampered input by adding `NoneOf(TokenSet.Replacement)` to the character classes that shouldn't hold one:

```csharp
var grammar = And(OneOrMore(NoneOf(TokenSet.Replacement)), Eof()).Compile();

grammar.Parse("hi").Success;          // true: clean input
grammar.Parse(tamperedInput).Success; // false: contains a U+FFFD from a lenient decode
```

Better still, stop the substitution at the source by decoding strictly, so malformed bytes throw at decode time instead of becoming a silent `U+FFFD`:

```csharp
var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
string text = strict.GetString(bytes);   // throws on malformed bytes instead of inserting U+FFFD
```

Many of these have a focused test in `SecurityByDefaultTests.cs` showing the attack and how the parser handles it. The combining-mark, case, and digit defenses are guidance for grammars you write rather than parser features, so they don't have one yet.

The primary sources behind this section, if you want to go deeper:

- [UTR #36 Unicode Security Considerations](https://www.unicode.org/reports/tr36/tr36-15.html) (a broad overview, now stabilized) and [UTS #39 Unicode Security Mechanisms](https://www.unicode.org/reports/tr39/) (the maintained algorithms), for confusables, mixed-script detection, and restriction levels.
- [UAX #15](https://www.unicode.org/reports/tr15/), for the normalization forms.
- [RFC 8264](https://www.rfc-editor.org/rfc/rfc8264) and [RFC 8265](https://www.rfc-editor.org/rfc/rfc8265), the PRECIS framework for internationalized usernames and passwords.
- [Trojan Source](https://trojansource.codes/), Boucher and Anderson, for the bidi reordering attack (CVE-2021-42574).