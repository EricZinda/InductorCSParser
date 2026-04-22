namespace InductorParser;

// Picks which lexer the parser drives input through. Each Token a rule
// reads is one of these units, and that choice changes how multi-rune
// content (emoji ZWJ sequences, combining-mark accents, CRLF, regional-
// indicator flags) shows up to the grammar.
//
//   Grapheme - GraphemeLexer. One Token = one user-perceived character
//       (UAX #29 grapheme cluster). 👨‍👩‍👧‍👦 arrives as a single Token
//       whose Chars span is all seven runes of the family-emoji ZWJ
//       sequence, "é" arrives as one Token containing both runes,
//       and "\r\n" arrives as one Token. This is the default because
//       it matches the unit users actually type and read.
//
//   Rune - RuneLexer. One Token = one Unicode scalar value. The same
//       family emoji arrives as seven separate Tokens, "é" as
//       two Tokens, "\r\n" as two Tokens. Pick this when the grammar
//       needs to inspect or anchor on individual code points (e.g.
//       low-level format work, or a grammar that explicitly handles
//       combining marks).
//
// See docs/UnicodeGotchas.md for the gotchas each mode introduces, and
// docs/UnicodeInternalsArchitecture.md for how the two lexers are built.
public enum InputUnit
{
    Grapheme,
    Rune
}
