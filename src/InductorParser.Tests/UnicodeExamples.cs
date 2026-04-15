namespace InductorParser.Tests;

// Named Unicode constants used across test fixtures. Raw hex escapes and
// surrogate-pair string literals don't tell you what they mean on sight;
// names do. Naming convention:
//
//   XxxRune      — int codepoint (U+xxxxx).
//   XxxGrapheme  — UTF-16 string that tokenizes as exactly one grapheme.
//   XxxText      — UTF-16 string that isn't necessarily a standalone
//                  grapheme (e.g. a combining modifier).
//
// When adding a new symbol, state its rune-vs-grapheme status in the comment.
internal static class UnicodeExamples
{
    // 👋 waving hand. Supplementary-plane rune (needs a surrogate pair in
    // UTF-16). One rune, one grapheme on its own.
    public const int WavingHandRune = 0x1F44B;
    public const string WavingHandGrapheme = "\uD83D\uDC4B";

    // 🏽 medium skin tone modifier. Combining modifier rune; attaches to a
    // base character to form a multi-rune grapheme. Not typically rendered
    // standalone.
    public const int MediumSkinToneRune = 0x1F3FD;
    public const string MediumSkinToneText = "\uD83C\uDFFD";

    // 👋🏽 waving hand + medium skin tone. ONE grapheme made of TWO runes
    // (4 UTF-16 chars total). Under the grapheme lexer this is a single
    // token; under the rune lexer it's two tokens.
    public const string SkinTonedWaveGrapheme = WavingHandGrapheme + MediumSkinToneText;

    // 🎸 guitar. One rune, one grapheme.
    public const int GuitarRune = 0x1F3B8;
    public const string GuitarGrapheme = "\uD83C\uDFB8";

    // 🎹 musical keyboard. One rune, one grapheme. Used alongside Guitar
    // as a "different emoji, same shape" mismatch example.
    public const int MusicalKeyboardRune = 0x1F3B9;
    public const string MusicalKeyboardGrapheme = "\uD83C\uDFB9";
}
