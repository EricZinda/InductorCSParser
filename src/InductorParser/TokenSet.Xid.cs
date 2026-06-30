using System;
using System.Globalization;

namespace InductorParser;

// XID identifier support for TokenSet (UAX #31 R1's XID_Start and XID_Continue).
//
// Sources:
//   UAX #31 §2:             https://www.unicode.org/reports/tr31/#Default_Identifier_Syntax
//   UnicodeData:            https://www.unicode.org/Public/17.0.0/ucd/UnicodeData.txt
//   PropList:               https://www.unicode.org/Public/17.0.0/ucd/PropList.txt
//   DerivedCoreProperties:  https://www.unicode.org/Public/17.0.0/ucd/DerivedCoreProperties.txt
//   DerivedGeneralCategory: https://www.unicode.org/Public/17.0.0/ucd/extracted/DerivedGeneralCategory.txt
// 
// Here are the definitions from the DerivedCoreProperties.txt header (described right after).
//
//   ID_Start     = Lu + Ll + Lt + Lm + Lo + Nl
//                + Other_ID_Start
//                - Pattern_Syntax - Pattern_White_Space
//   ID_Continue  = ID_Start + Mn + Mc + Nd + Pc
//                + Other_ID_Continue
//                - Pattern_Syntax - Pattern_White_Space
//   XID_Start    = ID_Start, closed under NFKx
//   XID_Continue = ID_Continue, closed under NFKx
//
// The cryptic names in the formulas are Unicode's shorthand for lists
// of characters. Each one comes from one of the data files linked above:
//  - Lu, Ll, Lt, Lm, Lo, Nl, Mn, Mc, Nd, Pc are "General_Category"
//    labels: Unicode's classification of every character (Lu =
//    uppercase letter, Ll = lowercase letter, Nd = decimal digit, Pc =
//    underscore-like connector punctuation, and so on). The full
//    character-to-category assignment is in UnicodeData.txt.
//  - Other_ID_Start, Other_ID_Continue, Pattern_Syntax,
//    Pattern_White_Space are "properties": additional named character
//    lists Unicode maintains for specific purposes (Pattern_Syntax,
//    for example, is the set of characters used as syntax: brackets,
//    operators, etc.). They live in PropList.txt.
//  - ID_Start, ID_Continue, XID_Start, XID_Continue are "derived
//    properties": lists computed from the others using the formulas
//    above. DerivedCoreProperties.txt publishes the computed lists,
//    and matching them is what this file is for.
//
// What "closed under NFKx" means: if you take an identifier and apply
// NFKC or NFKD to it, the result is still a valid identifier. To make
// that property hold, XID_Start drops any character whose decomposed
// form wouldn't itself be a valid identifier start. U+309B (a Japanese
// voicing mark) is the canonical case: it's a Modifier Symbol (Sk),
// not a letter, and reaches ID_Start through Other_ID_Start (the
// OtherIdStart table below lists it with that category). But it
// decomposes to "space + combining mark", and an identifier can't
// start with a space, so XID_Start excludes 309B. The same filter on
// the continue side gives XID_Continue.
//
// How we get the list of dropped characters without running NFKx
// ourselves: Unicode publishes both ID_Start and XID_Start as separate
// lists in DerivedCoreProperties.txt, so the closure-removed
// characters are just (ID_Start minus XID_Start). The
// NfkxClosureRemovedFromXidStart and NfkxClosureRemovedFromXidContinue
// constants below are that subtraction, and their matches_UCD tests
// fetch both lists from DerivedCoreProperties.txt and verify it.
//
// Sources: UAX #15 (https://www.unicode.org/reports/tr15/) defines the
// K-form normalizations (NFKC and NFKD; "NFKx" is the spec's shorthand
// for either one). UAX #31 (cited below) defines XID as their closure.
//
// Where each piece comes from:
//   * The General_Category sets (Lu..Nl, Mn, Mc, Nd, Pc) come from the .NET
//     BCL via CategoriesUnion (so they track whatever Unicode version the
//     BCL ships).
//   * Other_ID_Start and Other_ID_Continue: hand-typed below, verbatim
//     from PropList.txt at Unicode 17.0.
//   * NFKx closure removals: hand-typed below as
//     NfkxClosureRemovedFromXidStart and NfkxClosureRemovedFromXidContinue,
//     computed as (ID_X minus XID_X) per DerivedCoreProperties.txt.
//   * "- Pattern_Syntax - Pattern_White_Space" from the formula. The
//     identifier-base overlaps with these two properties are captured
//     as IdCategoriesInPatternSyntax and IdCategoriesInPatternWhiteSpace
//     (the latter is empty at Unicode 17.0). Each constant has its own
//     matches_UCD test, and the build applies both subtractions.
//
public readonly partial struct TokenSet
{
    // ============================================================
    // Spec-named source constants
    // ============================================================

    // PropList.txt's Other_ID_Start property at Unicode 17.0.
    //
    // Recipe to regenerate: copy every "; Other_ID_Start" range from
    // PropList.txt verbatim.
    //
    // Verified by XidIdentifierTests.OtherIdStart_matches_UCD_test.
    private static readonly (int Low, int High)[] OtherIdStart =
    {
        (0x1885, 0x1886),   // MONGOLIAN LETTER ALI GALI BALUDA..THREE BALUDA (Mn)
        (0x2118, 0x2118),   // SCRIPT CAPITAL P (Sm)
        (0x212E, 0x212E),   // ESTIMATED SYMBOL (So)
        (0x309B, 0x309C),   // KATAKANA-HIRAGANA VOICED / SEMI-VOICED SOUND MARK (Sk)
    };

    // PropList.txt's Other_ID_Continue property at Unicode 17.0.
    //
    // Recipe to regenerate: copy every "; Other_ID_Continue" range from
    // PropList.txt verbatim.
    //
    // Verified by XidIdentifierTests.OtherIdContinue_matches_UCD_test.
    private static readonly (int Low, int High)[] OtherIdContinue =
    {
        (0x00B7, 0x00B7),   // MIDDLE DOT (Po)
        (0x0387, 0x0387),   // GREEK ANO TELEIA (Po)
        (0x1369, 0x1371),   // ETHIOPIC DIGIT ONE..NINE (No)
        (0x19DA, 0x19DA),   // NEW TAI LUE THAM DIGIT ONE (No)
        (0x200C, 0x200D),   // ZERO WIDTH NON-JOINER, ZERO WIDTH JOINER (Cf), added to Other_ID_Continue in Unicode 16.0
        (0x30FB, 0x30FB),   // KATAKANA MIDDLE DOT (Po), added in Unicode 16.0
        (0xFF65, 0xFF65),   // HALFWIDTH KATAKANA MIDDLE DOT (Po), added in Unicode 16.0
    };

    // Code points that NFKx closure drops on the way from ID_Start to
    // XID_Start, at Unicode 17.0. See the closure-rule explanation at the
    // top of this file.
    //
    // Recipe to regenerate: take every "; ID_Start" range from
    // DerivedCoreProperties.txt and every "; XID_Start" range, and
    // subtract the latter from the former. Each entry that survives is
    // one tuple here.
    //
    // Verified by XidIdentifierTests.NfkxClosureRemovedFromXidStart_matches_UCD_test.
    private static readonly (int Low, int High)[] NfkxClosureRemovedFromXidStart =
    {
        (0x037A, 0x037A),   // GREEK YPOGEGRAMMENI (NFKx: SPACE + IOTA)
        (0x0E33, 0x0E33),   // THAI CHARACTER SARA AM (NFKx: NIKHAHIT + SARA AA)
        (0x0EB3, 0x0EB3),   // LAO VOWEL SIGN AM (NFKx: NIGGAHITA + AA)
        (0x309B, 0x309C),   // KATAKANA-HIRAGANA (SEMI-)VOICED SOUND MARK (NFKx: SPACE + combining mark)
        (0xFC5E, 0xFC63),   // ARABIC LIGATURE SHADDA WITH HARAKAT ISOLATED FORM (6)
        (0xFDFA, 0xFDFB),   // ARABIC LIGATURE SALLALLAHOU / JALLAJALALOUHOU
        (0xFE70, 0xFE70),   // ARABIC FATHATAN ISOLATED FORM
        (0xFE72, 0xFE72),   // ARABIC DAMMATAN ISOLATED FORM
        (0xFE74, 0xFE74),   // ARABIC KASRATAN ISOLATED FORM
        (0xFE76, 0xFE76),   // ARABIC FATHA ISOLATED FORM
        (0xFE78, 0xFE78),   // ARABIC DAMMA ISOLATED FORM
        (0xFE7A, 0xFE7A),   // ARABIC KASRA ISOLATED FORM
        (0xFE7C, 0xFE7C),   // ARABIC SHADDA ISOLATED FORM
        (0xFE7E, 0xFE7E),   // ARABIC SUKUN ISOLATED FORM
        (0xFF9E, 0xFF9F),   // HALFWIDTH KATAKANA VOICED / SEMI-VOICED SOUND MARK
    };

    // Same idea on the continue side.  
    //
    // Recipe to regenerate: take every "; ID_Continue" range from
    // DerivedCoreProperties.txt and every "; XID_Continue" range, and
    // subtract the latter from the former.
    //
    // Verified by XidIdentifierTests.NfkxClosureRemovedFromXidContinue_matches_UCD_test.
    private static readonly (int Low, int High)[] NfkxClosureRemovedFromXidContinue =
    {
        (0x037A, 0x037A),   // GREEK YPOGEGRAMMENI
        (0x309B, 0x309C),   // KATAKANA-HIRAGANA (SEMI-)VOICED SOUND MARK
        (0xFC5E, 0xFC63),   // ARABIC LIGATURE SHADDA WITH HARAKAT ISOLATED FORM
        (0xFDFA, 0xFDFB),   // ARABIC LIGATURE SALLALLAHOU / JALLAJALALOUHOU
        (0xFE70, 0xFE70),   // ARABIC FATHATAN ISOLATED FORM
        (0xFE72, 0xFE72),   // ARABIC DAMMATAN ISOLATED FORM
        (0xFE74, 0xFE74),   // ARABIC KASRATAN ISOLATED FORM
        (0xFE76, 0xFE76),   // ARABIC FATHA ISOLATED FORM
        (0xFE78, 0xFE78),   // ARABIC DAMMA ISOLATED FORM
        (0xFE7A, 0xFE7A),   // ARABIC KASRA ISOLATED FORM
        (0xFE7C, 0xFE7C),   // ARABIC SHADDA ISOLATED FORM
        (0xFE7E, 0xFE7E),   // ARABIC SUKUN ISOLATED FORM
    };

    // Identifier-base category members (L+Nl+Mn+Mc+Nd+Pc) that
    // "- Pattern_Syntax" filters out from XID_Start and
    // XID_Continue.
    //
    // Recipe to regenerate: intersect
    // (Lu+Ll+Lt+Lm+Lo+Nl+Mn+Mc+Nd+Pc per DerivedGeneralCategory.txt)
    // with (Pattern_Syntax per PropList.txt).
    //
    // Verified by XidIdentifierTests.IdCategoriesInPatternSyntax_matches_UCD_test.
    private static readonly (int Low, int High)[] IdCategoriesInPatternSyntax =
    {
        (0x2E2F, 0x2E2F),   // VERTICAL TILDE (Lm by category, Pattern_Syntax-excluded)
    };

    // Identifier-base category members (L+Nl+Mn+Mc+Nd+Pc) that
    // "- Pattern_White_Space" filters out from XID_Start and
    // XID_Continue. At Unicode 17.0 the intersection is empty. The empty
    // table is kept so the formula's "- Pattern_White_Space" step is
    // realized in code, and so a future Unicode release that grew the
    // intersection would surface as a test failure.
    //
    // Recipe to regenerate: intersect
    // (Lu+Ll+Lt+Lm+Lo+Nl+Mn+Mc+Nd+Pc per DerivedGeneralCategory.txt)
    // with (Pattern_White_Space per PropList.txt).
    //
    // Verified by XidIdentifierTests.IdCategoriesInPatternWhiteSpace_matches_UCD_test.
    private static readonly (int Low, int High)[] IdCategoriesInPatternWhiteSpace =
    {
    };

    // ============================================================
    // Public lazy accessors
    // ============================================================

    private static readonly Lazy<TokenSet> _xidStart = new Lazy<TokenSet>(BuildXidStart);
    private static readonly Lazy<TokenSet> _xidContinue = new Lazy<TokenSet>(BuildXidContinue);

    /// <summary>
    /// The set of Unicode scalar values that may begin an identifier per
    /// UAX #31 R1 (XID_Start). Use together with <see cref="XidContinue"/> and
    /// <see cref="Rules.Identifier"/> for spec-compliant identifier matching.
    /// </summary>
    /// <remarks>
    /// Doesn't include "_". For the programming-language profile that allows a
    /// leading underscore, use
    /// Rules.Identifier(extraStartRunes: TokenSet.Runes("_")).
    /// </remarks>
    public static TokenSet XidStart => _xidStart.Value;

    /// <summary>
    /// The set of Unicode scalar values that may continue an identifier per
    /// UAX #31 R1 (XID_Continue). Intended for the tail of an identifier match.
    /// </summary>
    /// <remarks>
    /// Includes everything in <see cref="XidStart"/> plus combining marks,
    /// decimal digits, and connector punctuation (so "_" is already in
    /// XidContinue regardless of the start-side profile).
    /// </remarks>
    public static TokenSet XidContinue => _xidContinue.Value;

    // ============================================================
    // Build methods: combine the spec constants with the BCL's
    // CategoriesUnion to produce the final XID sets.
    // ============================================================

    // Each Build method below is a direct translation of one line from
    // the formula at the top of this file. The "|" operator is "+", and
    // "-" is set difference.

    // ID_Start = Lu + Ll + Lt + Lm + Lo + Nl + Other_ID_Start
    //          - Pattern_Syntax - Pattern_White_Space
    private static TokenSet BuildIdStart()
    {
        var lettersAndLetterNumber = CategoriesUnion(
            UnicodeCategory.UppercaseLetter,
            UnicodeCategory.LowercaseLetter,
            UnicodeCategory.TitlecaseLetter,
            UnicodeCategory.ModifierLetter,
            UnicodeCategory.OtherLetter,
            UnicodeCategory.LetterNumber);
        return (lettersAndLetterNumber | FromRanges(OtherIdStart))
            - FromRanges(IdCategoriesInPatternSyntax)
            - FromRanges(IdCategoriesInPatternWhiteSpace);
    }

    // ID_Continue = ID_Start + Mn + Mc + Nd + Pc + Other_ID_Continue
    //             - Pattern_Syntax - Pattern_White_Space
    private static TokenSet BuildIdContinue()
    {
        var continueExtraCategories = CategoriesUnion(
            UnicodeCategory.NonSpacingMark,
            UnicodeCategory.SpacingCombiningMark,
            UnicodeCategory.DecimalDigitNumber,
            UnicodeCategory.ConnectorPunctuation);
        return (BuildIdStart() | continueExtraCategories | FromRanges(OtherIdContinue))
            - FromRanges(IdCategoriesInPatternSyntax)
            - FromRanges(IdCategoriesInPatternWhiteSpace);
    }

    // XID_Start = ID_Start, closed under NFKx
    private static TokenSet BuildXidStart() =>
        BuildIdStart() - FromRanges(NfkxClosureRemovedFromXidStart);

    // XID_Continue = ID_Continue, closed under NFKx
    private static TokenSet BuildXidContinue() =>
        BuildIdContinue() - FromRanges(NfkxClosureRemovedFromXidContinue);
}
