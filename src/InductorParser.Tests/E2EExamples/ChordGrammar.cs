using InductorParser;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Chord grammar: the Inductor Parser replacement for the chord-detection
// regex used by UnityTabs, a private real-world side project that parses
// guitar tablature files. The regex below is reproduced verbatim from the
// UnityTabs source so the grammar can be checked for behavioral parity.
//
// The original (UnityTabs Parser.cs line 18-20):
//
//   @"^[A-Ga-g][#b♯♭x]*
//     (maj|min|m|dim|°|o|aug|\+|sus[24]?|5)?
//     (6|7|9|11|13)?
//     (maj|M|Δ|m|ø|°)?
//     (7|9|11|13)?
//     (add[2469]|add1[13]|b5|#5|b9|#9|#11|b13|no[357]|sus[24]?|alt)*
//     (\/[A-Ga-g][#b♯♭x]*)?$"
//
// with RegexOptions.IgnoreCase.
//
// Inductor Parser vs regex: regex's (a|b) alternation is "eager but can
// backtrack out," while Inductor Parser's FirstOf(a, b) commits to the
// first match that succeeds. In every alternation below, the branches
// either have disjoint first characters (a/b/c/...) or are ordered
// longest-first (maj before m, 11/13 before 1) so the commit happens on
// the right branch.
//
// Case-insensitivity: the regex's IgnoreCase flag matches ASCII letters
// in either case, and in practice that's all the matching we need here
// (the special symbols ♯♭°øΔ have no case variants, so case
// invariance wouldn't change them either way). Every case-insensitive keyword, including single-letter
// ones like "m" and "o", goes through the library's LiteralIgnoreAsciiCase
// leaf so each word is one transaction.
public static class ChordGrammar
{
    public static readonly Rule Chord = Build();

    private static Rule Build()
    {
        var root = OneOf("ABCDEFGabcdefg");

        // Accidentals include b and x (and their uppercase pair under
        // IgnoreCase: B is legal after the root via the case-insensitive
        // match, same for X). Rare in real input but we have to mirror
        // regex behavior.
        var accidental = OneOf("#bB♯♭xX");

        // (maj|min|m|dim|°|o|aug|+|sus[24]?|5)?
        // Longest first so "maj" wins over "m", "min" wins over "m".
        var quality1 = FirstOf(
            LiteralIgnoreAsciiCase("maj"),
            LiteralIgnoreAsciiCase("min"),
            LiteralIgnoreAsciiCase("dim"),
            LiteralIgnoreAsciiCase("aug"),
            AllOf(LiteralIgnoreAsciiCase("sus"), Optional(OneOf("24"))),
            LiteralIgnoreAsciiCase("m"),
            LiteralIgnoreAsciiCase("o"),
            Token('°'),
            Token('+'),
            Token('5')
        );

        // (6|7|9|11|13)?
        var ext1 = FirstOf(
            LiteralIgnoreAsciiCase("11"),
            LiteralIgnoreAsciiCase("13"),
            Token('6'),
            Token('7'),
            Token('9')
        );

        // (maj|M|Δ|m|ø|°)?
        // IgnoreCase makes M and m equivalent, so LiteralIgnoreAsciiCase("m") covers both.
        var quality2 = FirstOf(
            LiteralIgnoreAsciiCase("maj"),
            LiteralIgnoreAsciiCase("m"),
            Token('Δ'),
            Token('ø'),
            Token('°')
        );

        // (7|9|11|13)?
        var ext2 = FirstOf(
            LiteralIgnoreAsciiCase("11"),
            LiteralIgnoreAsciiCase("13"),
            Token('7'),
            Token('9')
        );

        // (add[2469]|add1[13]|b5|#5|b9|#9|#11|b13|no[357]|sus[24]?|alt)*
        // Ordering: "add1" before "add" (longer prefix match for Inductor Parser). "#11"
        // before "#5"/"#9" (again, longer first). "b13" before "b5"/"b9".
        // "sus[24]?" matches "sus", "sus2", or "sus4".
        var addMod = FirstOf(
            AllOf(LiteralIgnoreAsciiCase("add1"), OneOf("13")),
            AllOf(LiteralIgnoreAsciiCase("add"), OneOf("2469")),
            LiteralIgnoreAsciiCase("b13"),
            LiteralIgnoreAsciiCase("#11"),
            LiteralIgnoreAsciiCase("b5"),
            LiteralIgnoreAsciiCase("b9"),
            LiteralIgnoreAsciiCase("#5"),
            LiteralIgnoreAsciiCase("#9"),
            AllOf(LiteralIgnoreAsciiCase("no"), OneOf("357")),
            AllOf(LiteralIgnoreAsciiCase("sus"), Optional(OneOf("24"))),
            LiteralIgnoreAsciiCase("alt")
        );

        // (\/[A-Ga-g][#b♯♭x]*)?
        var slashBass = AllOf(Token('/'), root, ZeroOrMore(accidental));

        return AllOf(
            root,
            ZeroOrMore(accidental),
            Optional(quality1),
            Optional(ext1),
            Optional(quality2),
            Optional(ext2),
            ZeroOrMore(addMod),
            Optional(slashBass),
            Eof()
        );
    }
}
