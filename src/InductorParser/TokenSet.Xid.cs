using System;
using System.Globalization;

namespace InductorParser;

// XID identifier support for TokenSet.
//
// XID_Start and XID_Continue are Unicode properties defined in UAX #31
// (Unicode Identifier and Pattern Syntax). They describe which code points
// may begin an identifier (XID_Start) and which may continue one
// (XID_Continue). Spec: https://www.unicode.org/reports/tr31/ .
//
// UAX #31 R1 defines a default identifier syntax as the pattern
// XID_Start XID_Continue*. Python and Rust build on this shape; C#,
// ECMAScript, Java, Swift, and other languages have nearby but not
// identical profiles. Grammars built on this parser get the UAX #31-shaped
// rule via Rules.Identifier(), which wires these two sets into the
// combinator (see Rules.cs).
//
// UAX #31 R4 (NFC equivalence) says two identifiers are equal if their
// NFC normalizations are equal. That's handled elsewhere: ParseOptions
// normalizes input to NFC by default before the lexer runs, so "café"
// precomposed and "café" as e + combining acute produce the same
// flattened string automatically. This file is only about R1.
//
// Why this file is split out from TokenSet.cs
//
// Most of XID comes from Unicode's General_Category property, which the
// .NET BCL exposes for free via TokenSet.Category(...). XID_Start is
// mostly Lu+Ll+Lt+Lm+Lo+Nl (the Letter and LetterNumber categories).
// XID_Continue adds Mn+Mc+Nd+Pc (combining marks, decimal digits,
// connector punctuation).
//
// The parts of XID that AREN'T derivable from General_Category are a
// small set of additions (code points that are symbols or marks by
// category but semantically act as identifier characters, like SCRIPT
// CAPITAL P) and a small set of exclusions (code points that are
// letters by category but whose NFKC decomposition would break
// identifier-ness, like the Arabic ligature "peace be upon him"
// (U+FDFA), which decomposes into a phrase of letters and spaces).
// Those small add/remove lists are what fill this file. Splitting
// them out keeps the core set algebra in TokenSet.cs from getting
// buried under a hundred lines of Unicode-data tables.
//
// How the exception tables are structured
//
// Four arrays: additions for Start, exclusions for Start, additions
// for Continue, exclusions for Continue. The final composition is
//
//   XidStart    = ((L+Nl) | XidStartAdds)              & ~XidStartExclusions
//   XidContinue = ((L+Nl+Mn+Mc+Nd+Pc) | XidContinueAdds) & ~XidContinueExclusions
//
// where the General_Category parts come from the BCL and the add/remove
// arrays are from DerivedCoreProperties.txt and PropList.txt at the Unicode
// version listed below. Exact coverage follows the Unicode version in the
// runtime's General_Category tables plus these exception tables.
//
// Version drift
//
// The General_Category parts track whatever Unicode version the .NET
// BCL ships. The hand-coded exception tables here are from Unicode
// 17.0. When a new Unicode release adds a new Other_ID_Start code point
// or changes the NFKC-exclusion set, the BCL catches up automatically
// but these arrays don't. On each Unicode release, re-run the diff
// against the new UCD files and
// update. Not automated, but the header comment makes the dependency
// visible so it gets noticed.
//
// Profile extensions (leading underscore, dollar sign, etc.)
//
// Strictly per UAX #31, "_" is General_Category Pc (connector
// punctuation). That puts it in XID_Continue but NOT in XID_Start.
// Different languages profile the base set in different ways: C#,
// Python, and Rust add "_" to Start, ECMAScript adds "_" and "$" to
// both positions, Raku adds "-" and "'" mid-word. The tables in this
// file stay strict-spec. Callers extend via the extraStartRunes and
// extraBodyRunes parameters on Rules.Identifier. See
// docs/UnicodeGotchas.md for language-by-language recipes.
public readonly partial struct TokenSet
{
    // Source: Unicode 17.0 XID_Start (DerivedCoreProperties.txt) minus
    // General_Category L + Nl. Code points in XID_Start that the BCL
    // doesn't report as Letter or LetterNumber.
    //
    // To regenerate: diff XID_Start against the union of Lu+Ll+Lt+Lm+Lo+Nl
    // from DerivedCoreProperties.txt / DerivedGeneralCategory.txt.
    // https://www.unicode.org/Public/UCD/latest/ucd/DerivedCoreProperties.txt
    private static readonly (int Low, int High)[] XidStartAdds =
    {
        (0x1885, 0x1886),   // MONGOLIAN LETTER ALI GALI BALUDA..THREE BALUDA (Mn)
        (0x2118, 0x2118),   // SCRIPT CAPITAL P (Sm)
        (0x212E, 0x212E),   // ESTIMATED SYMBOL (So)
    };

    // Source: Unicode 17.0. Code points in General_Category L + Nl that
    // AREN'T in XID_Start. These are code points the BCL reports as
    // letters but UAX #31 excludes from identifier starts because their
    // NFKC decomposition would produce a sequence that isn't a valid
    // identifier start (typically because it includes a space).
    private static readonly (int Low, int High)[] XidStartExclusions =
    {
        (0x037A, 0x037A),   // GREEK YPOGEGRAMMENI (NFKC: SPACE + IOTA)
        (0x0E33, 0x0E33),   // THAI CHARACTER SARA AM (NFKC: NIKHAHIT + SARA AA)
        (0x0EB3, 0x0EB3),   // LAO VOWEL SIGN AM (NFKC: NIGGAHITA + AA)
        (0x2E2F, 0x2E2F),   // VERTICAL TILDE (Lm by category, non-starter by NFKC)
        (0xFC5E, 0xFC63),   // ARABIC LIGATURE SHADDA WITH DAMMATAN/KASRATAN/etc. ISOLATED FORM (6)
        (0xFDFA, 0xFDFB),   // ARABIC LIGATURE SALLALLAHOU/JALLAJALALOUHOU (decompose to full phrases)
        (0xFE70, 0xFE70),   // ARABIC FATHATAN ISOLATED FORM
        (0xFE72, 0xFE72),   // ARABIC DAMMATAN ISOLATED FORM
        (0xFE74, 0xFE74),   // ARABIC KASRATAN ISOLATED FORM
        (0xFE76, 0xFE76),   // ARABIC FATHA ISOLATED FORM
        (0xFE78, 0xFE78),   // ARABIC DAMMA ISOLATED FORM
        (0xFE7A, 0xFE7A),   // ARABIC KASRA ISOLATED FORM
        (0xFE7C, 0xFE7C),   // ARABIC SHADDA ISOLATED FORM
        (0xFE7E, 0xFE7E),   // ARABIC SUKUN ISOLATED FORM
        (0xFF9E, 0xFF9F),   // HALFWIDTH KATAKANA VOICED/SEMI-VOICED SOUND MARK
    };

    // Source: Unicode 17.0 XID_Continue minus General_Category
    // L + Nl + Mn + Mc + Nd + Pc. Code points in XID_Continue that the
    // BCL doesn't report in any of those categories.
    private static readonly (int Low, int High)[] XidContinueAdds =
    {
        (0x00B7, 0x00B7),   // MIDDLE DOT (Po)
        (0x0387, 0x0387),   // GREEK ANO TELEIA (Po)
        (0x1369, 0x1371),   // ETHIOPIC DIGIT ONE..NINE (No, 9)
        (0x19DA, 0x19DA),   // NEW TAI LUE THAM DIGIT ONE (No)
        (0x200C, 0x200D),   // ZERO WIDTH NON-JOINER, ZERO WIDTH JOINER (Cf)
        (0x2118, 0x2118),   // SCRIPT CAPITAL P (Sm), also in XidStartAdds
        (0x212E, 0x212E),   // ESTIMATED SYMBOL (So), also in XidStartAdds
        (0x30FB, 0x30FB),   // KATAKANA MIDDLE DOT (Po)
        (0xFF65, 0xFF65),   // HALFWIDTH KATAKANA MIDDLE DOT (Po)
    };

    // Source: Unicode 17.0. Code points in L + Nl + Mn + Mc + Nd + Pc
    // that AREN'T in XID_Continue. Same shape as XidStartExclusions:
    // the BCL reports them in an identifier-ish category but UAX #31
    // excludes them from identifier continuations for NFKC reasons.
    // The overlap with XidStartExclusions is large; the difference is
    // mostly in spacing-combining-mark code points like SARA AM which
    // UAX #31 keeps in Continue but not in Start.
    private static readonly (int Low, int High)[] XidContinueExclusions =
    {
        (0x037A, 0x037A),   // GREEK YPOGEGRAMMENI
        (0x2E2F, 0x2E2F),   // VERTICAL TILDE
        (0xFC5E, 0xFC63),   // ARABIC LIGATURE SHADDA WITH ... ISOLATED FORM (6)
        (0xFDFA, 0xFDFB),   // ARABIC LIGATURE SALLALLAHOU/JALLAJALALOUHOU
        (0xFE70, 0xFE70),   // ARABIC FATHATAN ISOLATED FORM
        (0xFE72, 0xFE72),   // ARABIC DAMMATAN ISOLATED FORM
        (0xFE74, 0xFE74),   // ARABIC KASRATAN ISOLATED FORM
        (0xFE76, 0xFE76),   // ARABIC FATHA ISOLATED FORM
        (0xFE78, 0xFE78),   // ARABIC DAMMA ISOLATED FORM
        (0xFE7A, 0xFE7A),   // ARABIC KASRA ISOLATED FORM
        (0xFE7C, 0xFE7C),   // ARABIC SHADDA ISOLATED FORM
        (0xFE7E, 0xFE7E),   // ARABIC SUKUN ISOLATED FORM
    };

    private static readonly Lazy<TokenSet> _xidStart = new Lazy<TokenSet>(BuildXidStart);
    private static readonly Lazy<TokenSet> _xidContinue = new Lazy<TokenSet>(BuildXidContinue);

    // The set of Unicode scalar values that may begin an identifier per
    // UAX #31 R1 (XID_Start). Use together with XidContinue and
    // Rules.Identifier for spec-compliant identifier matching. Doesn't
    // include "_". For the programming-language profile that allows
    // leading underscore, use
    // Rules.Identifier(extraStartRunes: TokenSet.Runes("_")).
    public static TokenSet XidStart => _xidStart.Value;

    // The set of Unicode scalar values that may continue an identifier
    // per UAX #31 R1 (XID_Continue). Includes everything in XidStart
    // plus combining marks, decimal digits, and connector punctuation
    // (so "_" is already in XidContinue regardless of the start-side
    // profile). Intended for the tail of an identifier match.
    public static TokenSet XidContinue => _xidContinue.Value;

    private static TokenSet BuildXidStart()
    {
        var lettersAndLetterNumber = CategoriesUnion(
            UnicodeCategory.UppercaseLetter,
            UnicodeCategory.LowercaseLetter,
            UnicodeCategory.TitlecaseLetter,
            UnicodeCategory.ModifierLetter,
            UnicodeCategory.OtherLetter,
            UnicodeCategory.LetterNumber);
        return (lettersAndLetterNumber | FromRanges(XidStartAdds))
            & ~FromRanges(XidStartExclusions);
    }

    private static TokenSet BuildXidContinue()
    {
        var continueBase = CategoriesUnion(
            UnicodeCategory.UppercaseLetter,
            UnicodeCategory.LowercaseLetter,
            UnicodeCategory.TitlecaseLetter,
            UnicodeCategory.ModifierLetter,
            UnicodeCategory.OtherLetter,
            UnicodeCategory.LetterNumber,
            UnicodeCategory.NonSpacingMark,
            UnicodeCategory.SpacingCombiningMark,
            UnicodeCategory.DecimalDigitNumber,
            UnicodeCategory.ConnectorPunctuation);
        return (continueBase | FromRanges(XidContinueAdds))
            & ~FromRanges(XidContinueExclusions);
    }
}
