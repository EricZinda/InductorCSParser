
When you build grammars in the Inductor Parser you don't need to worry about the encoding complexities of Unicode, you build rules around the actual characters people see. The engine ensures that:

1) The text stream is normalized into a form that is canonical. Invalid Unicode throws.
2) Characters in your rules are encoded in the same canonical form so they match properly. Rules in non-canonical form throw.
3) Tokens given to your rules are always characters the user (and you!) perceives 

It is designed so that you can safely write grammars without understanding it all and properly write grammars for Unicode text, If you want to understand how, read on.

# Background: Unicode "Grapheme" Character Sequences
The atoms of Unicode are 21 bit *code points* (called `runes` in .Net). Unicode encodes the enormous number of actual characters in the world by allowing a sequence of one or more code points to represent a single character (called a *grapheme* or *grapheme cluster*). There are 4 different ways code points are used or combined to represent graphemes:

1. Single character graphemes: (not a formal Unicode term, since Unicode doesn't have one for this category) like `A` (all of ASCII) have a single rune that represents them (U+0041 A) and thats the only way you'll ever see them.

2. Combining character sequences: like `क्ष` don't have a single defining character but are always represented as the same sequence (U+0915 क, U+094D ्, U+0937 ष).

3. Canonically equivalent sequences are characters that can be represented like #1 *or* #2. Like `é`, they have a single Rune that represents them (U+00E9 é) *and* can be represented by a sequence of Runes that "build" the character (U+0065 e, U+0301 ◌́). Unicode says that the sequence should be shown to the user using the same grapheme (U+00E9 é) since it represents the same thing.

4. Emoji sequences: are "Lego blocks" of Graphemes that can be built out of combinations of characters using certain rules. They are an open-ended and growing set of graphemes, like `👨‍👩‍👧` (U+1F468 👨, U+200D ZWJ, U+1F469 👩, U+200D ZWJ, U+1F467 👧). These are more like a programming language for building Graphemes.

All 4 categories are different ways that a single user visible character gets formed, but they all end up the same: building a single user visible character called a grapheme. The Inductor Parser rules are thus designed around graphemes. 

The rules for where one grapheme ends and the next begins in a stream of code points are defined by UAX #29.

Note that there are plenty of code point and code point sequences that do not represent Graphemes (user perceived characters), per se. The valid ones are split by the same rules (UAX #29) and represented the same way in the parser, even though they don't encode a user visible character. Invalid ones throw when encountered.

# Step 1: Text Normalization
Some graphemes in Unicode can be represented in more than one way. To avoid forcing grammar writers to include all the variations, the parser normalizes the input text first.

The parser normalizes input to a canonical form called Unicode Normalization Form C by default. "C" stands for "composed". It takes a string and produces a canonical equivalent that is "as composed as possible", meaning shrunked down into fewer (ideally 1) code point and written in a canonical order. This is the default because most people don't want to write their grammars anticipating all the different ways a single Unicode character might be built, they want to depend on the ultimate form of it. Most input is already composed (web content, modern source files, anything produced by normal typing on modern OSes), so the normalization pass is usually a cheap scan with no textual rewrite. It fixes potential bugs for denormalized input when grammars don't account for it.

Unicode defines four normalization forms. The library uses the .NET enum directly to access the Unicode forms. `NormalizationForm.FormC` is composed (the default). `FormD` is decomposed. There are also two "compatibility" variants (a composed and decomposed version) that take characters that might look a bit different and make them the same if they "mean" the same thing: Ａ → A, math-bold 𝐀 → A, Ⅸ → "IX", ligature ﬁ → fi. FormC is true to what the original input has, just canonicalized. It has characters that are visually different stay distinct and that's why we've chosen it as the default.

The form is a grammar-level decision committed at `Compile` time, not a per-parse option. You choose it when you compile the grammar:

```csharp
var grammar = And(...).Compile();                              // FormC default
var grammar = And(...).Compile(NormalizationForm.FormKC);      // explicit FormKC
var grammar = And(...).Compile(null);                          // no normalization
```

It is chosen at the grammar level (as opposed to per parse) because at the moment you write `Token("é")` you've committed to a specific Unicode form for that literal. If a later `Parse` ran the grammar against decomposed input under FormD, the lexer would hand back the two-rune `e + U+0301` form and your `Token("é")` rule (looking for the single-rune U+00E9) would silently never match. The form is part of the grammar's identity, so it lives on the grammar.

`Compile(form)` validates every rule that has a literal (e.g. `Token`, `Literal`, `LiteralIgnoreAsciiCase`) against the chosen form. All literals whose text isn't already in that form gets reported in a single `InvalidOperationException` listing every offender and the suggested normalized form, so the author can fix them all in one pass. Validation is skipped when the form is `null`.

Callers who want character-exact round-trippability (where `tree.ToString()` must match the original input string character for character) compile with `null`. The tradeoff is that input in the "wrong" form will silently fail exact-match rules that are written for a specific composition.

Positions reported in `ParseResult` (`ErrorCharIndex` and its derived line/column/token properties) are always into the caller's original input string, never into the normalized form. The parser normalizes internally for the lexer to operate on, then translates any failure offset back to original coordinates at the boundary. When normalization is a no-op and .NET returns the original string reference, translation is skipped. When input got rewritten, or when the runtime returns a distinct but equivalent string, the parser maps the failure position back to the original string. This mapping is paid only on failure or budget-abort paths, not on success.

One consequence to know about: when the failure lands inside a combining character sequence that got composed (or vice-versa), the reported position is the start of that sequence in the original string, not a phantom position mid-sequence. That matches what an editor wants for highlight-the-bad-token diagnostics anyway. You can't put a caret between an 'e' and its combining acute in any reasonable UI. This inherits the pre-.NET 5 `StringInfo` caveat: a handful of real grapheme clusters segment incorrectly on legacy runtimes, and the translator uses the same primitive, so whatever the lexer saw, the translator sees.


# Improper Unicode

# Inductor Parser's Approach In Detail
Now that we've reviewed Unicode a bit, I can explain how the Inductor Parser works in a bit more technical detail:

When parsing, Inductor Parser first ensures that the string is simplified so that all forms are represented in a single canonical form: Sequences that can map to a single rune are converted to it, and sequences that can be in different orders are converted to a canonical one. This is called FormC in Unicode and is the default in the Inductor Parser.

Then, it uses the rules in UAX #29 to break up the canonical string into Graphemes. Each sequence of one or more Runes that represents a grapheme becomes exactly one `token`.

The combination means that your rules can be written in one way and still match all the different ways a single document can be encoded validly in Unicode.

