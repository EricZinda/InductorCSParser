
When you build grammars in the Inductor Parser you don't need to worry about the encoding complexities of Unicode, you build rules around the actual characters people see. The engine ensures that:

1) The text stream is normalized into a form that is canonical. Invalid Unicode throws.
2) Characters in your rules are encoded in the same canonical form so they match properly. If not, compiling a grammar throws.
3) Tokens given to your rules are characters the user (and you!) perceives 

You can safely write grammars without understanding it all, but if you want to, read on.

# Unicode "Grapheme" Character Sequences
The atoms of Unicode are 21 bit *code points* (called `runes` in .Net). Unicode encodes the enormous number of actual characters in the world by allowing a sequence of one or more code points to represent a single character (called a *grapheme* or *grapheme cluster*). There are 4 different ways code points are used or combined to represent graphemes:

1. Single character graphemes: (not a formal Unicode term, since Unicode doesn't have one for this category) like `A` (all of ASCII) have a single rune that represents them (U+0041 A) and thats the only way you'll ever see them.

2. Combining character sequences: like `क्ष` don't have a single defining character but are always represented as the same sequence (U+0915 क, U+094D ्, U+0937 ष).

3. Canonically equivalent sequences are characters that can be represented like #1 *or* #2. Like `é`, they have a single Rune that represents them (U+00E9 é) *and* can be represented by a sequence of Runes that "build" the character (U+0065 e, U+0301 ◌́). Unicode says that the sequence should be shown to the user using the same grapheme (U+00E9 é) since it represents the same thing.

4. Emoji sequences: are "Lego blocks" of Graphemes that can be built out of combinations of characters using certain rules. They are an open-ended and growing set of graphemes, like `👨‍👩‍👧` (U+1F468 👨, U+200D ZWJ, U+1F469 👩, U+200D ZWJ, U+1F467 👧). These are more like a programming language for building Graphemes.

All 4 categories are different ways that a single user visible character gets formed, but they all end up the same: building a single user visible character called a grapheme. The Inductor Parser rules are thus designed around graphemes. 

The rules for where one grapheme ends and the next begins in a stream of code points are defined by UAX #29.

Note that there are plenty of code point and code point sequences that do not represent Graphemes. The valid ones are split by the same rules (UAX #29) and represented the same way in the parser, even though they don't encode a user visible character. Invalid ones throw when encountered.

# Text Normalization
- You pick the way you want to normalize, and rules throw if they aren't written right. Setting null is a freeforall
When parsing there is another property worth considering that is orthogonal to how graphemes are built. Some graphemes are considered "equivalent" and this is defined by another Unicode document: Unicode Standard Annex #15 (UAX #15).

5. Compatibility-equivalent sequences: Characters that have different ways to represent them, but they have the same meaning. Roman numeral `Ⅸ` and the two ASCII characters `IX`,  `²`and `2`, for example. These are different graphemes, but Unicode says they are "equivalent".

# Improper Unicode

# Inductor Parser's Approach In Detail
Now that we've reviewed Unicode a bit, I can explain how the Inductor Parser works in a bit more technical detail:

When parsing, Inductor Parser first ensures that the string is simplified so that all forms are represented in a single canonical form: Sequences that can map to a single rune are converted to it, and sequences that can be in different orders are converted to a canonical one. This is called FormC in Unicode and is the default in the Inductor Parser.

Then, it uses the rules in UAX #29 to break up the canonical string into Graphemes. Each sequence of one or more Runes that represents a grapheme becomes exactly one `token`.

The combination means that your rules can be written in one way and still match all the different ways a single document can be encoded validly in Unicode.

