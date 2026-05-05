# Unicode Edge Cases
When you build grammars in the Inductor Parser you don't need to worry about the encoding complexities of Unicode, you build rules around the characters you care about and the engine ensures that:

1) The text stream is normalized into a form that is canonical. Invalid Unicode throws.
2) Characters in your rules are encoded in the same normalized form so they match properly. Rules in non-normalized form throw at compile time.
3) Tokens given to your rules are always characters the user (and you!) perceives as a single character (i.e. "Grapheme Clusters") and match exactly that character in the text.

It is designed so you can safely write grammars over Unicode text without having to be a Unicode expert. 

Let's imagine we're building a parser for a todo list program that has three priority levels: `top`, `med`, and `low`. Todo items are one per line:

```
[top] Fix the database connection bug
[med] Update the README with new examples
[med] Refactor the parser internals
[low] Awaiting design review for new auth flow
```
The grammar would be:
```
priority = FirstOf(Literal("top"), 
                   Literal("med"), 
                   Literal("low")).As("priority");

itemText = OneOrMore(AllOf(Not(EndOfLine(eofIsEol:true)), 
                           AnyToken())).As("itemText");

todoLine = AllOf(Optional(InlineWhitespace()),
                 Token('['), 
                 Optional(InlineWhitespace()),
                 priority,
                 Optional(InlineWhitespace()),
                 Token(']'), 
                 Optional(InlineWhitespace()),
                 itemText,
                 EndOfLine(eofIsEol:true));

list = OneOrMore(todoLine);
```
## Arbitrary Unicode In Content
The built-in rules we've used here play well with someone including any Unicode text in their todo list:
- `InlineWhitespace()`: All forms of Unicode whitespace are accepted
- `EndOfLine()`: All Unicode single character and multi-character ("\r\n") end of lines are supported
- `AnyToken()`: Accepts all Unicode input, so they are free to write any Unicode characters in their todo item

## Arbitrary Unicode in Rules
If we wanted to localize our app into other languages, the built-in rules make sure the specific keyword characters our grammar looks for will match properly.

Let's do Spanish first:

```
// Spanish
// [máx] Fix the database connection bug
// [med] Update the README with new examples
// [mín] Refactor the parser internals
priority = FirstOf(Literal("máx"), 
                   Literal("med"), 
                   Literal("mín")).As("priority");

```
The normal Unicode gotcha here is that many accented characters (and all of the Spanish ones) can be written as a single character like `á` (`U+00E1`) or by starting with the base `a` (U+0061) and following with the accent (`U+0301`) like this: `á`. It looks the same but is really two Unicode code points, and that's the gotcha. Do you have to put both in every rule? Or just one? Which one?

The Inductor Parser ensures you don't introduce bugs by:
1) Normalizing the incoming text to a "composed" (i.e. single character) form by default
2) Throwing an exception if you use the other form in your rules

Thus, you're able to write simple, readable rules that match *all* of the ways of encoding the character in the original Unicode. If you use options on the parser to select other normalized forms (see next section), it will ensure you are using those forms for your Rules, too. 

Let's walk through the different forms next.

### Compatibility vs. Canonical
You can choose to normalize your grammar in a few different ways if you don't like the default. To understand them, first know that Unicode defines two types of "equivalence" among characters:

