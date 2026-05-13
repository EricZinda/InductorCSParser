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
// When adding a new symbol, state its rune-vs-grapheme status in the comment.
internal static class UnicodeExamples
{
    // 👋 waving hand. Supplementary-plane rune (needs a surrogate pair in
    // UTF-16). One rune, one grapheme on its own.
    public const int WavingHandRune = 0x1F44B;
    public const string WavingHandGrapheme = "\uD83D\uDC4B"; // looks like waving hand emoji

    // 🏽 medium skin tone modifier. Combining modifier rune that attaches to a
    // base character to form a multi-rune grapheme. Not typically rendered
    // standalone.
    public const int MediumSkinToneRune = 0x1F3FD;
    public const string MediumSkinToneText = "\uD83C\uDFFD"; // looks like medium-skin-tone modifier (combines with preceding emoji)

    // 👋🏽 waving hand + medium skin tone. ONE grapheme made of TWO runes
    // (4 UTF-16 chars total). The lexer reads the whole sequence as a
    // single token.
    public const string SkinTonedWaveGrapheme = WavingHandGrapheme + MediumSkinToneText; // looks like medium-skin-tone waving hand

    // 🎸 guitar. One rune, one grapheme.
    public const int GuitarRune = 0x1F3B8;
    public const string GuitarGrapheme = "\uD83C\uDFB8"; // looks like guitar emoji

    // 🎹 musical keyboard. One rune, one grapheme. Used alongside Guitar
    // as a "different emoji, same shape" mismatch example.
    public const int MusicalKeyboardRune = 0x1F3B9;
    public const string MusicalKeyboardGrapheme = "\uD83C\uDFB9"; // looks like musical keyboard emoji

    // U+0301 combining acute. Combining mark that attaches to a base character
    // to form a multi-rune grapheme. Not a standalone grapheme.
    public const string CombiningAcuteText = "\u0301"; // looks like a floating acute accent (attaches to preceding base)

    // é as e + combining acute. The textbook multi-rune Token: TWO runes,
    // ONE grapheme (2 UTF-16 chars total). Works on every runtime including
    // legacy StringInfo because the base+combining-mark rule predates UAX #29.
    // Use this when a test needs a multi-rune grapheme that segments the same
    // way on .NET 5+ and on Unity Mono / IL2CPP.
    public const string LatinEAcuteGrapheme = "e" + CombiningAcuteText; // looks like é (e + combining acute, decomposed)

    // é as the single precomposed rune U+00E9 (LATIN SMALL LETTER E WITH
    // ACUTE). One rune, one grapheme. Pair with LatinEAcuteGrapheme when
    // a test wants to be explicit about which form it's using.
    public const string LatinEAcutePrecomposedGrapheme = "é"; // looks like é (precomposed)

    // 🤷‍♀️ woman shrugging. ZWJ emoji sequence: base shrug rune + ZWJ +
    // female sign + emoji variation selector. UAX #29 sees ONE grapheme.
    // Legacy StringInfo splits it. Use this to test the ZWJ rule.
    public const string WomanShruggingGrapheme = "\uD83E\uDD37\u200D\u2640\uFE0F"; // looks like woman-shrugging emoji (ZWJ sequence)

    // 🇺🇸 US flag. Two regional indicator code points (U + S). UAX #29 sees
    // ONE grapheme. Legacy StringInfo splits it. Use this to test the
    // regional-indicator pairing rule.
    public const string USFlagGrapheme = "\uD83C\uDDFA\uD83C\uDDF8"; // looks like US flag emoji (regional indicator pair)

    // ก + ํา Thai "kam". SARA AM is the canonical extended-grapheme-cluster
    // case (a vowel sign that visually composes with the preceding consonant).
    // UAX #29 sees ONE grapheme. Legacy StringInfo splits it. Use this to
    // test the SARA AM rule.
    public const string ThaiKamGrapheme = "\u0E01\u0E33"; // looks like \u0E01\u0E33 (Thai KO KAI + SARA AM, "kam")

