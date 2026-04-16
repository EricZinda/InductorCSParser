using InductorParser;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Chord grammar: the PEG replacement for UnityTabs' ChordRegex.
//
// Mirrors UnityTabs Parser.cs line 18-20:
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
// PEG vs regex: regex's (a|b) alternation is "eager but can backtrack out,"
// while PEG Or(a, b) commits to the first match that succeeds. In every
// alternation below, the branches either have disjoint first characters
// (a/b/c/...) or are ordered longest-first (maj before m, 11/13 before 1)
// so the commit happens on the right branch.
//
// Case-insensitivity: the regex's IgnoreCase flag folds ASCII letters only
// in practice here (the special symbols ♯♭°øΔ only appear in their printed
// form in real chord notation). Every case-folded keyword, including
// single-letter ones like "m" and "o", goes through the library's
// LiteralIgnoreAsciiCase primitive so each word is one transaction.
public static class ChordGrammar
{
    public static readonly Rule Chord = Build();

    private static Rule Build()
    {
        var root = RuneIn("ABCDEFGabcdefg");

        // Accidentals include b and x (and their uppercase pair under
        // IgnoreCase: B is legal after the root via case-fold, same for X).
        // Rare in real input but we have to mirror regex behavior.
        var accidental = RuneIn("#bB♯♭xX");

        // (maj|min|m|dim|°|o|aug|+|sus[24]?|5)?
        // Longest first so "maj" wins over "m", "min" wins over "m".
        var quality1 = Or(
            LiteralIgnoreAsciiCase("maj"),
            LiteralIgnoreAsciiCase("min"),
            LiteralIgnoreAsciiCase("dim"),
            LiteralIgnoreAsciiCase("aug"),
            And(LiteralIgnoreAsciiCase("sus"), Optional(RuneIn("24"))),
            LiteralIgnoreAsciiCase("m"),
            LiteralIgnoreAsciiCase("o"),
            Char('°'),
            Char('+'),
            Char('5')
        );

        // (6|7|9|11|13)?
        // Try two-digit numbers first so "11" and "13" don't get partial-matched
        // as "1" with nothing to follow.
        var ext1 = Or(
            LiteralIgnoreAsciiCase("11"),
            LiteralIgnoreAsciiCase("13"),
            Char('6'),
            Char('7'),
            Char('9')
        );

        // (maj|M|Δ|m|ø|°)?
        // IgnoreCase makes M and m equivalent, so LiteralIgnoreAsciiCase("m") covers both.
        var quality2 = Or(
            LiteralIgnoreAsciiCase("maj"),
            LiteralIgnoreAsciiCase("m"),
            Char('Δ'),
            Char('ø'),
            Char('°')
        );

        // (7|9|11|13)?
        var ext2 = Or(
            LiteralIgnoreAsciiCase("11"),
            LiteralIgnoreAsciiCase("13"),
            Char('7'),
            Char('9')
        );

        // (add[2469]|add1[13]|b5|#5|b9|#9|#11|b13|no[357]|sus[24]?|alt)*
        // Ordering: "add1" before "add" (longer prefix match for PEG); "#11"
        // before "#5"/"#9" (again, longer first); "b13" before "b5"/"b9".
        // "sus[24]?" matches "sus", "sus2", or "sus4".
        var addMod = Or(
            And(LiteralIgnoreAsciiCase("add1"), RuneIn("13")),
            And(LiteralIgnoreAsciiCase("add"), RuneIn("2469")),
            LiteralIgnoreAsciiCase("b13"),
            LiteralIgnoreAsciiCase("#11"),
            LiteralIgnoreAsciiCase("b5"),
            LiteralIgnoreAsciiCase("b9"),
            LiteralIgnoreAsciiCase("#5"),
            LiteralIgnoreAsciiCase("#9"),
            And(LiteralIgnoreAsciiCase("no"), RuneIn("357")),
            And(LiteralIgnoreAsciiCase("sus"), Optional(RuneIn("24"))),
            LiteralIgnoreAsciiCase("alt")
        );

        // (\/[A-Ga-g][#b♯♭x]*)?
        var slashBass = And(Char('/'), root, ZeroOrMore(accidental));

        return And(
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