The first type, "Canonical",  means ["...characters which represent the same abstract character, and which when correctly displayed should always have the same visual appearance and behavior"](https://unicode.org/reports/tr15/#Canon_Compat_Equivalence). The "smaller" side is called "composed" and the "broken apart side" is called "decomposed". Most modern editors write the "composed" form and thus this is the default in the parser so that rules are easier to write.

Examples:
```
composed ↔ decomposed
     Ç   ↔   C + ◌̧
    가   ↔   ᄀ +ᅡ
```

The second type, "Compatible", means ["...characters which represent the same abstract character (or sequence of abstract characters), but which may have distinct visual appearances or behaviors"](https://unicode.org/reports/tr15/#Canon_Compat_Equivalence). Some of these are historical or for compatibility with old encoding schemes. It chooses one form and converts other characters which are "semantically equivalent" to it. 

Examples:
```
ℍ	→	H
①	→	1
ｶ	→	カ
i⁹	→	i9
```
You can see it is a weaker type of equivalence that attempts to get at "meaning" more. The parser does not choose this by default because it would allow matching of characters that look very different and is a weaker form of equivalence. The standard recommends care when deciding to use it for this reason, but you can choose it as an option.

Canonical and Compatible can be combined to create 4 different ways to normalize the text: 

- (default) Composed (`NormalizationForm.FormC`)
- Decomposed (`NormalizationForm.FormD`)
- Composed and Compatible (`NormalizationForm.FormKC`)
- Decomposed and Compatible (`NormalizationForm.FormKD`)

 You choose the one you want as an option on `Rule.Compile()`, and every rule that takes a character will ensure you have written the proper form to match the form of normalization you choose.

With that in mind, let's try Korean in our example:
```
// Hangul (Korean)
// [높음] Fix the database connection bug
// [보통] Update the README with new examples
// [낮음] Refactor the parser internals
priority = FirstOf(Literal("높음"), 
                   Literal("보통"), 
                   Literal("낮음")).As("priority");
```
Korean characters are composable and decomposable just like `á` can be `á` (`U+00E1`) or base `a` (U+0061) + accent mark (`U+0301`). And, just like the Spanish grammar above, the Korean grammar properly handles both forms and ensures that your rule is written to match the composed version by default.

But Korean also has 2 *more* ways to write a character that is "equivalent", but not exactly the same. They exist for historical reasons and should normally be ignored: ["Note that (c) and (d) are present for compatibility with legacy code pages, and are not required for the representation of Korean."](https://www.unicode.org/faq/korean.html).  If you *do* want them, however, you can use `FormKC` when you compile. Which has other implications described above.

Now lets look at how the grammar will behave on malformed Unicode input.

## Unexpected Unicode
There are very few ways to write a truly "illegal" Unicode document. The parser actually throws an exception during normalization for those cases. However, there are many ways the text could be "unexpected", especially for someone new to Unicode. The parser is designed to keep grammars understandable and avoid pitfalls with those.

### Legitimate Ill-formed Input
The parser takes a .Net `String`. If you created your string from a file or a sequence of bytes using any of .Net's UTF encoding types, like:

```CSharp
string text = File.ReadAllText(path, new UTF8Encoding());
string text = Encoding.UTF32.GetString(bytes)

```
... then .Net already made sure any illegal Unicode characters are replaced with a special Unicode character called a "replacement character" (`U+FFFD`). 

Non-Unicode encodings (ASCII, Latin-1, Windows-1252) use a different fallback character: a literal ? (`U+003F`). That's the abstract Encoding class default. Only the Unicode encodings override it to `U+FFFD`.

But if your code doesn't do this, or got a string by some other means, it could contain invalid Unicode sequences. 

In that case, when you call .Parse() using the defaults, you will get an exception. The default FormC normalization will catch it and throw. 

If you decide to go without Normalization at all by calling `Compile(null)` and then `Parse()`, the engine will treat ill-formed code points as separate tokens that you can match specifically by using any Rule that matches specific tokens (e.g. `Token`), or collect them with a range of "any" text in all tokens like `AnyToken` that match literally anything. Those are the only ways you will match them. 

All of these together ensure that your Grammar will not get "confused" by ill-formed input (and will fail if it exists) unless you are truly testing for it.

### Unexpected (Often Non-visible) Characters
There are many characters that are perfectly valid in a Unicode document but might be unexpected to most developers. These surface as their own stand-alone token in the parser and thus will never match any rules looking for *particular* text in your grammar. For example: `Token(' ')` won't match a non-breaking space in a document. 

Just like ill-formed tokens above, the only way you can match these is by putting them in a Rule that matches specific tokens (e.g. `Token`), or by using a rule designed to match literally "any" text like `AnyToken`.

- Bare attaching characters: characters meant to combine with the one before or after, but appearing alone. Examples: a stray combining accent (`U+0301`) without a letter under it, a Zero Width Joiner (`U+200D`) without emoji to glue together, an unpaired regional indicator (the things that compose country flags).
- Invisible formatting characters: don't render as a glyph but still take a position in the text. Examples: zero-width space (`U+200B`), soft hyphen (`U+00AD`), byte-order mark (`U+FEFF`), bidi-direction controls (the characters behind "Trojan Source" attacks).
- Noncharacters: code points Unicode reserved for internal use, not supposed to appear in real text. Examples: `U+FFFE`, `U+FFFF`, and the block `U+FDD0`..`U+FDEF`. One special case: parsing input containing `U+FFFE` under default normalization throws, because .NET treats it as a sign of byte-order confusion upstream.
- Private use: code points Unicode set aside for private agreements between apps, with no assigned meaning. Examples: Apple's logo at `U+F8FF`, corporate logo fonts, game icon fonts. Main block is `U+E000`..`U+F8FF`.
- Replacement: a single character, `U+FFFD` (often shown as � or a question mark in a box), inserted by .NET decoders for bytes that weren't valid in the source encoding. Its presence means an upstream decoder swallowed something. The parser exposes `TokenSet.Replacement` to detect or reject these.

## Security-related Concerns
Unicode opens up a few classic ways to attack a parser. The good news is that grammars written naturally already block most of them. The one to be aware of is whether your rule defines what's *allowed* (your rule must match for input to be accepted) or what's *blocked* (your rule must match for input to be rejected). The default behavior is right for "allowed" rules. For "blocked" rules, you sometimes need to do a little extra work.

- Trojan Source: an attacker hides a bidi-direction character (like `U+202E`) in input so an editor renders the text in one order while the parser sees a different one. The same source code can look like one thing to a reviewer and mean another to a compiler. Your grammar isn't fooled because the parser doesn't reorder anything based on bidi controls. It just sees the raw character sequence in the input, in the actual logical order the attacker submitted. The visual rearrangement an editor would have shown to a human reviewer doesn't exist as far as the grammar is concerned.

- Lookalike characters: some characters look almost identical to common letters but are different code points. `𝐀` (math-bold A), `Ａ` (fullwidth A), and hundreds of others all look like A but aren't. An attacker writes `ｓｅｌｅｃｔ` to slip past a SQL filter, or `𝐚dmin` to register an account that looks like admin. For "allowed" rules, the default `FormC` is good because the lookalike doesn't match. For "blocked" rules, compile with `FormKC` instead. It turns lookalikes into plain letters before the rule runs.

- Invisible characters: zero-width spaces, soft hyphens, BOMs, and similar characters don't render but still take up a position in the text. An attacker writes `ki<ZWS>ll` to slip a banned word past a profanity filter, or registers a name that displays as `admin` but compares as different. For "allowed" rules, the default is good — the invisibles don't match. For "blocked" rules, no normalization form strips invisibles, so you have to filter them out yourself before parsing.

- Homoglyphs (the one the parser does NOT defend against by default): Latin `a` (`U+0061`) and Cyrillic `а` (`U+0430`) look identical but are different code points from different scripts. Greek `α` and several other scripts do the same for various letters. Anywhere your grammar accepts letters from arbitrary scripts (`Identifier()`, `OneOf(TokenSet.Letters)`, or any other rule that takes a broad letter set) an attacker can mix scripts to make text that looks legitimate but compares as different. The fix: restrict your grammar to one script's letters, use `TokenSet.Ascii.Letters` for ASCII-only, or build a custom set covering the script(s) you actually want to support.

For each of these, there's a focused test in `SecurityByDefaultTests.cs` showing the attack and how the parser handles it.
