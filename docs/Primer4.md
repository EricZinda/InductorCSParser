
# Security-Related Concerns
## Pathological Input

Regex expressions can sometimes introduce [denial-of-service attacks](https://en.wikipedia.org/wiki/ReDoS) (or just plain poor user experiences) when they encounter adversarial or unexpected text. Here's a classic that looks reasonable in code review: 

`^([a-zA-Z0-9]+)*@example.com$`

A simple email-ish validator. Feed it `"aaaaaaaaaaaaaaaaaaaaa!"` and .NET Regex will happily burn seconds trying to find a match. The problem is the nested `+` inside `*`: when the match fails, the engine has to try every way to split the a's across the two quantifiers before giving up. Add another a or two and the time doubles.

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

Backtracking isn't the only way to hang. A 100 MB input file, a grammar that recurses 10,000 levels deep on nested parenthesis, or untrusted input in a web handler can all do it, too. The parser has three ways to handle these scenarios:

- `RuleCountLimit` (default 10M) caps how many rule invocations a parse can do. The benchmark's JSON parser on a 1 MB input (1,081,666 chars) does about 1.4M invocations and parses in ~55 ms, so the default has comfortable headroom for well-formed input. Deterministic across hardware, so the same input trips at the same count on every machine.
- `MaxDepth` (default 1000) caps the recursion depth. 10,000 nested open parenthesis fail cleanly instead of killing the process with an uncatchable `StackOverflowException`.
- `Timeout` (default off) caps wall-clock time spent (done without a thread to support WebGL). Off by default because the other two handle safety issues and wall-clock limits make tests flaky across hardware. Still worth turning on for untrusted input in a request handler.

## Unicode Attacks
Unicode opens up a few classic ways to attack a parser. The good news is that grammars written naturally already block most of them. The one to be aware of is whether your rule defines what's *allowed* (your rule must match for input to be accepted) or what's *blocked* (your rule must match for input to be rejected). The default behavior is right for "allowed" rules. For "blocked" rules, you sometimes need to do a little extra work.

### Trojan Source
Attack: An attacker hides a bidi-direction character (like `U+202E`) in input so an editor renders the text in one order while the parser sees a different one. The same source code can look like one thing to a reviewer and mean another to a compiler. 

Defense: Your grammar isn't fooled because the parser doesn't reorder anything based on bidi controls. It just sees the raw character sequence in the input, in the actual logical order the attacker submitted. The visual rearrangement an editor would have shown to a human reviewer doesn't exist as far as the grammar is concerned.

### Lookalike characters
Attack: Some characters look almost identical to common letters but are different code points underneath. An attacker writes `𝐚dmin` (math-bold), `ａｄｍｉｎ` (fullwidth), or `аdmin` (with a Cyrillic `а`) to register an account that looks like `admin`, or types `ｓｅｌｅｃｔ` to slip past a SQL filter.

There are two kinds of lookalike, and one is fixable by [normalization](Primer3.md#compatibility-vs-canonical).

#### Unicode Compatibility Lookalikes
Unicode compatibility lookalikes are stylistic or formatting variants of the same base character. Math-bold `𝐀` (`U+1D400`), fullwidth `Ａ` (`U+FF21`), small caps, superscripts, ligatures: Unicode declares them compatibility-equivalent to plain `A`. FormKC [normalization](Primer3.md#compatibility-vs-canonical) converts them to their "plain" versions. For "allowed" rules the default `FormC` rejects the versions you didn't write down explicitly, which is what you want. For "blocked" rules, compile with `FormKC` instead and they all convert to plain letters before the rule runs:

```csharp
// A rule that blocks all forms of "admin"
var blocker = And(Literal("admin"), Eof()).Compile(NormalizationForm.FormKC);

blocker.Parse("admin").Success;      // true
blocker.Parse("𝐚dmin").Success;      // true: FormKC converts 𝐚 (math-bold) to plain a
blocker.Parse("ａｄｍｉｎ").Success;  // true: FormKC converts fullwidth letters to ASCII
```
#### Homoglyphs
Cross-script homoglyphs look identical but aren't variants of the same base character. Latin `a` (`U+0061`) and Cyrillic `а` (`U+0430`) aren't the same letter that got styled differently. They're independently encoded letters from independently-developed alphabets that happen to share a glyph shape. Unicode considers them distinct because they mean different things in their respective scripts, so FormKC won't convert one into the other. No [normalization form](Primer3.md#compatibility-vs-canonical) will. The only defense is to refuse one of them at the grammar level. Anywhere your grammar accepts letters from arbitrary scripts (`Identifier()`, `OneOf(TokenSet.Letters)`, or any other rule that takes a broad letter set) an attacker can mix scripts. Restrict your grammar to one script's letters, use `TokenSet.Ascii.Letters` for ASCII-only, or build a custom set covering the script(s) you actually want to support:

```csharp
var username = And(OneOrMore(OneOf(TokenSet.Ascii.Letters)), Eof()).Compile();

username.Parse("admin").Success;      // true
username.Parse("аdmin").Success;      // false: Cyrillic 'а' (U+0430) isn't in the ASCII range
username.Parse("𝐚dmin").Success;      // false: math-bold 𝐚 isn't either
username.Parse("ａｄｍｉｎ").Success;  // false: fullwidth letters aren't either
```

This catches both kinds at once: Cyrillic `а`, math-bold `𝐚`, and fullwidth `ａ` all fall outside `Ascii.Letters` (the range `U+0041..U+005A` and `U+0061..U+007A`), so the rule rejects them under default `FormC`. For a field that only legitimately holds ASCII letters, this is a complete defense and no `FormKC` is needed.

Script restriction only works when the field really is one script, though. A username field that has to accept letters from many scripts (e.g. someone wants to register `김철수`) can't use this defense. There you'd compile with `FormKC` to catch compatibility lookalikes (math-bold `𝐚`, fullwidth `ａ`) and accept that cross-script homoglyphs (Cyrillic `а`) remain unsolvable.

### Invisible characters
Attack: Zero-width spaces, soft hyphens, BOMs, and similar characters don't render but still take up a position in the text. An attacker writes `ki<ZWS>ll` to slip a banned word past a profanity filter, or registers a name that displays as `admin` but compares as different. 

Defense: For "allowed" rules, the default is good — the invisibles don't match. For "blocked" rules, no [normalization form](Primer3.md#compatibility-vs-canonical) strips invisibles, so you have to filter them out yourself before parsing. The filter is short:

```csharp
static readonly TokenSet Invisibles = TokenSet.Category(UnicodeCategory.Format);

static string StripInvisibles(string input) =>
    string.Concat(input.EnumerateRunes()
        .Where(r => !Invisibles.ContainsRune(r)));

// "ki<ZWS>ll" becomes "kill" before the rule sees it
var result = blockedWords.Parse(StripInvisibles(userInput));
```

`UnicodeCategory.Format` is exactly the set you want here. It covers zero-width space, zero-width joiner / non-joiner, soft hyphen, BOM, bidi controls, word joiner, language tags, and every other "takes up a position but doesn't render" character Unicode has named. `TokenSet.Category` builds the set once and caches it.

The same `Invisibles` value plugs into a real grammar rule. An "allowed"-rule username that flat-out refuses any invisible in the input:

```csharp
var safeUsername = And(
    OneOrMore(NoneOf(Invisibles)),
    Eof()
).Compile();

safeUsername.Parse("admin").Success;             // true
safeUsername.Parse("ad\u200Bmin").Success;       // false: the ZWS fails NoneOf(Invisibles)
```

One thing to know about `Format`: it includes `U+200D` (ZWJ), so emoji families like `👨‍👩‍👧` split into their components after the filter. That's fine for a banned-text check. If your input can carry emoji you want to keep whole, take the `Format` category and subtract ZWJ with the `-` (difference) operator:

```csharp
static readonly TokenSet Invisibles =
    TokenSet.Category(UnicodeCategory.Format) - TokenSet.Single(0x200D);
```

For each of these, there's a focused test in `SecurityByDefaultTests.cs` showing the attack and how the parser handles it.