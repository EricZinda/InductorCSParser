# Unicode in the Inductor Parser
When you build grammars in the Inductor Parser you don't need to worry about the encoding complexities of Unicode, you build rules around the characters you care about and the engine ensures that:

1) The text stream is normalized into a form that is canonical. Invalid Unicode fails the parse with a `MalformedInput` result.
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
priority = Or(Literal("top"), 
                   Literal("med"), 
                   Literal("low"))
                   .As("priority");

itemText = OneOrMore(And(Not(EndOfLine(eofIsEol:true)), 
                           AnyToken()))
                           .As("itemText");

todoLine = And(Optional(InlineWhitespace()),
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
priority = Or(Literal("máx"), 
                   Literal("med"), 
                   Literal("mín"))
                   .As("priority");

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
priority = Or(Literal("높음"), 
                   Literal("보통"), 
                   Literal("낮음"))
                   .As("priority");
```
Korean characters are composable and decomposable just like `á` can be `á` (`U+00E1`) or base `a` (U+0061) + accent mark (`U+0301`). And, just like the Spanish grammar above, the Korean grammar properly handles both forms and ensures that your rule is written to match the composed version by default.

But Korean also has 2 *more* ways to write a character that is "equivalent", but not exactly the same. They exist for historical reasons and should normally be ignored: ["Note that (c) and (d) are present for compatibility with legacy code pages, and are not required for the representation of Korean."](https://www.unicode.org/faq/korean.html).  If you *do* want them, however, you can use `FormKC` when you compile. Which has other implications described above.

## Error Index

When a rule fails (or when you walk the parse tree on success) the parser tells you where in the *original input* things happened. 

"Where" can mean two different things, because tokens are what the user sees as characters but the input is a .NET `string` of UTF-16 chars. The parser reports both. For plain ASCII the two are the same. Once the input contains a 2-char letter like `𠮷`, which is one character to a reader but two .NET `chars` or a 3-char letter like Devanagari `क्ष`, they aren't.

```csharp
var result = list.Parse("[top] 𠮷田 broke\nBAD");
// First line parses. Second line should start with '[' but starts with 'B'.
// ErrorCharIndex  = 16
// ErrorTokenIndex = 15
// (𠮷 is one token but two chars, so the indices differ from there on)
```

Every Symbol in a successful parse exposes the same pair through `SourceRange`:

```csharp
var result = list.Parse("[top] 𠮷田 fix\n");
// list is OneOrMore(...), a Flatten root, so its children bubble up to the
// top level and result.Tree is null. result.Find walks every top-level
// Symbol, so it works whether the root preserved itself or flattened to a list.
var range = result.Find(itemText)!.SourceRange!.Value;
// Width of the matched item text:
//   range.End.CharIndex  - range.Start.CharIndex  == 7  // 𠮷 contributes 2 chars
//   range.End.TokenIndex - range.Start.TokenIndex == 6  // 𠮷 contributes 1 token
```

Use whichever unit matches what your consumer counts in. Chars for `string.Substring` or an editor diagnostic. Tokens for a `^^^` underline a reader will scan with their eyes.

Now lets look at how the grammar will behave on what might be unexpected Unicode input.

## Unexpected Unicode
There are very few ways to write a truly "illegal" Unicode document. The parser actually rejects those during normalization, failing the parse with a `MalformedInput` result. However, there are many ways the text could be "unexpected", especially for someone new to Unicode. The parser is designed to keep grammars understandable and avoid pitfalls with those.

### Legitimate Ill-formed Input
The parser takes a .Net `String`. If you created your string from a file or a sequence of bytes using any of .Net's UTF encoding types, like:

```CSharp
string text = File.ReadAllText(path, new UTF8Encoding());
string text = Encoding.UTF32.GetString(bytes)

```
... then .Net already made sure any illegal Unicode characters are replaced with a special Unicode character called a "replacement character" (`U+FFFD`). 

Non-Unicode encodings (ASCII, Latin-1, Windows-1252) use a different fallback character: a literal ? (`U+003F`). That's the abstract Encoding class default. Only the Unicode encodings override it to `U+FFFD`.

But if your code doesn't do this, or got a string by some other means, it could contain invalid Unicode sequences. 

In that case, when you call .Parse() using the defaults, the parse fails with a `MalformedInput` outcome. The default FormC normalization catches the ill-formed input before any rule runs, so you read `result.Outcome` to tell it apart from an ordinary grammar mismatch and `result.ErrorMessage` (set from `ParseOptions.MalformedInputTemplate`) for a message you can localize. 

If you decide to go without Normalization at all by calling `Compile(null)` and then `Parse()`, the engine will treat ill-formed code points as separate tokens that you can match specifically by using any Rule that matches specific tokens (e.g. `Token`), or collect them with a range of "any" text in all tokens like `AnyToken` that match literally anything. Those are the only ways you will match them. 

All of these together ensure that your Grammar will not get "confused" by ill-formed input (and will fail if it exists) unless you are truly testing for it.

### Unexpected (Often Non-visible) Characters
There are many characters that are perfectly valid in a Unicode document but might be unexpected to most developers. These surface as their own stand-alone token in the parser and thus will never match any rules looking for *particular* text in your grammar. For example: `Token(' ')` won't match a non-breaking space in a document. 

Just like ill-formed tokens above, the only way you can match these is by putting them in a Rule that matches specific tokens (e.g. `Token`), or by using a rule designed to match literally "any" text like `AnyToken`.

- Bare attaching characters: characters meant to combine with the one before or after, but appearing alone. Examples: a stray combining accent (`U+0301`) without a letter under it, a Zero Width Joiner (`U+200D`) without emoji to glue together, an unpaired regional indicator (the things that compose country flags).
- Invisible formatting characters: don't render as a glyph but still take a position in the text. Examples: zero-width space (`U+200B`), soft hyphen (`U+00AD`), byte-order mark (`U+FEFF`), bidi-direction controls (the characters behind "Trojan Source" attacks).
- Noncharacters: code points Unicode reserved for internal use, not supposed to appear in real text. Examples: `U+FFFE`, `U+FFFF`, and the block `U+FDD0`..`U+FDEF`. One special case: parsing input containing `U+FFFE` under default normalization fails with a `MalformedInput` result, because .NET treats it as a sign of byte-order confusion upstream.
- Private use: code points Unicode set aside for private agreements between apps, with no assigned meaning. Examples: Apple's logo at `U+F8FF`, corporate logo fonts, game icon fonts. Main block is `U+E000`..`U+F8FF`.
- Replacement: a single character, `U+FFFD` (often shown as � or a question mark in a box), inserted by .NET decoders for bytes that weren't valid in the source encoding. Its presence means an upstream decoder swallowed something. The parser exposes `TokenSet.Replacement` to detect or reject these.

## Security-related Concerns
Unicode opens up a few classic ways to attack a parser, see [primer 4](Primer4.md) for a walkthrough.