    // 👨 man. One rune, one grapheme. Used as a base for ZWJ sequences
    // in malformed-Unicode and joiner tests.
    public const int ManEmojiRune = 0x1F468;
    public const string ManEmojiGrapheme = "\uD83D\uDC68"; // looks like man emoji

    // \uD83D\uDC69 woman. One rune, one grapheme. Paired with ManEmoji and
    // BoyEmoji to build the family ZWJ sequence below, and with
    // Briefcase to build the woman-office-worker profession ZWJ.
    public const int WomanEmojiRune = 0x1F469;
    public const string WomanEmojiGrapheme = "\uD83D\uDC69"; // looks like woman emoji

    // \uD83D\uDC66 boy. One rune, one grapheme. Used as the third element of the
    // family ZWJ sequence below.
    public const int BoyEmojiRune = 0x1F466;
    public const string BoyEmojiGrapheme = "\uD83D\uDC66"; // looks like boy emoji

    // Family ZWJ sequence: man + ZWJ + woman + ZWJ + boy. UAX #29 GB11
    // keeps an emoji + ZWJ + emoji chain as a single cluster. Five
    // runes (man, ZWJ, woman, ZWJ, boy), 8 UTF-16 chars. Distinct from
    // the woman-shrugging sequence because the joins are between three
    // base emoji rather than base + sign + variation selector. Locks
    // in that the multi-link ZWJ rule fires at every joiner along the
    // chain, not just the first one.
    public const string FamilyManWomanBoyGrapheme =
        ManEmojiGrapheme + ZeroWidthJoinerText
        + WomanEmojiGrapheme + ZeroWidthJoinerText
        + BoyEmojiGrapheme; // looks like family man+woman+boy emoji

    // \uD83D\uDCBC briefcase. One rune, one grapheme. The profession-tag emoji
    // for "office worker." Paired with WomanEmoji via ZWJ to form
    // woman-office-worker.
    public const int BriefcaseRune = 0x1F4BC;
    public const string BriefcaseGrapheme = "\uD83D\uDCBC"; // looks like briefcase emoji

    // Profession ZWJ sequence: base human emoji + ZWJ + profession-tag
    // emoji. Three runes (woman, ZWJ, briefcase), 5 UTF-16 chars.
    // Different shape from the family sequence (one ZWJ link, not
    // two) and from woman-shrugging (no variation selector). Lock-in
    // case for the base-emoji-joins-profession-tag pattern that
    // Unicode 13+ uses for the gendered profession emoji set.
    public const string WomanOfficeWorkerGrapheme =
        WomanEmojiGrapheme + ZeroWidthJoinerText + BriefcaseGrapheme; // looks like woman office worker

    // U+20E3 COMBINING ENCLOSING KEYCAP. Combining mark that wraps a
    // square box around the preceding base. UAX #29 GCB=Extend.
    public const string CombiningEnclosingKeycapText = "\u20E3"; // looks like a keycap box wrapping the preceding base

    // 1\uFE0F\u20E3 keycap digit one. Three runes (DIGIT ONE + emoji variation
    // selector + COMBINING ENCLOSING KEYCAP), one grapheme. The
    // canonical keycap sequence: a base character followed by VS16 to
    // select emoji presentation and the combining enclosing keycap to
    // wrap it. UAX #29 GB9 / GB9a keep the Extend characters glued to
    // the base. Distinct from the family ZWJ sequence (which uses
    // emoji + ZWJ + emoji links) so the test covers the
    // base + variation-selector + combining-mark shape.
    public const string DigitOneKeycapGrapheme =
        "1" + EmojiVariationSelectorText + CombiningEnclosingKeycapText; // looks like 1\uFE0F\u20E3 keycap digit one

