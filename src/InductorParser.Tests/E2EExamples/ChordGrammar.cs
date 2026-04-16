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
// form in real chord notation). CiChar and CiWord below build character-
// class or sequence rules that accept both cases.
public static class ChordGrammar
{
    // Accidentals include b and x (and their uppercase pair under IgnoreCase:
    // B is legal after the root via case-fold, same for X). Rare in real
    // input but we have to mirror regex behavior.
    //
    // Field order matters: these RuneSets must appear before Chord below so
    // Build() sees them populated. C# initializes static readonly fields in
    // lexical order, and Build() dereferences these sets; if Chord is first
    // it captures empty default RuneSets and the grammar rejects every input.
    private static readonly RuneSet AccidentalSet =
        RuneSet.Runes("#bB\u266F\u266DxX"); // # b B ♯ ♭ x X

    private static readonly RuneSet RootSet =
        RuneSet.Runes("ABCDEFGabcdefg");

    public static readonly Rule Chord = Build();

    private static Rule Build()
    {
        var root = RuneIn(RootSet);
        var accidental = RuneIn(AccidentalSet);
        var accidentals = ZeroOrMore(accidental);

        // (maj|min|m|dim|°|o|aug|+|sus[24]?|5)?
        // Longest first so "maj" wins over "m", "min" wins over "m".
        var quality1 = Optional(Or(
            CiWord("maj"),
            CiWord("min"),
            CiWord("dim"),
            CiWord("aug"),
            And(CiWord("sus"), Optional(Or(Char('2'), Char('4')))),
            CiChar('m'),
            CiChar('o'),
            Char('\u00B0'),    // °
            Char('+'),
            Char('5')
        ));

        // (6|7|9|11|13)?
        // Try two-digit numbers first so "11" and "13" don't get partial-matched
        // as "1" with nothing to follow.
        var ext1 = Optional(Or(
            CiWord("11"),
            CiWord("13"),
            Char('6'),
            Char('7'),
            Char('9')
        ));

        // (maj|M|Δ|m|ø|°)?
        // IgnoreCase makes M and m equivalent, so CiChar('m') covers both.
        var quality2 = Optional(Or(
            CiWord("maj"),
            CiChar('m'),
            Char('\u0394'),    // Δ
            Char('\u00F8'),    // ø
            Char('\u00B0')     // °
        ));

        // (7|9|11|13)?
        var ext2 = Optional(Or(
            CiWord("11"),
            CiWord("13"),
            Char('7'),
            Char('9')
        ));

        // (add[2469]|add1[13]|b5|#5|b9|#9|#11|b13|no[357]|sus[24]?|alt)*
        // Ordering: "add1" before "add" (longer prefix match for PEG); "#11"
        // before "#5"/"#9" (again, longer first); "b13" before "b5"/"b9".
        // "sus[24]?" matches "sus", "sus2", or "sus4".
        var addMod = Or(
            And(CiWord("add1"), Or(Char('1'), Char('3'))),
            And(CiWord("add"), Or(Char('2'), Char('4'), Char('6'), Char('9'))),
            CiWord("b13"),
            CiWord("#11"),
            CiWord("b5"),
            CiWord("b9"),
            CiWord("#5"),
            CiWord("#9"),
            And(CiWord("no"), Or(Char('3'), Char('5'), Char('7'))),
            And(CiWord("sus"), Optional(Or(Char('2'), Char('4')))),
            CiWord("alt")
        );
        var addMods = ZeroOrMore(addMod);

        // (\/[A-Ga-g][#b♯♭x]*)?
        var slashBass = Optional(And(
            Char('/'),
            root,
            accidentals
        ));

        return And(
            root,
            accidentals,
            quality1,
            ext1,
            quality2,
            ext2,
            addMods,
            slashBass,
            Eof()
        );
    }

    private static Rule CiChar(char c)
    {
        if (c >= 'a' && c <= 'z')
            return RuneIn(RuneSet.Runes(new string(new[] { c, (char)(c - 32) })));
        if (c >= 'A' && c <= 'Z')
            return RuneIn(RuneSet.Runes(new string(new[] { c, (char)(c + 32) })));
        return Char(c);
    }

    private static Rule CiWord(string word)
    {
        var parts = new Rule[word.Length];
        for (int i = 0; i < word.Length; i++)
            parts[i] = CiChar(word[i]);
        return And(parts);
    }
}
