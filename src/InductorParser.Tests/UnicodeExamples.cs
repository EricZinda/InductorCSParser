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
    public const string WavingHandGrapheme = "\uD83D\uDC4B";

    // 🏽 medium skin tone modifier. Combining modifier rune that attaches to a
    // base character to form a multi-rune grapheme. Not typically rendered
    // standalone.
    public const int MediumSkinToneRune = 0x1F3FD;
    public const string MediumSkinToneText = "\uD83C\uDFFD";

    // 👋🏽 waving hand + medium skin tone. ONE grapheme made of TWO runes
    // (4 UTF-16 chars total). Under the grapheme lexer this is a single
    // token. Under the rune lexer it's two tokens.
    public const string SkinTonedWaveGrapheme = WavingHandGrapheme + MediumSkinToneText;

    // 🎸 guitar. One rune, one grapheme.
    public const int GuitarRune = 0x1F3B8;
    public const string GuitarGrapheme = "\uD83C\uDFB8";

    // 🎹 musical keyboard. One rune, one grapheme. Used alongside Guitar
    // as a "different emoji, same shape" mismatch example.
    public const int MusicalKeyboardRune = 0x1F3B9;
    public const string MusicalKeyboardGrapheme = "\uD83C\uDFB9";

    // U+0301 combining acute. Combining mark that attaches to a base character
    // to form a multi-rune grapheme. Not a standalone grapheme.
    public const string CombiningAcuteText = "\u0301";

    // é as e + combining acute. The textbook multi-rune Token: TWO runes,
    // ONE grapheme (2 UTF-16 chars total). Works on every runtime including
    // legacy StringInfo because the base+combining-mark rule predates UAX #29.
    // Use this when a test needs a multi-rune grapheme that segments the same
    // way on .NET 5+ and on Unity Mono / IL2CPP.
    public const string LatinEAcuteGrapheme = "e" + CombiningAcuteText;

    // 🤷‍♀️ woman shrugging. ZWJ emoji sequence: base shrug rune + ZWJ +
    // female sign + emoji variation selector. UAX #29 sees ONE grapheme.
    // Legacy StringInfo splits it. Use this to test the ZWJ rule.
    public const string WomanShruggingGrapheme = "\uD83E\uDD37\u200D\u2640\uFE0F";

    // 🇺🇸 US flag. Two regional indicator code points (U + S). UAX #29 sees
    // ONE grapheme. Legacy StringInfo splits it. Use this to test the
    // regional-indicator pairing rule.
    public const string USFlagGrapheme = "\uD83C\uDDFA\uD83C\uDDF8";

    // ก + ํา Thai "kam". SARA AM is the canonical extended-grapheme-cluster
    // case (a vowel sign that visually composes with the preceding consonant).
    // UAX #29 sees ONE grapheme. Legacy StringInfo splits it. Use this to
    // test the SARA AM rule.
    public const string ThaiKamGrapheme = "\u0E01\u0E33";
}