    // U+00A0 NO-BREAK SPACE. Renders as a space, but classified by
    // .NET as Unicode category Zs (Space_Separator), same as ordinary
    // space. UAX #29 treats it as GCB=Other so it breaks on both
    // sides; it surfaces as its own one-character token between
    // adjacent letters. Distinct from U+0020 SPACE because it isn't a
    // word-wrap opportunity. Common in copy-pasted text from word
    // processors and Wikipedia.
    public const string NoBreakSpaceText = "\u00A0"; // looks like a space (no-break, doesn't word-wrap)

    // 🇺 REGIONAL INDICATOR SYMBOL LETTER U. One half of a flag emoji
    // pair (two regional indicators in a row form a country flag). UAX #29 pairs
    // two of these into one cluster; a single one stands alone.
    public const int RegionalIndicatorURune = 0x1F1FA;
    public const string RegionalIndicatorUText = "\uD83C\uDDFA"; // looks like regional-indicator letter U (one half of a flag pair)

    // U+10FFFF maximum Unicode scalar value. Encoded as the surrogate pair
    // (U+DBFF, U+DFFF) in UTF-16. Boundary case for rune decoding.
    public const int MaximumCodePointRune = 0x10FFFF;
    public const string MaximumCodePointGrapheme = "\uDBFF\uDFFF"; // looks like (font-dependent \u2014 U+10FFFF, max Unicode scalar)

    // === Lone surrogate halves (not valid scalars on their own) ===
    // UTF-16 encodes supplementary-plane scalars as a HIGH-then-LOW
    // surrogate pair. A surrogate code unit appearing without its
    // partner isn't a valid Unicode scalar value, but .NET strings
    // can still hold one. Useful for the malformed-input tests.

    // U+D800: lowest high-surrogate code unit. "Min" of the high range.
    public const string HighSurrogateMinText = "\uD800"; // looks like (no rendering — lone high surrogate, invalid scalar)

    // U+DC00: lowest low-surrogate code unit. "Min" of the low range.
    public const string LowSurrogateMinText = "\uDC00"; // looks like (no rendering — lone low surrogate, invalid scalar)

    // U+DFFF: highest low-surrogate code unit. "Max" of the low range.
    public const string LowSurrogateMaxText = "\uDFFF"; // looks like (no rendering — lone low surrogate, invalid scalar)

    // === Format / control characters that don't render as a glyph ===
    // The names exist so test source stays readable. Inline literals like
    // "‍" or "؀" appear as empty quotes in most editors; the named constants
    // make the intent obvious.

    // U+200D ZERO WIDTH JOINER. Glues emoji together into composed sequences
    // (the family / professions / variant emojis). UAX #29 GCB=ZWJ; semantically
    // attaches to surrounding bases.
    public const string ZeroWidthJoinerText = "\u200D"; // looks like (invisible \u2014 zero-width joiner)

    // U+200C ZERO WIDTH NON-JOINER. Suppresses default joining behavior in
    // scripts like Arabic and Indic. UAX #29 GCB=Extend.
    public const string ZeroWidthNonJoinerText = "\u200C"; // looks like (invisible \u2014 zero-width non-joiner)

    // U+200B ZERO WIDTH SPACE. Invisible word-break hint. UAX #29 GCB=Other
    // (breaks on both sides).
    public const string ZeroWidthSpaceText = "\u200B"; // looks like (invisible \u2014 zero-width space)

    // U+FE0F EMOJI VARIATION SELECTOR. Selects emoji-style presentation for
    // the preceding base. UAX #29 GCB=Extend.
    public const string EmojiVariationSelectorText = "\uFE0F"; // looks like (invisible \u2014 emoji variation selector)

    // U+FEFF BYTE ORDER MARK / ZERO WIDTH NO-BREAK SPACE. Marks UTF byte
    // order at the start of streams; left over from older encodings.
    public const string ByteOrderMarkText = "\uFEFF"; // looks like (invisible \u2014 byte order mark / ZWNBSP)

