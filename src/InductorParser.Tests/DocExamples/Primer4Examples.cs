using System.Globalization;
using System.Linq;
using System.Text;
using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;

namespace InductorParser.Tests.DocExamples;

// Verifies the runnable code examples in docs/Primer4.md (the
// "Security-Related Concerns" walkthrough). Primer4 had no backing
// example test, which is exactly the ozpb gap that let two of its
// snippets drift:
//   * The "Invisible characters" StripInvisibles helper called
//     Invisibles.Contains(r), but TokenSet has no Contains method
//     (the rune-membership test is ContainsRune(Rune)), so the
//     documented helper didn't compile.
//   * The "safe username" grammar wrapped NoneOf(Invisibles) in
//     And(..., AnyToken()). NoneOf already consumes one token, so that
//     pairing ate two tokens per iteration and failed on odd-length
//     input like "admin" (which the doc claims succeeds).
// Each test mirrors a doc snippet and asserts the documented behavior.
// Non-ASCII test data is built from hex code points (no raw glyphs in
// source) so the UnicodeLiteralCanary scanner stays happy.
[TestFixture]
public class Primer4Examples
{
    // Fullwidth "admin": U+FF41 U+FF44 U+FF4D U+FF49 U+FF4E.
    private static readonly string FullwidthAdmin =
        new string(new[] { (char)0xFF41, (char)0xFF44, (char)0xFF4D, (char)0xFF49, (char)0xFF4E });
    // Math-bold small a (U+1D41A) followed by plain "dmin".
    private static readonly string MathBoldAdmin = char.ConvertFromUtf32(0x1D41A) + "dmin";
    // Cyrillic small a (U+0430) followed by plain "dmin".
    private static readonly string CyrillicAdmin = ((char)0x0430) + "dmin";
    // Zero-width space (U+200B).
    private static readonly string ZeroWidthSpace = ((char)0x200B).ToString();

    // "Pathological Input": the ReDoS-shaped grammars run in linear
    // time and reject / accept cleanly.
    [Test]
    public void Pathological_input_validators_match_and_reject_cleanly()
    {
        var validator = And(
            OneOrMore(OneOf(TokenSet.Ascii.Letters | TokenSet.Ascii.Digits)),
            Literal("@example.com"),
            Eof()
        ).Compile();
        Assert.That(validator.Parse("abc123@example.com").Success, Is.True);
        Assert.That(validator.Parse("aaaaaaaaaaaaaaaaaaaaa!").Success, Is.False);

        var pattern = And(OneOrMore(OneOrMore(Token('a'))), Eof()).Compile();
        Assert.That(pattern.Parse("aaaa").Success, Is.True);
        Assert.That(pattern.Parse("aaaab").Success, Is.False);
    }

    // "Unicode Compatibility Lookalikes": a FormKC-compiled blocker
    // converts math-bold and fullwidth letters to plain ASCII before
    // the rule runs, so all three spellings of "admin" match.
    [Test]
    public void Compatibility_lookalike_blocker_under_FormKC()
    {
        var blocker = And(Literal("admin"), Eof()).Compile(NormalizationForm.FormKC);

        Assert.That(blocker.Parse("admin").Success, Is.True);
        Assert.That(blocker.Parse(MathBoldAdmin).Success, Is.True);
        Assert.That(blocker.Parse(FullwidthAdmin).Success, Is.True);
    }

    // "Homoglyphs": an ASCII-only username rule rejects Cyrillic,
    // math-bold, and fullwidth lookalikes under the default FormC.
    [Test]
    public void Ascii_only_username_rejects_homoglyphs_and_compat_lookalikes()
    {
        var username = And(OneOrMore(OneOf(TokenSet.Ascii.Letters)), Eof()).Compile();

        Assert.That(username.Parse("admin").Success, Is.True);
        Assert.That(username.Parse(CyrillicAdmin).Success, Is.False);
        Assert.That(username.Parse(MathBoldAdmin).Success, Is.False);
        Assert.That(username.Parse(FullwidthAdmin).Success, Is.False);
    }

    // "Invisible characters": the documented StripInvisibles helper.
    // This is the snippet that drifted: it must use ContainsRune(Rune),
    // not the nonexistent Contains(Rune).
    private static readonly TokenSet Invisibles = TokenSet.Category(UnicodeCategory.Format);

    private static string StripInvisibles(string input) =>
        string.Concat(input.EnumerateRunes()
            .Where(r => !Invisibles.ContainsRune(r)));

    [Test]
    public void StripInvisibles_removes_format_characters_before_parsing()
    {
        // "ki<ZWS>ll" becomes "kill" before the rule sees it.
        string smuggled = "ki" + ZeroWidthSpace + "ll";
        Assert.That(StripInvisibles(smuggled), Is.EqualTo("kill"));

        var blockedWords = And(Literal("kill"), Eof()).Compile();
        Assert.That(blockedWords.Parse(StripInvisibles(smuggled)).Success, Is.True);
    }

    // "Invisible characters": an allowed-rule username that refuses any
    // invisible in the input via NoneOf(Invisibles). NoneOf already
    // consumes one non-invisible token per match, so it stands alone
    // inside OneOrMore (not paired with AnyToken()).
    [Test]
    public void Safe_username_refuses_any_invisible_character()
    {
        var safeUsername = And(
            OneOrMore(NoneOf(Invisibles)),
            Eof()
        ).Compile();

        Assert.That(safeUsername.Parse("admin").Success, Is.True);
        Assert.That(safeUsername.Parse("ad" + ZeroWidthSpace + "min").Success, Is.False);
    }

    // "Invisible characters": keeping emoji whole by subtracting ZWJ
    // from the Format category with & and ~.
    [Test]
    public void Invisibles_minus_zwj_keeps_zwj_out_of_the_set()
    {
        var invisiblesKeepingEmoji =
            TokenSet.Category(UnicodeCategory.Format) & ~TokenSet.Single(0x200D);

        Assert.That(invisiblesKeepingEmoji.ContainsRune(0x200B), Is.True,  // ZWS still removed
            "zero-width space stays in the invisible set");
        Assert.That(invisiblesKeepingEmoji.ContainsRune(0x200D), Is.False, // ZWJ kept
            "ZWJ is subtracted so emoji families survive the filter");
    }
}
