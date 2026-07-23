using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using static InductorParser.Tests.CanaryHelper;

namespace InductorParser.Tests;

// Named Unicode constants used across test fixtures. Raw hex escapes and
// surrogate-pair string literals don't tell you what they mean on sight.
// Names do. Naming convention:
//
//   XxxRune      int codepoint (U+xxxxx).
//   XxxGrapheme  UTF-16 string that tokenizes as exactly one grapheme.
//   XxxText      UTF-16 string that isn't necessarily a standalone
//                  grapheme (e.g. a combining modifier).
//
// Every string constant goes through Canary(literal, description, codepoints)
// so an editor or tool silently rewriting a literal produces a runtime
// failure that names what was supposed to be there. See Canary.cs.
//
// One rule keeps the two field types from being a coin toss: every
// example has a string constant (XxxGrapheme or XxxText). That's the
// canonical form and the whole corpus, the thing AllStringConstants()
// returns. An XxxRune int constant is an optional sibling, added only
// when a caller needs the codepoint as an int (Token(int),
// TokenSet.Single(int), a [TestCase] / [TestCase((char)...)] argument,
// none of which can take a runtime string). A Rune int is never the
// only form of an example, so adding one is never how a new example
// enters the corpus: add the string constant, then add the int sibling
// too if a caller needs it.
// When both forms exist, the string's Canary call takes the XxxRune
// constant as its code-point argument, so the code point is written in
// one place and the int and string forms can't drift apart.
internal static class UnicodeExamples
{
    // Every string constant declared below, paired with its field name,
    // gathered by reflection. Lets a fixture run the whole corpus through
    // one uniform check without re-listing the constants, and means a
    // constant added below is picked up with no further edit.
    // Ordered by field name so test discovery is reproducible run to run.
    public static IEnumerable<(string Name, string Value)> AllStringConstants() =>
        typeof(UnicodeExamples)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(string))
            .OrderBy(field => field.Name, StringComparer.Ordinal)
            .Select(field => (field.Name, (string)field.GetValue(null)!));

    // Waving hand. Supplementary-plane rune (needs a surrogate pair in
    // UTF-16). One rune, one grapheme on its own.
    public const int WavingHandRune = 0x1F44B;
    public static readonly string WavingHandGrapheme = Canary(
        "👋", "waving hand emoji", WavingHandRune);

    // Medium skin tone modifier. Combining modifier rune that attaches to a
    // base character to form a multi-rune grapheme. Not typically rendered
    // standalone.
    public const int MediumSkinToneRune = 0x1F3FD;
    public static readonly string MediumSkinToneText = Canary(
        "🏽", "medium skin tone modifier", MediumSkinToneRune);

    // Waving hand + medium skin tone. One grapheme made of two runes
    // (4 UTF-16 chars total). The lexer reads the whole sequence as a
    // single token.
    public static readonly string SkinTonedWaveGrapheme = Canary("👋🏽", "medium-skin-tone waving hand",
        WavingHandRune, MediumSkinToneRune);

    // Guitar. One rune, one grapheme.
    public const int GuitarRune = 0x1F3B8;
    public static readonly string GuitarGrapheme = Canary(
        "🎸", "guitar emoji", GuitarRune);

    // Musical keyboard. One rune, one grapheme. Used alongside Guitar
    // as a "different emoji, same shape" mismatch example.
    public const int MusicalKeyboardRune = 0x1F3B9;
    public static readonly string MusicalKeyboardGrapheme = Canary(
        "🎹", "musical keyboard emoji", MusicalKeyboardRune);

    // U+0301 combining acute. Combining mark that attaches to a base character
    // to form a multi-rune grapheme. Not a standalone grapheme.
    public static readonly string CombiningAcuteText = Canary("́", "combining acute (attaches to preceding base)", 0x0301);

    // é as e + combining acute. The textbook multi-rune Token: two runes,
    // one grapheme (2 UTF-16 chars total). Works on every runtime including
    // legacy StringInfo because the base+combining-mark rule predates UAX #29.
    // Use this when a test needs a multi-rune grapheme that segments the same
    // way on .NET 5+ and on Unity Mono / IL2CPP.
    public static readonly string LatinEAcuteGrapheme = Canary("é", "e + combining acute, decomposed",
        0x0065, 0x0301);

    // é as the single precomposed rune U+00E9 (LATIN SMALL LETTER E WITH
    // ACUTE). One rune, one grapheme. Pair with LatinEAcuteGrapheme when
    // a test wants to be explicit about which form it's using.
    public static readonly string LatinEAcutePrecomposedGrapheme = Canary("é", "latin small letter e with acute (precomposed)", LatinEAcuteRune);

    // Woman shrugging. ZWJ emoji sequence: base shrug rune + ZWJ +
    // female sign + emoji variation selector. UAX #29 sees one grapheme.
    // Legacy StringInfo splits it. Use this to test the ZWJ rule.
    public static readonly string WomanShruggingGrapheme = Canary(
        "🤷‍♀️",
        "woman-shrugging emoji (ZWJ sequence)",
        0x1F937, 0x200D, 0x2640, 0xFE0F);

    // US flag. Two regional indicator code points (U + S). UAX #29 sees
    // one grapheme. Legacy StringInfo splits it. Use this to test the
    // regional-indicator pairing rule.
    public static readonly string USFlagGrapheme = Canary("🇺🇸", "us flag emoji (regional indicator pair)", RegionalIndicatorURune, 0x1F1F8);

    // Thai "kam". SARA AM is the canonical extended-grapheme-cluster
    // case (a vowel sign that visually composes with the preceding consonant).
    // UAX #29 sees one grapheme. Legacy StringInfo splits it. Use this to
    // test the SARA AM rule.
    public static readonly string ThaiKamGrapheme = Canary("กำ", "Thai ko kai + sara am (kam)", 0x0E01, 0x0E33);

    // Man. One rune, one grapheme. Used as a base for ZWJ sequences
    // in malformed-Unicode and joiner tests.
    public const int ManEmojiRune = 0x1F468;
    public static readonly string ManEmojiGrapheme = Canary(
        "👨", "man emoji", ManEmojiRune);

    // Woman. One rune, one grapheme. Paired with ManEmoji and
    // BoyEmoji to build the family ZWJ sequence below, and with
    // Briefcase to build the woman-office-worker profession ZWJ.
    public const int WomanEmojiRune = 0x1F469;
    public static readonly string WomanEmojiGrapheme = Canary(
        "👩", "woman emoji", WomanEmojiRune);

    // Boy. One rune, one grapheme. Used as the third element of the
    // family ZWJ sequence below.
    public const int BoyEmojiRune = 0x1F466;
    public static readonly string BoyEmojiGrapheme = Canary(
        "👦", "boy emoji", BoyEmojiRune);

    // Family ZWJ sequence: man + ZWJ + woman + ZWJ + boy. UAX #29 GB11
    // keeps an emoji + ZWJ + emoji chain as a single cluster. Five
    // runes (man, ZWJ, woman, ZWJ, boy), 8 UTF-16 chars. Distinct from
    // the woman-shrugging sequence because the joins are between three
    // base emoji rather than base + sign + variation selector. Locks
    // in that the multi-link ZWJ rule fires at every joiner along the
    // chain, not just the first one.
    public static readonly string FamilyManWomanBoyGrapheme = Canary("👨‍👩‍👦", "family man + ZWJ + woman + ZWJ + boy emoji",
        ManEmojiRune, 0x200D, WomanEmojiRune, 0x200D, BoyEmojiRune);

    // Briefcase. One rune, one grapheme. The profession-tag emoji
    // for "office worker." Paired with WomanEmoji via ZWJ to form
    // woman-office-worker.
    public const int BriefcaseRune = 0x1F4BC;
    public static readonly string BriefcaseGrapheme = Canary(
        "💼", "briefcase emoji", BriefcaseRune);

    // Profession ZWJ sequence: base human emoji + ZWJ + profession-tag
    // emoji. Three runes (woman, ZWJ, briefcase), 5 UTF-16 chars.
    // Different shape from the family sequence (one ZWJ link, not
    // two) and from woman-shrugging (no variation selector). Lock-in
    // case for the base-emoji-joins-profession-tag pattern that
    // Unicode 13+ uses for the gendered profession emoji set.
    public static readonly string WomanOfficeWorkerGrapheme = Canary("👩‍💼", "woman office worker emoji (woman + ZWJ + briefcase)",
        WomanEmojiRune, 0x200D, BriefcaseRune);

    // U+20E3 COMBINING ENCLOSING KEYCAP. Combining mark that wraps a
    // square box around the preceding base. UAX #29 GCB=Extend.
    public static readonly string CombiningEnclosingKeycapText = Canary("⃣", "combining enclosing keycap", 0x20E3);

    // Keycap digit one. Three runes (DIGIT ONE + emoji variation
    // selector + COMBINING ENCLOSING KEYCAP), one grapheme. The
    // canonical keycap sequence: a base character followed by VS16 to
    // select emoji presentation and the combining enclosing keycap to
    // wrap it. UAX #29 GB9 keeps the Extend characters glued to
    // the base. Distinct from the family ZWJ sequence (which uses
    // emoji + ZWJ + emoji links) so the test covers the
    // base + variation-selector + combining-mark shape.
    public static readonly string DigitOneKeycapGrapheme = Canary("1️⃣", "keycap digit one (digit one + VS16 + combining enclosing keycap)", 0x0031, 0xFE0F, 0x20E3);

    // U+00A0 NO-BREAK SPACE. Renders as a space, but classified by
    // .NET as Unicode category Zs (Space_Separator), same as ordinary
    // space. UAX #29 treats it as GCB=Other so it breaks on both
    // sides. It surfaces as its own one-character token between
    // adjacent letters. Distinct from U+0020 SPACE because it isn't a
    // word-wrap opportunity. Common in copy-pasted text from word
    // processors and Wikipedia.
    public static readonly string NoBreakSpaceText = Canary(" ", "no-break space (renders as space, doesn't word-wrap)", 0x00A0);

    // Regional indicator letter U. One half of a flag emoji
    // pair (two regional indicators in a row form a country flag). UAX #29
    // pairs two of these into one cluster. A single one stands alone.
    public const int RegionalIndicatorURune = 0x1F1FA;
    public static readonly string RegionalIndicatorUText = Canary("🇺", "regional indicator letter u (one half of a flag pair)", RegionalIndicatorURune);

    // U+10FFFF maximum Unicode scalar value. Encoded as the surrogate pair
    // (U+DBFF, U+DFFF) in UTF-16. Boundary case for rune decoding.
    public const int MaximumCodePointRune = 0x10FFFF;
    public static readonly string MaximumCodePointGrapheme = Canary("􏿿", "maximum Unicode scalar value", MaximumCodePointRune);

    // === Lone surrogate halves (not valid scalars on their own) ===
    // UTF-16 encodes supplementary-plane scalars as a HIGH-then-LOW
    // surrogate pair. A surrogate code unit appearing without its
    // partner isn't a valid Unicode scalar value, but .NET strings
    // can still hold one. Useful for the malformed-input tests.
    //
    // These are built from char values at runtime, never written as
    // string literals: IL2CPP's string-literal conversion replaces an
    // unpaired surrogate in a compiled literal with U+FFFD, so a
    // literal form of these constants reaches the Unity test player
    // corrupted and Canary fires on every corpus-dependent test. A
    // runtime-built string has no literal to corrupt. Well-formed
    // surrogate pairs (the emoji constants above) survive as literals.

    // U+D800: lowest high-surrogate code unit. "Min" of the high range.
    public static readonly string HighSurrogateMinText = Canary(((char)HighSurrogateMinRune).ToString(), "lone high surrogate (min, invalid scalar)", HighSurrogateMinRune);

    // U+DBFF: highest high-surrogate code unit. "Max" of the high range.
    public static readonly string HighSurrogateMaxText = Canary(((char)HighSurrogateMaxRune).ToString(), "lone high surrogate (max, invalid scalar)", HighSurrogateMaxRune);

    // U+DC00: lowest low-surrogate code unit. "Min" of the low range.
    public static readonly string LowSurrogateMinText = Canary(((char)LowSurrogateMinRune).ToString(), "lone low surrogate (min, invalid scalar)", LowSurrogateMinRune);

    // U+DFFF: highest low-surrogate code unit. "Max" of the low range.
    public static readonly string LowSurrogateMaxText = Canary(((char)LowSurrogateMaxRune).ToString(), "lone low surrogate (max, invalid scalar)", LowSurrogateMaxRune);

    // U+D83D: high-surrogate code unit that starts most common emoji
    // surrogate pairs (waving hand, woman, man, etc.). Used standalone for
    // malformed-emoji tests where the low-half partner is missing.
    public static readonly string EmojiStartHighSurrogateText = Canary(((char)EmojiStartHighSurrogateRune).ToString(), "lone high surrogate U+D83D (emoji start half, invalid scalar)", EmojiStartHighSurrogateRune);

    // U+DC00 then U+D800: a low surrogate followed by a high surrogate.
    // Pair-shaped but in the wrong order (UTF-16 pairs are high-then-low),
    // so the two never combine into a supplementary scalar and stay two
    // separate ill-formed code units. WTF-8 (Simon Sapin) discusses this
    // exact pattern.
    public static readonly string ReversedSurrogatePairText = Canary(new string(new[] { (char)LowSurrogateMinRune, (char)HighSurrogateMinRune }), "reversed surrogate pair (low then high, invalid)", LowSurrogateMinRune, HighSurrogateMinRune);

    // === Format / control characters that don't render as a glyph ===
    // The names exist so test source stays readable. Inline literals like
    // "‍" or "؀" appear as empty quotes in most editors. The named
    // constants make the intent obvious.

    // U+200D ZERO WIDTH JOINER. Glues emoji together into composed sequences
    // (the family / professions / variant emojis). UAX #29 GCB=ZWJ. Semantically
    // attaches to surrounding bases.
    public static readonly string ZeroWidthJoinerText = Canary("‍", "zero-width joiner", 0x200D);

    // U+200C ZERO WIDTH NON-JOINER. Suppresses default joining behavior in
    // scripts like Arabic and Indic. UAX #29 GCB=Extend.
    public static readonly string ZeroWidthNonJoinerText = Canary("‌", "zero-width non-joiner", 0x200C);

    // U+200B ZERO WIDTH SPACE. Invisible word-break hint. UAX #29 GCB=Other
    // (breaks on both sides).
    public static readonly string ZeroWidthSpaceText = Canary("​", "zero-width space", 0x200B);

    // U+FE0F EMOJI VARIATION SELECTOR. Selects emoji-style presentation for
    // the preceding base. UAX #29 GCB=Extend.
    public static readonly string EmojiVariationSelectorText = Canary("️", "emoji variation selector (VS16)", 0xFE0F);

    // U+FEFF BYTE ORDER MARK / ZERO WIDTH NO-BREAK SPACE. Marks UTF byte
    // order at the start of streams. Left over from older encodings.
    public static readonly string ByteOrderMarkText = Canary("﻿", "byte order mark / zwnbsp", 0xFEFF);

    // U+00AD SOFT HYPHEN. Hyphenation hint. UAX #29 breaks on both sides so
    // it surfaces as its own token between surrounding letters.
    public static readonly string SoftHyphenText = Canary("­", "soft hyphen (hyphenation hint, invisible by default)", 0x00AD);

    // U+202E RIGHT-TO-LEFT OVERRIDE. Bidi-direction control. The same
    // character used in the Trojan Source attack to make source code render
    // differently from its logical order.
    public static readonly string RightToLeftOverrideText = Canary("‮", "right-to-left bidi override", 0x202E);

    // U+0600 ARABIC NUMBER SIGN. UAX #29 Prepend category, normally attaches
    // to the FOLLOWING character.
    public static readonly string ArabicNumberSignText = Canary("؀", "Arabic number sign (UAX #29 Prepend)", 0x0600);

    // U+094D DEVANAGARI SIGN VIRAMA. Indic linker that joins consonants into
    // conjuncts. UAX #29 GCB=Extend.
    public static readonly string DevanagariViramaText = Canary("्", "Devanagari virama (conjunct linker)", 0x094D);

    // U+0316 COMBINING GRAVE ACCENT BELOW. Combining mark with no
    // precomposed form for any letter, so NFC never composes it away.
    // Used to build long non-starter sequences for Stream-Safe tests.
    public const int CombiningGraveBelowRune = 0x0316;
    public static readonly string CombiningGraveBelowText = Canary("̖", "combining grave accent below", CombiningGraveBelowRune);

    // U+0915 DEVANAGARI LETTER KA. Base consonant for conjunct-formation
    // and nukta tests. Lo category.
    public static readonly string DevanagariKaGrapheme = Canary("क", "Devanagari letter ka", 0x0915);

    // U+0937 DEVANAGARI LETTER SSA. Second consonant for the ka-virama-ssa
    // conjunct sequence used in Indic shaping tests.
    public static readonly string DevanagariSsaGrapheme = Canary("ष", "Devanagari letter ssa", 0x0937);

    // U+0939 + U+093F: Devanagari "hi" (HA + I VOWEL SIGN). The lexer
    // groups these into a single multi-rune grapheme cluster.
    public static readonly string DevanagariHiGrapheme = Canary("हि", "Devanagari ha + i vowel sign", 0x0939, 0x093F);

    // U+E0001 LANGUAGE TAG. Used inside emoji tag sequences for subdivision
    // flags. UAX #29 GCB=Extend.
    public const int LanguageTagRune = 0xE0001;
    public static readonly string LanguageTagText = Canary("󠀁", "language tag (emoji subdivision flag base)", LanguageTagRune);

    // U+180E MONGOLIAN VOWEL SEPARATOR. Property has shifted across Unicode
    // versions (Cf, then Whitespace, now Cf again depending on the runtime's
    // data).
    public static readonly string MongolianVowelSeparatorText = Canary("᠎", "Mongolian vowel separator", 0x180E);

    // === Noncharacters, Private Use, Replacement ===

    // U+FFFE / U+FFFF: BMP noncharacters. .NET's string.Normalize rejects
    // U+FFFE specifically as "invalid Unicode code points" but accepts
    // U+FFFF (.NET-specific asymmetry).
    public static readonly string NoncharacterFFFEText = Canary("￾", "BMP noncharacter (rejected by .net Normalize)", 0xFFFE);
    public static readonly string NoncharacterFFFFText = Canary("￿", "BMP noncharacter (accepted by .net Normalize)", 0xFFFF);

    // U+FDD0 noncharacter. First code point in the U+FDD0..U+FDEF
    // noncharacter block. .NET's Normalize accepts these.
    public static readonly string NoncharacterFDD0Text = Canary("﷐", "noncharacter (first in U+FDD0..U+FDEF block)", 0xFDD0);

    // U+E000 start of the BMP Private Use Area. Valid scalar with no
    // assigned character. No Unicode category data.
    public static readonly string PrivateUseAreaStartText = Canary("", "start of BMP Private Use Area", 0xE000);

    // U+FFFD REPLACEMENT CHARACTER. What permissive decoders write when they
    // hit invalid bytes. By the time it reaches the parser it's a perfectly
    // valid scalar.
    public static readonly string ReplacementCharacterText = Canary("�", "replacement character", 0xFFFD);

    // U+0000 NULL. Cc control. Valid scalar. Most editors render as a box
    // or as zero-width, depending on font.
    public static readonly string NullText = Canary(" ", "null", 0x0000);

    // === Normalization edge-case characters ===
    // Many of these look identical to a plain ASCII or Greek character
    // in source. Constants are critical here: U+212A KELVIN renders the
    // same as ASCII 'K', and a reader inspecting source has no way to
    // tell which one was typed. Same story for Latin / Cyrillic 'a' in
    // the homoglyph test.

    // U+1EAD LATIN SMALL LETTER A WITH CIRCUMFLEX AND DOT BELOW,
    // precomposed. Canonical decomposition: a + dot-below + circumflex.
    // The two marks have DIFFERENT combining classes (dot-below is
    // ccc=220, circumflex is ccc=230), so NFC reorders them by class
    // when they appear in non-canonical order, then composes to
    // U+1EAD. This is the test case for "NFC reorders marks of
    // different classes". Same-class marks (e.g. acute+circumflex,
    // both ccc=230) aren't reordered, so input order matters there.
    public const int VietnameseACircumflexDotBelowRune = 0x1EAD;
    public static readonly string VietnameseACircumflexDotBelowGrapheme = Canary("ậ", "Latin a with circumflex and dot-below (precomposed)", VietnameseACircumflexDotBelowRune);

    // a + dot-below + circumflex, already in canonical order
    // (ccc 220 then 230). NFC composes directly to U+1EAD.
    public static readonly string VietnameseACircumflexDotBelowCanonicalText = Canary(
        "ậ",
        "a + dot-below + circumflex (canonical mark order)",
        0x0061, 0x0323, 0x0302);

    // a + circumflex + dot-below, non-canonical order
    // (ccc 230 then 220). NFC reorders to canonical (220 then 230),
    // then composes to U+1EAD. Both strings normalize to the same
    // precomposed character.
    public static readonly string VietnameseACircumflexDotBelowReorderedText = Canary(
        "ậ",
        "a + circumflex + dot-below (non-canonical mark order)",
        0x0061, 0x0302, 0x0323);

    // U+D55C HANGUL SYLLABLE HAN, precomposed. The poster child for
    // canonical-equivalence in CJK.
    public static readonly string HangulHanGrapheme = Canary("한", "Hangul syllable han (precomposed)", 0xD55C);

    // Decomposed Hangul "han": U+1112 (HIEUH) + U+1161 (A) + U+11AB (NIEUN).
    // NFC composes the three jamo back to U+D55C.
    public static readonly string HangulHanDecomposedText = Canary("한", "Hangul han decomposed (hieuh + a + nieun jamo)", 0x1112, 0x1161, 0x11AB);

    // U+212B ANGSTROM SIGN. Canonical singleton. NFC converts to U+00C5.
    // Renders identically to U+00C5 LATIN CAPITAL LETTER A WITH RING ABOVE.
    public static readonly string AngstromGrapheme = Canary("Å", "Angstrom sign (canonical singleton to U+00C5)", AngstromRune);

    // U+00C5 LATIN CAPITAL LETTER A WITH RING ABOVE. The canonical
    // replacement that NFC produces from the Angstrom singleton.
    public static readonly string LatinCapitalAWithRingAboveGrapheme = Canary("Å", "Latin capital a with ring above", 0x00C5);

    // A + combining ring above. The FormD (NFD) decomposition of
    // both U+00C5 and U+212B. Used as a literal under Compile(FormD).
    public static readonly string LatinAWithRingAboveDecomposedText = Canary("Å", "a + combining ring above (decomposed form of U+00C5 / U+212B)", 0x0041, 0x030A);

    // U+2126 OHM SIGN. Canonical singleton. NFC converts to U+03A9.
    // Renders identically to U+03A9 GREEK CAPITAL LETTER OMEGA.
    public static readonly string OhmGrapheme = Canary("Ω", "Ohm sign (canonical singleton to U+03A9)", 0x2126);

    // U+03A9 GREEK CAPITAL LETTER OMEGA. The canonical replacement
    // that NFC produces from the Ohm singleton.
    public static readonly string GreekCapitalOmegaGrapheme = Canary("Ω", "Greek capital omega", 0x03A9);

    // U+212A KELVIN SIGN. Canonical singleton. NFC converts to U+004B.
    // Renders identically to ASCII 'K'. Most dangerous of the singletons
    // in source: a literal "K" might be either the Kelvin sign or the
    // ASCII K and a reader can't tell.
    public static readonly string KelvinGrapheme = Canary("K", "Kelvin sign (canonical singleton to ASCII k)", 0x212A);

    // U+004B ASCII 'K'. The canonical replacement that NFC produces
    // from the Kelvin singleton. Constant exists so a test reader
    // can immediately tell which "K" is meant: ASCII or Kelvin.
    public static readonly string AsciiCapitalKGrapheme = Canary("K", "ASCII capital k", 0x004B);

    // U+2329 LEFT-POINTING ANGLE BRACKET. Canonical singleton. NFC
    // converts to U+3008 (CJK angle bracket). Renders similarly.
    public static readonly string LeftPointingAngleBracketGrapheme = Canary("〈", "left-pointing angle bracket (canonical singleton to U+3008)", 0x2329);

    // U+3008 LEFT ANGLE BRACKET (CJK). The canonical replacement
    // that NFC produces from U+2329.
    public static readonly string CjkLeftAngleBracketGrapheme = Canary("〈", "cjk left angle bracket", 0x3008);

    // U+2102 DOUBLE-STRUCK CAPITAL C. Compatibility singleton: NFKC
    // converts to ASCII 'C', NFC leaves it alone.
    public static readonly string DoubleStruckCGrapheme = Canary("ℂ", "double-struck capital c (compatibility singleton to ASCII c)", 0x2102);

    // U+0043 ASCII 'C'. The compatibility-form replacement that NFKC
    // produces from the double-struck C singleton.
    public static readonly string AsciiCapitalCGrapheme = Canary("C", "ASCII capital c", 0x0043);

    // === Identifier-relevant edge-case characters ===

    // U+0430 CYRILLIC SMALL LETTER A. Renders identically to Latin 'a'
    // (U+0061) in most fonts. The homoglyph star.
    public static readonly string CyrillicSmallAGrapheme = Canary("а", "Cyrillic small letter a (homoglyph for Latin a)", 0x0430);

    // === Normalization-test grapheme corpus ===
    // Constants used by the NormalizationExamples table for grapheme
    // behaviors across NFC/NFD/NFKC/NFKD. Brought here from
    // NormalizationExamples.cs so every interesting Unicode constant
    // lives in one place.

    // U+FB01 LATIN SMALL LIGATURE FI. Compatibility ligature that
    // NFKC decomposes into two ASCII letters "fi". One rune, one
    // grapheme in the source form, two graphemes after the
    // compatibility expansion.
    public static readonly string FiLigatureGrapheme = Canary("ﬁ", "compatibility ligature fi (expands to \"fi\" under FormKC)", 0xFB01);

    // U+2167 ROMAN NUMERAL EIGHT. Compatibility singleton that
    // expands to the four ASCII letters "VIII" under FormKC.
    public static readonly string RomanNumeralEightGrapheme = Canary("Ⅷ", "Roman numeral eight VIII (compatibility, expands to \"VIII\" under FormKC)", 0x2167);

    // U+FF21 FULLWIDTH LATIN CAPITAL LETTER A. Compatibility
    // singleton. NFKC substitutes it for plain ASCII 'A'. Used for
    // homoglyph / fullwidth-spoofing tests.
    public static readonly string FullwidthAGrapheme = Canary("Ａ", "fullwidth Latin capital A (substitutes to ASCII A under FormKC)", 0xFF21);

    // U+0958 DEVANAGARI LETTER QA, precomposed. Its canonical
    // decomposition (KA + NUKTA) is on the composition exclusions
    // list, so NFC of the precomposed source returns the decomposed
    // multi-rune form.
    public static readonly string DevanagariQaPrecomposedGrapheme = Canary("क़", "Devanagari letter qa precomposed (composition exclusion, NFC produces decomposed form)", 0x0958);

    // U+AC00 HANGUL SYLLABLE GA, precomposed. The textbook two-jamo
    // Hangul case: NFD decomposes into CHOSEONG KIYEOK + JUNGSEONG A.
    public static readonly string HangulGaPrecomposedGrapheme = Canary("가", "Hangul syllable ga precomposed (2-jamo)", 0xAC00);

    // U+AC01 HANGUL SYLLABLE GAG, precomposed. The three-jamo case:
    // NFD decomposes into CHOSEONG KIYEOK + JUNGSEONG A + JONGSEONG
    // KIYEOK.
    public static readonly string HangulGagPrecomposedGrapheme = Canary("각", "Hangul syllable gag precomposed (3-jamo)", 0xAC01);

    // U+0344 COMBINING GREEK DIALYTIKA TONOS. A single combining
    // mark that decomposes to TWO combining marks (U+0308 + U+0301).
    // Unicode lists U+0344 as the example of a non-starter
    // decomposition.
    public static readonly string DialytikaTonosPrecomposedGrapheme = Canary("̈́", "combining Greek dialytika tonos (defective, decomposes to two non-starters)", 0x0344);

    // U+0300 COMBINING GRAVE ACCENT, standalone. A defective
    // combining sequence: a combining mark with no preceding base.
    public static readonly string LoneCombiningGraveText = Canary("̀", "lone combining grave (defective combining sequence, no base)", 0x0300);

    // U+30AC KATAKANA LETTER GA, precomposed. The base+voicing form
    // KA + COMBINING KATAKANA-HIRAGANA VOICED SOUND MARK composes to
    // this single rune under NFC.
    public static readonly string FullwidthKatakanaGaPrecomposedGrapheme = Canary("ガ", "Katakana letter ga precomposed", 0x30AC);

    // "가" decomposed: U+1100 (HANGUL CHOSEONG KIYEOK) +
    // U+1161 (HANGUL JUNGSEONG A). NFC composes the two jamo back
    // to U+AC00.
    public static readonly string HangulGaTwoJamoDecomposedText = Canary("가", "Hangul ga decomposed (CHOSEONG KIYEOK + JUNGSEONG A)", 0x1100, 0x1161);

    // "각" decomposed: U+1100 (CHOSEONG KIYEOK) + U+1161
    // (JUNGSEONG A) + U+11A8 (JONGSEONG KIYEOK). NFC composes the
    // three jamo back to U+AC01.
    public static readonly string HangulGagThreeJamoDecomposedText = Canary("각", "Hangul gag decomposed (CHOSEONG KIYEOK + JUNGSEONG A + JONGSEONG KIYEOK)", 0x1100, 0x1161, 0x11A8);

    // KA + NUKTA decomposed: U+0915 + U+093C. On the composition
    // exclusions list, so NFC doesn't recompose it back to U+0958.
    public static readonly string DevanagariKaNuktaDecomposedText = Canary("क़", "Devanagari ka + nukta decomposed (composition exclusion, doesn't recompose)", 0x0915, 0x093C);

    // q + COMBINING DOT ABOVE (CCC=230) + COMBINING DOT BELOW
    // (CCC=220), non-canonical order. NFD reorders the marks into
    // canonical class order (220 before 230).
    public static readonly string QWithDotAboveDotBelowNonCanonicalText = Canary("q̣̇", "q + dot-above + dot-below (non-canonical mark order)", 0x0071, 0x0307, 0x0323);

    // q + COMBINING DOT BELOW (CCC=220) + COMBINING DOT ABOVE
    // (CCC=230), already in canonical class order. NFD leaves it.
    public static readonly string QWithDotBelowDotAboveCanonicalText = Canary("q̣̇", "q + dot-below + dot-above (canonical mark order)", 0x0071, 0x0323, 0x0307);

    // Ḋ (U+1E0A, D WITH DOT ABOVE precomposed) + COMBINING DOT
    // BELOW. The UAX #15 worked example for canonical-composite
    // shifting under recomposition.
    public static readonly string DWithDotAboveAndDotBelowSourceText = Canary("Ḍ̇", "D with dot above (U+1E0A) + combining dot below", 0x1E0A, 0x0323);

    // Ḍ (U+1E0C, D WITH DOT BELOW precomposed) + COMBINING DOT
    // ABOVE. The FormC result of the dot-above-dot-below source
    // after canonical reordering picks a different composite.
    public static readonly string DWithDotBelowAndDotAboveFormCText = Canary("Ḍ̇", "D with dot below (U+1E0C) + combining dot above (composite shifted)", 0x1E0C, 0x0307);

    // D + COMBINING DOT BELOW + COMBINING DOT ABOVE, fully
    // decomposed. NFD form of both U+1E0A+U+0323 and U+1E0C+U+0307.
    public static readonly string DWithBothDotsFullyDecomposedText = Canary("Ḍ̇", "D + dot-below + dot-above (fully decomposed)", 0x0044, 0x0323, 0x0307);

    // COMBINING DIAERESIS + COMBINING ACUTE: the canonical
    // decomposition of U+0344 COMBINING GREEK DIALYTIKA TONOS. Two
    // adjacent combining marks with no preceding base.
    public static readonly string DialytikaTonosDecomposedText = Canary("̈́", "diaeresis + acute (two defective combining marks)", 0x0308, 0x0301);

    // U+FF76 HALFWIDTH KATAKANA LETTER KA + U+FF9E HALFWIDTH
    // KATAKANA VOICED SOUND MARK. NFKC composes the two halfwidths
    // into U+30AC (precomposed Katakana GA).
    public static readonly string HalfwidthKaWithVoicingSourceText = Canary("ｶﾞ", "halfwidth katakana ka + halfwidth voicing sound mark", 0xFF76, 0xFF9E);

    // U+30AB KATAKANA LETTER KA + U+3099 COMBINING KATAKANA-HIRAGANA
    // VOICED SOUND MARK. NFC composes to U+30AC.
    public static readonly string KatakanaKaPlusCombiningVoicingText = Canary("ガ", "katakana ka + combining katakana-hiragana voiced sound mark", 0x30AB, 0x3099);

    // Family man + ZWJ + woman + ZWJ + girl. Distinct from
    // FamilyManWomanBoyGrapheme by ending with girl (U+1F467)
    // instead of boy (U+1F466).
    public static readonly string FamilyManWomanGirlGrapheme = Canary("👨‍👩‍👧", "family man + ZWJ + woman + ZWJ + girl emoji", ManEmojiRune, 0x200D, WomanEmojiRune, 0x200D, 0x1F467);



    // === Additional atomics used across multiple test files ===

    // U+00E0 LATIN SMALL LETTER A WITH GRAVE. Precomposed.
    public static readonly string LatinSmallAWithGraveGrapheme = Canary("à", "latin small letter a with grave", 0x00E0);

    // U+00E8 LATIN SMALL LETTER E WITH GRAVE. Precomposed.
    public static readonly string LatinSmallEWithGraveGrapheme = Canary("è", "latin small letter e with grave", 0x00E8);

    // U+00DF LATIN SMALL LETTER SHARP S. The German 'eszett'.
    // Normalization leaves it unchanged under all four forms. The
    // famous "SS" expansion is Unicode's full uppercase mapping
    // (SpecialCasing.txt), a case mapping that normalization never
    // applies, and .NET's ToUpperInvariant doesn't apply it either
    // (the eszett comes back unchanged).
    public static readonly string LatinSmallSharpSGrapheme = Canary("ß", "latin small letter sharp s (eszett)", 0x00DF);

    // U+00F8 LATIN SMALL LETTER O WITH STROKE. Half-diminished
    // chord symbol in jazz notation.
    public static readonly string LatinSmallOWithStrokeGrapheme = Canary("ø", "latin small letter o with stroke", 0x00F8);

    // U+00B0 DEGREE SIGN. Diminished chord symbol in jazz notation.
    public static readonly string DegreeSignGrapheme = Canary("°", "degree sign", 0x00B0);

    // U+00D7 MULTIPLICATION SIGN. Used in UAX #29 conformance test
    // notation as the no-break marker between codepoints.
    public static readonly string MultiplicationSignGrapheme = Canary("×", "multiplication sign", 0x00D7);

    // U+2014 EM DASH. Punctuation. Common bidi/normalization gotcha.
    public static readonly string EmDashGrapheme = Canary("—", "em dash", 0x2014);

    // U+0394 GREEK CAPITAL LETTER DELTA. Major-seventh chord symbol
    // in jazz notation.
    public static readonly string GreekCapitalDeltaGrapheme = Canary("Δ", "greek capital letter delta", 0x0394);

    // U+266D MUSIC FLAT SIGN. Flat accidental in music notation.
    public static readonly string MusicFlatSignGrapheme = Canary("♭", "music flat sign", 0x266D);

    // U+266F MUSIC SHARP SIGN. Sharp accidental in music notation.
    public static readonly string MusicSharpSignGrapheme = Canary("♯", "music sharp sign", 0x266F);

    // U+2118 SCRIPT CAPITAL P. Sm category but added to XID_Start
    // via Other_ID_Start. Used to test the XidStartAdds table.
    public static readonly string ScriptCapitalPGrapheme = Canary("℘", "script capital p", 0x2118);

    // U+213C DOUBLE-STRUCK SMALL PI. Compatibility character.
    public static readonly string DoubleStruckSmallPiGrapheme = Canary("ℼ", "double-struck small pi", 0x213C);

    // U+FDFA ARABIC LIGATURE SALLALLAHOU ALAYHE WASALLAM. Lo by
    // GC but excluded from XID_Start because its NFKC decomposition
    // is a multi-word phrase. Tests the XidStartExclusions table.
    public static readonly string ArabicLigatureSallallahouGrapheme = Canary("ﷺ", "arabic ligature sallallahou alayhe wasallam", 0xFDFA);

    // U+FF11 FULLWIDTH DIGIT ONE. Compatibility-ASCII digit.
    // NFKC converts to ASCII '1'.
    public static readonly string FullwidthDigitOneGrapheme = Canary("１", "fullwidth digit one", 0xFF11);

    // U+3131 HANGUL LETTER KIYEOK. Compatibility jamo from the
    // Hangul Compatibility Jamo block. NFKC converts to U+1100
    // (CHOSEONG KIYEOK).
    public static readonly string HangulLetterKiyeokGrapheme = Canary("ㄱ", "hangul letter kiyeok (compatibility jamo)", 0x3131);

    // U+314F HANGUL LETTER A. Compatibility jamo from the Hangul
    // Compatibility Jamo block. NFKC converts to U+1161 (JUNGSEONG A).
    public static readonly string HangulLetterAGrapheme = Canary("ㅏ", "hangul letter a (compatibility jamo)", 0x314F);

    // U+FFA1 HALFWIDTH HANGUL LETTER KIYEOK. Halfwidth jamo from the
    // Halfwidth and Fullwidth Forms block. NFKC converts it to U+1100
    // (CHOSEONG KIYEOK), the same conjoining jamo the compatibility
    // jamo U+3131 above converts to.
    public static readonly string HalfwidthHangulKiyeokGrapheme = Canary("ﾡ", "halfwidth hangul letter kiyeok", 0xFFA1);

    // U+FFC2 HALFWIDTH HANGUL LETTER A. Halfwidth jamo. NFKC converts
    // it to U+1161 (JUNGSEONG A), the same conjoining jamo the
    // compatibility jamo U+314F above converts to, so a halfwidth
    // kiyeok + a pair composes to U+AC00 (가) under FormKC exactly
    // like the compatibility pair does.
    public static readonly string HalfwidthHangulAGrapheme = Canary("ￂ", "halfwidth hangul letter a", 0xFFC2);

    // U+304B HIRAGANA LETTER KA. Used as a base in tests of
    // Katakana-Hiragana voiced-sound-mark interactions.
    public static readonly string HiraganaKaGrapheme = Canary("か", "hiragana letter ka", 0x304B);

    // U+309B KATAKANA-HIRAGANA VOICED SOUND MARK. Sk category.
    // UAX #31 adds it to XID_Start via Other_ID_Start (low end
    // of the 309B..309C range).
    public const int KatakanaHiraganaVoicedSoundMarkRune = 0x309B;
    public static readonly string KatakanaHiraganaVoicedSoundMarkGrapheme = Canary("゛", "katakana-hiragana voiced sound mark", KatakanaHiraganaVoicedSoundMarkRune);

    // U+309C KATAKANA-HIRAGANA SEMI-VOICED SOUND MARK. High end
    // of the Sk range added to XID_Start.
    public const int KatakanaHiraganaSemiVoicedSoundMarkRune = 0x309C;
    public static readonly string KatakanaHiraganaSemiVoicedSoundMarkGrapheme = Canary("゜", "katakana-hiragana semi-voiced sound mark", KatakanaHiraganaSemiVoicedSoundMarkRune);

    // U+2764 HEAVY BLACK HEART. Text-presentation by default,
    // becomes emoji presentation when followed by VS16 (U+FE0F).
    public static readonly string HeavyBlackHeartGrapheme = Canary("❤", "heavy black heart", 0x2764);

    // U+1D400 MATHEMATICAL BOLD CAPITAL A. Supplementary-plane
    // letter that NFKC converts to ASCII 'A'. Homoglyph spoofing test.
    public static readonly string MathematicalBoldCapitalAGrapheme = Canary("𝐀", "mathematical bold capital a", 0x1D400);

    // U+1F600 GRINNING FACE. Supplementary-plane emoji used in
    // tests that need a single-rune emoji that isn't part of a
    // ZWJ sequence.
    public static readonly string GrinningFaceEmojiGrapheme = Canary("😀", "grinning face emoji", 0x1F600);

    // "café" with precomposed U+00E9 LATIN SMALL LETTER E WITH
    // ACUTE. Four codepoints, four graphemes.
    public static readonly string CafePrecomposedGrapheme = Canary("café", "cafe with precomposed e-acute", 0x0063, 0x0061, 0x0066, LatinEAcuteRune);

    // "café" with decomposed e + combining acute. Five codepoints,
    // four graphemes. NFC composes to CafePrecomposedGrapheme.
    public static readonly string CafeDecomposedText = Canary("café", "cafe with decomposed e + combining acute", 0x0063, 0x0061, 0x0066, 0x0065, 0x0301);

    // Greek "καλημέρα" ("good morning"). All eight codepoints are Ll
    // (Letter, Lowercase), pure XID_Start territory.
    public static readonly string GreekKalimeraIdentifier = Canary("καλημέρα", "greek 'good morning' identifier (kalimera)", 0x03BA, 0x03B1, 0x03BB, 0x03B7, 0x03BC, 0x03AD, 0x03C1, 0x03B1);

    // Devanagari "हिन्दी" ("hindi"). Six runes that the lexer
    // composes into three multi-rune graphemes (Lo + Mc + Lo + Mn +
    // Lo + Mc).
    public static readonly string DevanagariHindiIdentifier = Canary("हिन्दी", "devanagari 'hindi' identifier (ha + i + na + virama + da + ii)", 0x0939, 0x093F, 0x0928, 0x094D, 0x0926, 0x0940);

    // Fullwidth Latin "ｆｏｏ" (U+FF46 + U+FF4F + U+FF4F). NFKC
    // converts each to plain ASCII, so it matches ASCII "foo" under
    // FormKC but not under FormC.
    public static readonly string FullwidthFooGrapheme = Canary("ｆｏｏ", "fullwidth latin 'foo' (f + o + o)", 0xFF46, 0xFF4F, 0xFF4F);

    // U+FB01 LATIN SMALL LIGATURE FI followed by two ASCII 'o'.
    // NFKC converts the ligature to two ASCII letters, yielding
    // "fioo".
    public static readonly string FiLigaturePlusOoText = Canary("ﬁoo", "fi-ligature + o + o (folds to 'fioo' under NFKC)", 0xFB01, 0x006F, 0x006F);

    // U+FB00 LATIN SMALL LIGATURE FF followed by two ASCII 'o'.
    // NFKC converts the ligature to two ASCII letters, yielding
    // "ffoo".
    public static readonly string FfLigaturePlusOoText = Canary("ﬀoo", "ff-ligature + o + o (folds to 'ffoo' under NFKC)", 0xFB00, 0x006F, 0x006F);

    // Mathematical Bold "𝐟𝐨𝐨". Three supplementary-plane runes
    // that NFKC converts to ASCII "foo". Locks in that the homoglyph
    // table reaches past the surrogate gap.
    public static readonly string MathBoldFooIdentifier = Canary("𝐟𝐨𝐨", "mathematical bold 'foo' (f + o + o)", 0x1D41F, 0x1D428, 0x1D428);

    // Fullwidth Latin "ｓｅｌｅｃｔ" (six runes, each converting to
    // its ASCII counterpart under NFKC). Used in the SQL-keyword
    // homoglyph bypass test.
    public static readonly string FullwidthSelectIdentifier = Canary("ｓｅｌｅｃｔ", "fullwidth latin 'select'", 0xFF53, 0xFF45, 0xFF4C, 0xFF45, 0xFF43, 0xFF54);



    // === Integer codepoint constants for int-typed APIs (TokenSet.Single,
    //     char comparisons, [TestCase] attribute args) ===
    // Compile-time-constant int siblings for the most commonly referenced
    // codepoints. Adds nothing beyond the string form for runtime checks,
    // but gives [TestCase] attributes and char-typed comparisons a named
    // constant to reference instead of a bare hex literal.

    // U+00E9 LATIN SMALL LETTER E WITH ACUTE.
    public const int LatinEAcuteRune = 0x00E9;

    // U+212B ANGSTROM SIGN.
    public const int AngstromRune = 0x212B;

    // U+D800: lowest high-surrogate code unit (lone, invalid scalar).
    public const int HighSurrogateMinRune = 0xD800;

    // U+DBFF: highest high-surrogate code unit (lone, invalid scalar).
    public const int HighSurrogateMaxRune = 0xDBFF;

    // U+DC00: lowest low-surrogate code unit (lone, invalid scalar).
    public const int LowSurrogateMinRune = 0xDC00;

    // U+DFFF: highest low-surrogate code unit (lone, invalid scalar).
    public const int LowSurrogateMaxRune = 0xDFFF;

    // U+D83D: high-surrogate code unit that starts common emoji pairs.
    public const int EmojiStartHighSurrogateRune = 0xD83D;

    // === Real-world Unicode gotcha constants used by UnexpectedUnicodeTests ===

    // U+0130 LATIN CAPITAL LETTER I WITH DOT ABOVE. The uppercase form of
    // Turkish 'i'. Famous locale bug: a locale-aware ToLower on a Turkish
    // system maps ASCII 'I' to 'ı' (U+0131) and ASCII 'i' to 'İ' (U+0130),
    // breaking case-insensitive string comparisons. Bit Spotify in 2013,
    // .NET Framework's String.Compare without an explicit culture, Win32
    // CompareString, Java's String.toLowerCase, and others. The parser's
    // LiteralIgnoreAsciiCase is named for the ASCII-only restriction
    // specifically to avoid this: ASCII 'I' folds only to ASCII 'i', and
    // U+0130 is its own rune that doesn't participate in the fold.
    public static readonly string TurkishCapitalIWithDotGrapheme = Canary(
        "İ", "latin capital I with dot above (Turkish dotted I)", 0x0130);

    // U+0131 LATIN SMALL LETTER DOTLESS I. The lowercase form of Turkish
    // capital 'I' under Turkish locale rules. Its own rune, and it
    // doesn't fold to ASCII 'i' under LiteralIgnoreAsciiCase.
    public static readonly string TurkishSmallDotlessIGrapheme = Canary(
        "ı", "latin small dotless i (Turkish dotless i)", 0x0131);

    // U+13A0 CHEROKEE LETTER A. Cherokee block runs U+13A0..U+13F5. Several
    // letters in the block render as glyphs that closely resemble Latin
    // capitals in common fonts (the most-cited examples: U+13A0 'Ꭰ' near
    // Latin D or T, U+13C7 'Ꮇ' near Latin W, U+13F4 'Ꮤ' near Latin W).
    // Used in the Microsoft 2018 phishing campaign where attacker
    // hostnames mixed Cherokee letters with Latin to spoof legitimate
    // names. Category Lo, so Identifier() accepts it under the default
    // UAX #31 profile. A Latin-only TokenSet rejects it.
    public static readonly string CherokeeLetterAGrapheme = Canary(
        "Ꭰ", "cherokee letter A (homoglyph for latin caps in some fonts)", 0x13A0);

    // U+000B VERTICAL TAB (VT). C0 control that renders like whitespace,
    // but UAX #18 counts it as a line terminator, so the parser does too
    // (it's in TokenSet.LineTerminators, but Token('\n') doesn't catch it).
    public static readonly string VerticalTabText = Canary(
        "\u000B", "vertical tab (UAX #18 line terminator)", 0x000B);

    // U+000C FORM FEED (FF). The classic page-break control. C0 control,
    // UAX #18 line terminator, treated as one ordinary token the parser
    // counts as a line break.
    public static readonly string FormFeedText = Canary(
        "\u000C", "form feed (UAX #18 line terminator)", 0x000C);

    // U+2028 LINE SEPARATOR. UAX #14 line break, UAX #18 line terminator.
    // Famous JavaScript bug: ECMAScript source disallowed U+2028 and U+2029
    // as unescaped characters in string literals, but JSON.parse allowed
    // them. JSONP responses containing these characters in user content
    // produced "Unexpected token ILLEGAL" errors in the browser. Fixed in
    // ES2019 (string-literal grammar updated to allow them). The Inductor
    // parser treats U+2028 as one ordinary token. A grammar matching
    // Token('\n') doesn't catch it, but EndOfLine() does (it's in
    // TokenSet.LineTerminators).
    public static readonly string LineSeparatorText = Canary(
        "\u2028", "line separator (UAX #18 line terminator)", 0x2028);

    // U+2029 PARAGRAPH SEPARATOR. Same JSON / JavaScript story as U+2028.
    public static readonly string ParagraphSeparatorText = Canary(
        "\u2029", "paragraph separator (UAX #18 line terminator)", 0x2029);

    // U+0085 NEXT LINE (NEL). EBCDIC line-terminator-equivalent that
    // crossed into Unicode for round-tripping with IBM mainframe text. C0
    // control (Cc), UAX #18 line terminator. Real-world bug: Java's
    // BufferedReader.readLine treats NEL as a line terminator on some
    // JVMs but not others. XML 1.1 explicitly added it to the newline
    // characters list (XML 1.0 didn't). The Inductor parser treats NEL
    // as one ordinary token. Token('\n') doesn't catch it, EndOfLine()
    // does.
    public static readonly string NextLineText = Canary(
        "\u0085", "next line / NEL (EBCDIC heritage line terminator)", 0x0085);

    // U+2066 LEFT-TO-RIGHT ISOLATE (LRI). One of the Unicode 6.3 bidi
    // isolate controls (LRI, RLI U+2067, FSI U+2068, PDI U+2069). The
    // original Trojan Source paper (CVE-2021-42574) demonstrates source
    // code attacks using the directional formatting characters, including
    // these isolates as well as the older RLO. Treated by the parser as
    // an ordinary token. The parser doesn't apply Unicode's Bidirectional
    // Algorithm, so a Trojan Source string can't trick the grammar into
    // matching something different from what's in the input.
    public static readonly string LeftToRightIsolateText = Canary(
        "\u2066", "left-to-right isolate (bidi control, Trojan Source variant)", 0x2066);

    // U+034F COMBINING GRAPHEME JOINER (CGJ). Combining mark whose only
    // purpose is to block canonical reordering of combining marks under
    // normalization. Used in Hebrew and Yiddish typography to keep marks
    // in non-canonical visual order. UAX #29 GCB=Extend, so when bare at
    // the start of input it ends up as its own one-character cluster (the
    // same shape as the other bare combining marks).
    public static readonly string CombiningGraphemeJoinerText = Canary(
        "\u034F", "combining grapheme joiner (blocks NFC mark reordering)", 0x034F);

    // U+03C3 GREEK SMALL LETTER SIGMA. The "non-final" form of lowercase
    // sigma, used in the middle of Greek words.
    public static readonly string GreekSmallSigmaGrapheme = Canary(
        "σ", "greek small letter sigma (non-final form)", 0x03C3);

    // U+03C2 GREEK SMALL LETTER FINAL SIGMA. Word-final form of lowercase
    // sigma. Different rune from U+03C3 even though Unicode treats them as
    // case-equivalent to the same uppercase Σ. Real-world Greek search
    // bug: matching software that compares rune-by-rune misses "πῶς" vs
    // "πῶσ" because the final sigma and non-final sigma are different
    // code points, but Greek readers consider them the same letter.
    public static readonly string GreekSmallFinalSigmaGrapheme = Canary(
        "ς", "greek small letter final sigma (word-final form)", 0x03C2);

    // U+1F3F4 WAVING BLACK FLAG. Base emoji for tag-sequence subdivision
    // flags (England, Scotland, Wales). Stand-alone it renders as a
    // generic black/pirate flag.
    public const int BlackFlagRune = 0x1F3F4;
    public static readonly string BlackFlagGrapheme = Canary(
        "🏴", "waving black flag (base for tag-sequence subdivision flags)", BlackFlagRune);

    // U+E0067 TAG LATIN SMALL LETTER G. One of the tag characters used
    // inside emoji tag sequences (U+E0020..U+E007E). UAX #29 GCB=Extend.
    public static readonly string TagLatinSmallGText = Canary(
        "󠁧", "tag latin small letter g", 0xE0067);

    // U+E0062 TAG LATIN SMALL LETTER B.
    public static readonly string TagLatinSmallBText = Canary(
        "󠁢", "tag latin small letter b", 0xE0062);

    // U+E0065 TAG LATIN SMALL LETTER E.
    public static readonly string TagLatinSmallEText = Canary(
        "󠁥", "tag latin small letter e", 0xE0065);

    // U+E006E TAG LATIN SMALL LETTER N.
    public static readonly string TagLatinSmallNText = Canary(
        "󠁮", "tag latin small letter n", 0xE006E);

    // U+E007F CANCEL TAG. Marks the end of an emoji tag sequence.
    // UAX #29 GCB=Extend.
    public static readonly string CancelTagText = Canary(
        "󠁿", "cancel tag (end of emoji tag sequence)", 0xE007F);

    // England flag emoji as an emoji tag sequence (UTS #51): WAVING
    // BLACK FLAG + tag chars for "gbeng" + CANCEL TAG. UAX #29 rule GB9
    // keeps the whole sequence (7 runes, 14 UTF-16 chars) as a single
    // grapheme cluster, because every tag character (cancel tag
    // included) is GCB=Extend. Renderers that recognize the sequence
    // display the St George's Cross. Renderers that don't show a black
    // flag followed by the tag letters or nothing at all.
    public static readonly string EnglandFlagGrapheme = Canary(
        "🏴󠁧󠁢󠁥󠁮󠁧󠁿", "england flag emoji (waving black flag + GBENG tag sequence)",
        BlackFlagRune, 0xE0067, 0xE0062, 0xE0065, 0xE006E, 0xE0067, 0xE007F);

    // U+1F1F8 REGIONAL INDICATOR LETTER S. Second half of the US flag
    // (paired with U+1F1FA REGIONAL INDICATOR LETTER U).
    public const int RegionalIndicatorSRune = 0x1F1F8;
    public static readonly string RegionalIndicatorSText = Canary(
        "🇸", "regional indicator letter s (second half of US flag)", RegionalIndicatorSRune);

    // U+1F1EB REGIONAL INDICATOR LETTER F. Used after the US flag pair
    // (U + S) to construct an input with three consecutive regional
    // indicators, exercising UAX #29's pair-from-left rule (GB12/GB13).
    public const int RegionalIndicatorFRune = 0x1F1EB;
    public static readonly string RegionalIndicatorFText = Canary(
        "🇫", "regional indicator letter f (lone, used as trailing third RI)", RegionalIndicatorFRune);

    // === Additional real-world script samples ===
    // Tamil, Tibetan, Hebrew (RTL), and a Simplified/Traditional Chinese
    // pair. Each is a real word, not a hand-assembled test string, so the
    // identifier, grapheme, and JSON-roundtrip fixtures exercise the
    // grammars on text people actually type.

    // Tamil "தமிழ்" (the word "Tamil"). Five runes: TA (Lo), MA (Lo),
    // VOWEL SIGN I (Mc), LLLA (Lo), VIRAMA (Mn). The lexer composes
    // these into three graphemes (TA, MA+vowel-sign-i, LLLA+virama).
    // The vowel sign and virama are both XID_Continue, so the whole
    // word is a valid identifier after the leading consonant.
    public static readonly string TamilTamizhIdentifier = Canary(
        "தமிழ்", "tamil 'tamil' identifier (ta + ma + vowel-sign-i + llla + virama)",
        0x0BA4, 0x0BAE, 0x0BBF, 0x0BB4, 0x0BCD);

    // Tibetan "བོད" (the word "Tibet", romanized "bod"). Three runes:
    // BA (Lo) + VOWEL SIGN O (Mn) + DA (Lo). The vowel sign combines
    // with the preceding consonant into one grapheme, giving two
    // graphemes total.
    public static readonly string TibetanBodIdentifier = Canary(
        "བོད", "tibetan 'tibet' identifier (ba + vowel-sign-o + da)",
        0x0F56, 0x0F7C, 0x0F51);

    // Tibetan stacked syllable: KA (Lo) + SUBJOINED LETTER SSA (Mn).
    // Tibetan stacks a subjoined consonant under its base. The subjoined
    // letter is GCB=Extend, so base + subjoined is one grapheme made of
    // two runes. The textbook Tibetan grapheme-cluster case, parallel to
    // Devanagari conjuncts but using a dedicated subjoined code point
    // rather than a virama linker.
    public static readonly string TibetanStackedKaSsaGrapheme = Canary(
        "ཀྵ", "tibetan stacked ka + subjoined ssa (one grapheme, two runes)",
        0x0F40, 0x0FB5);

    // Hebrew "עברית" (the word "Hebrew", romanized "ivrit"). Five Lo
    // letters: AYIN, BET, RESH, YOD, TAV. RTL script: the runes are
    // stored in logical (reading) order, which is the order the parser
    // walks them. No combining marks, so five single-rune graphemes.
    public static readonly string HebrewIvritIdentifier = Canary(
        "עברית", "hebrew 'hebrew' identifier (ayin + bet + resh + yod + tav)",
        0x05E2, 0x05D1, 0x05E8, 0x05D9, 0x05EA);

    // Hebrew "שָׁלוֹם" (shalom, "peace") written with niqqud (vowel
    // points). Seven runes: SHIN (Lo) + QAMATS (Mn) + SHIN DOT (Mn) +
    // LAMED (Lo) + VAV (Lo) + HOLAM (Mn) + FINAL MEM (Lo). The points
    // are combining marks (XID_Continue) that combine with their base
    // letters, giving four graphemes from seven runes. RTL plus
    // combining marks in one word.
    public static readonly string HebrewShalomWithNiqqudIdentifier = Canary(
        "שָׁלוֹם", "hebrew 'shalom' with niqqud (shin + qamats + shin-dot + lamed + vav + holam + final-mem)",
        0x05E9, 0x05B8, 0x05C1, 0x05DC, 0x05D5, 0x05B9, 0x05DD);

    // U+6C49 CJK UNIFIED IDEOGRAPH (Simplified Chinese "hàn", the first
    // character of 汉字 "Chinese characters"). One rune, one grapheme.
    public const int ChineseSimplifiedHanRune = 0x6C49;
    public static readonly string ChineseSimplifiedHanGrapheme = Canary(
        "汉", "simplified chinese han (first char of 'hanzi')", ChineseSimplifiedHanRune);

    // U+6F22 CJK UNIFIED IDEOGRAPH (Traditional Chinese "hàn", the
    // traditional form of U+6C49). A different code point, not a
    // normalization variant: NFC/NFKC leave both alone, so the parser
    // treats simplified and traditional as distinct text. One rune.
    public const int ChineseTraditionalHanRune = 0x6F22;
    public static readonly string ChineseTraditionalHanGrapheme = Canary(
        "漢", "traditional chinese han (traditional form of U+6C49)", ChineseTraditionalHanRune);

    // Simplified "汉字" (hànzì, "Chinese characters"). U+6C49 + U+5B57.
    // Two Lo ideographs, two single-rune graphemes.
    public static readonly string ChineseSimplifiedHanziIdentifier = Canary(
        "汉字", "simplified chinese 'hanzi' (han + zi)", ChineseSimplifiedHanRune, 0x5B57);

    // Traditional "漢字" (hànzì, "Chinese characters"). U+6F22 + U+5B57.
    // Same second character (字) as the simplified form, different first
    // character. Pair with ChineseSimplifiedHanziIdentifier to verify
    // the two stay distinct under normalization.
    public static readonly string ChineseTraditionalHanziIdentifier = Canary(
        "漢字", "traditional chinese 'hanzi' (han + zi)", ChineseTraditionalHanRune, 0x5B57);

    // Cyrillic "привет" (privet, "hello"). Six Ll letters: PE, ER, I, VE,
    // IE, TE. No combining marks, so six single-rune graphemes. A real
    // running-text word, distinct from the lone homoglyph
    // CyrillicSmallAGrapheme above that the security tests use.
    public static readonly string CyrillicPrivetIdentifier = Canary(
        "привет", "cyrillic 'hello' identifier (pe + er + i + ve + ie + te)",
        0x043F, 0x0440, 0x0438, 0x0432, 0x0435, 0x0442);

    // Arabic "عربية" (arabiyya, "Arabic"). Five Lo letters: AIN, REH, BEH,
    // YEH, TEH MARBUTA. RTL script stored in logical (reading) order,
    // which is the order the parser walks. No harakat (vowel marks), so
    // five single-rune graphemes. Parallel to the Hebrew ivrit sample but
    // in a second RTL script.
    public static readonly string ArabicArabiyyaIdentifier = Canary(
        "عربية", "arabic 'arabic' identifier (ain + reh + beh + yeh + teh-marbuta)",
        0x0639, 0x0631, 0x0628, 0x064A, 0x0629);

    // Japanese "ひらがなカタカナ漢字" (literally "hiragana katakana kanji").
    // A real string that names the three Japanese writing systems it's
    // made of, mixing all three in one identifier run: four hiragana
    // (including the precomposed voiced GA, U+304C), four katakana, and
    // two kanji. Every rune is Lo / XID, so the lexer reads the whole
    // mixed-script run as one identifier.
    public static readonly string JapaneseHiraganaKatakanaKanjiIdentifier = Canary(
        "ひらがなカタカナ漢字", "japanese 'hiragana katakana kanji' (mixed-script identifier)",
        0x3072, 0x3089, 0x304C, 0x306A, 0x30AB, 0x30BF, 0x30AB, 0x30CA, 0x6F22, 0x5B57);

    // Korean "한국어" (hangugeo, "the Korean language") as precomposed
    // Hangul syllables: HAN (U+D55C) + GUK (U+AD6D) + EO (U+C5B4). Three
    // Lo syllables, three single-rune graphemes. The natural, NFC-stable
    // form Korean text arrives in. The decomposed conjoining-jamo form
    // (what macOS filesystems hand back) is derived from this constant in
    // the Korean identifier test rather than stored separately, so the
    // syllables and jamo can't drift apart.
    public static readonly string KoreanHangugeoIdentifier = Canary(
        "한국어", "korean 'korean language' identifier, precomposed syllables (han + guk + eo)",
        0xD55C, 0xAD6D, 0xC5B4);
}