    // U+00AD SOFT HYPHEN. Hyphenation hint. UAX #29 breaks on both sides so
    // it surfaces as its own token between surrounding letters.
    public const string SoftHyphenText = "\u00AD"; // looks like (invisible by default \u2014 soft hyphen, hyphenation hint)

    // U+202E RIGHT-TO-LEFT OVERRIDE. Bidi-direction control; the same
    // character used in the Trojan Source attack to make source code render
    // differently from its logical order.
    public const string RightToLeftOverrideText = "\u202E"; // looks like (invisible \u2014 right-to-left bidi override)

    // U+0600 ARABIC NUMBER SIGN. UAX #29 Prepend category, normally attaches
    // to the FOLLOWING character.
    public const string ArabicNumberSignText = "\u0600"; // looks like \u0600 (Arabic number sign, UAX #29 Prepend)

    // U+094D DEVANAGARI SIGN VIRAMA. Indic linker that joins consonants into
    // conjuncts. UAX #29 GCB=Extend.
    public const string DevanagariViramaText = "\u094D"; // looks like \u094D (Devanagari virama, conjunct linker)

    // U+E0001 LANGUAGE TAG. Used inside emoji tag sequences for subdivision
    // flags. UAX #29 GCB=Extend.
    public const int LanguageTagRune = 0xE0001;
    public const string LanguageTagText = "\uDB40\uDC01"; // looks like (invisible \u2014 language tag, used in emoji subdivision flags)

    // U+180E MONGOLIAN VOWEL SEPARATOR. Property has shifted across Unicode
    // versions (Cf, then Whitespace, now Cf again depending on the runtime's
    // data).
    public const string MongolianVowelSeparatorText = "\u180E"; // looks like (invisible \u2014 Mongolian vowel separator, property has shifted across Unicode versions)

    // === Noncharacters, Private Use, Replacement ===

    // U+FFFE / U+FFFF: BMP noncharacters. .NET's string.Normalize rejects
    // U+FFFE specifically as "invalid Unicode code points" but accepts
    // U+FFFF (.NET-specific asymmetry).
    public const string NoncharacterFFFEText = "\uFFFE"; // looks like (no rendering \u2014 BMP noncharacter U+FFFE, rejected by .NET Normalize)
    public const string NoncharacterFFFFText = "\uFFFF"; // looks like (no rendering \u2014 BMP noncharacter U+FFFF, accepted by .NET Normalize)

    // U+FDD0 noncharacter. First code point in the U+FDD0..U+FDEF
    // noncharacter block. .NET's Normalize accepts these.
    public const string NoncharacterFDD0Text = "\uFDD0"; // looks like (no rendering \u2014 noncharacter U+FDD0)

    // U+E000 start of the BMP Private Use Area. Valid scalar with no
    // assigned character; no Unicode category data.
    public const string PrivateUseAreaStartText = "\uE000"; // looks like (font-dependent \u2014 start of BMP Private Use Area)

    // U+FFFD REPLACEMENT CHARACTER. What permissive decoders write when they
    // hit invalid bytes. By the time it reaches the parser it's a perfectly
    // valid scalar.
    public const string ReplacementCharacterText = "\uFFFD"; // looks like \uFFFD (replacement character \u2014 what permissive decoders write for invalid bytes)

    // U+0000 NULL. Cc control. Valid scalar. Most editors render as a box
    // or as zero-width, depending on font.
    public const string NullText = "\u0000";

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
    // different classes" — same-class marks (e.g. acute+circumflex,
    // both ccc=230) are NOT reordered, so input order matters there.
    public const int VietnameseACircumflexDotBelowRune = 0x1EAD;
    public const string VietnameseACircumflexDotBelowGrapheme = "\u1EAD"; // looks like \u1EAD (precomposed, U+1EAD)

