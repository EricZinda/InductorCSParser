When you build grammars in the Inductor Parser you don't need to worry about the encoding complexities of Unicode, you build rules around the characters you care about and the engine ensures that:

1) The text stream is normalized into a form that is canonical. Invalid Unicode throws.
2) Characters in your rules are encoded in the same normalized form so they match properly. Rules in non-normalized form throw.
3) Tokens given to your rules are always characters the user (and you!) perceives as a single character (i.e. "Grapheme Clusters") 

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
If we wanted to localize our app into other languages, the built-in rules make sure the specific keyword characters our grammar looks for  will match properly.

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

# Unexpected Unicode
There are very few ways to write a truly "illegal" Unicode document. The parser actually throws an exception during normalization for those cases. However, there are many ways the text could be "unexpected", especially for someone new to Unicode. The parser is designed to keep grammars understandable and avoid pitfalls with those.

## Legitimate Ill-formed Input
The parser takes a .Net `String`. If you created your string from a file or a sequence of bytes using any of .Net's UTF encoding types, like:

```CSharp
string text = File.ReadAllText(path, new UTF8Encoding());
string text = Encoding.UTF32.GetString(bytes)

```
... then .Net already made sure any illegal Unicode characters are replaced with a special Unicode character called a "replacement character" (`U+FFFD`). 

Non-Unicode encodings (ASCII, Latin-1, Windows-1252) use a different fallback character: a literal ? (`U+003F`). That's the abstract Encoding class default. Only the Unicode encodings override it to `U+FFFD`.

But if your code doesn't do this, or got a string by some other means, it could contain invalid Unicode sequences. 

In that case, when you call .Parse() using the defaults, you will get an exception. The default FormC normalization will catch it and throw. 

If you decide to go without Normalization at all by calling `Compile(null)` and then `Parse()`, the engine will treat ill-formed code points as separate tokens that you can match using all of the Rules that match any tokens. And that is the only way you will match them. 

All of these together ensure that your Grammar will not get "confused" by ill-formed input (and will fail if it exists) unless you are truly testing for it.

## Unexpected (Often Non-visible) Characters
There are many characters that are perfectly valid in a Unicode document but might be unexpected by most developers.

BARE ATTACHING CHARACTERS
INVISIBLE FORMATTING CHARACTERS
NONCHARACTERS
PRIVATE USE
REPLACEMENT