    // "a + dot-below + circumflex" — already in canonical order
    // (ccc 220 then 230). NFC composes directly to U+1EAD.
    public const string VietnameseACircumflexDotBelowCanonicalText = "a\u0323\u0302"; // looks like \u1ead (a + dot-below + circumflex, canonical mark order)

    // "a + circumflex + dot-below" — non-canonical order
    // (ccc 230 then 220). NFC reorders to canonical (220 then 230),
    // then composes to U+1EAD. Both strings normalize to the same
    // precomposed character.
    public const string VietnameseACircumflexDotBelowReorderedText = "a\u0302\u0323";

    // U+D55C HANGUL SYLLABLE HAN, precomposed. The poster child for
    // canonical-equivalence in CJK.
    public const string HangulHanGrapheme = "\uD55C";

    // Decomposed Hangul "han": U+1112 (HIEUH) + U+1161 (A) + U+11AB (NIEUN).
    // NFC composes the three jamo back to U+D55C.
    public const string HangulHanDecomposedText = "\u1112\u1161\u11AB";

    // U+212B ANGSTROM SIGN. Canonical singleton; NFC converts to U+00C5.
    // Renders identically to U+00C5 LATIN CAPITAL LETTER A WITH RING ABOVE.
    public const string AngstromGrapheme = "\u212B";

    // U+00C5 LATIN CAPITAL LETTER A WITH RING ABOVE. The canonical
    // replacement that NFC produces from the Angstrom singleton.
    public const string LatinCapitalAWithRingAboveGrapheme = "\u00C5";

    // "A + combining ring above" — the FormD (NFD) decomposition of
    // both U+00C5 and U+212B. Used as a literal under Compile(FormD).
    public const string LatinAWithRingAboveDecomposedText = "A\u030A";

    // U+2126 OHM SIGN. Canonical singleton; NFC converts to U+03A9.
    // Renders identically to U+03A9 GREEK CAPITAL LETTER OMEGA.
    public const string OhmGrapheme = "\u2126";

    // U+03A9 GREEK CAPITAL LETTER OMEGA. The canonical replacement
    // that NFC produces from the Ohm singleton.
    public const string GreekCapitalOmegaGrapheme = "\u03A9";

    // U+212A KELVIN SIGN. Canonical singleton; NFC converts to U+004B.
    // Renders identically to ASCII 'K'. Most dangerous of the singletons
    // in source: a literal "K" might be either the Kelvin sign or the
    // ASCII K and a reader can't tell.
    public const string KelvinGrapheme = "\u212A";

    // U+004B ASCII 'K'. The canonical replacement that NFC produces
    // from the Kelvin singleton. Constant exists so a test reader
    // can immediately tell which "K" is meant: ASCII or Kelvin.
    public const string AsciiCapitalKGrapheme = "K";

    // U+2329 LEFT-POINTING ANGLE BRACKET. Canonical singleton; NFC
    // converts to U+3008 (CJK angle bracket). Renders similarly.
    public const string LeftPointingAngleBracketGrapheme = "\u2329";

    // U+3008 LEFT ANGLE BRACKET (CJK). The canonical replacement
    // that NFC produces from U+2329.
    public const string CjkLeftAngleBracketGrapheme = "\u3008";

    // U+2102 DOUBLE-STRUCK CAPITAL C. Compatibility singleton: NFKC
    // converts to ASCII 'C', NFC leaves it alone.
    public const string DoubleStruckCGrapheme = "\u2102";

    // U+0043 ASCII 'C'. The compatibility-form replacement that NFKC
    // produces from the double-struck C singleton.
    public const string AsciiCapitalCGrapheme = "C";

    // === Identifier-relevant edge-case characters ===

    // U+0430 CYRILLIC SMALL LETTER A. Renders identically to Latin 'a'
    // (U+0061) in most fonts. The homoglyph star.
    public const string CyrillicSmallAGrapheme = "\u0430";
}
