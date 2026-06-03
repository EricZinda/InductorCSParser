using System.Globalization;
using System.Linq;
using System.Text;
using NUnit.Framework;
using static InductorParser.Rules;

namespace InductorParser.Tests.DocExamples;

// Verifies the runnable code examples in docs/Primer4.md (the
// "Security-Related Concerns" walkthrough: pathological input, the
// FormKC compatibility-lookalike blocker, ASCII script restriction,
// and the invisible-character defenses). Each test mirrors a code block
// from the doc and asserts what the doc claims.
[TestFixture]
public class Primer4Examples
{
    // Primer4.md "Pathological Input": the linear-time email-ish
    // validator. Accepts a well-formed address and rejects the
    // ReDoS-style adversarial input cleanly (no catastrophic
    // backtracking).
    [Test]
    public void ReDoS_validator_accepts_valid_and_rejects_pathological()
    {
        var validator = And(
            OneOrMore(OneOf(TokenSet.Ascii.Letters | TokenSet.Ascii.Digits)),
            Literal("@example.com"),
            Eof());

        Assert.That(validator.Parse("user42@example.com").Success, Is.True);
        // "aaaa...!" is the classic ReDoS trigger for the equivalent
        // regex. The parser consumes the run in one greedy pass and
        // fails cleanly.
        Assert.That(validator.Parse(new string('a', 40) + "!").Success, Is.False);
    }

    // Primer4.md: even the textbook (a+)+ ReDoS shape is linear here.
    [Test]
    public void ReDoS_textbook_nested_quantifiers_terminate()
    {
        var pattern = And(OneOrMore(OneOrMore(Token('a'))), Eof());

        Assert.That(pattern.Parse("aaaa").Success, Is.True);
        Assert.That(pattern.Parse("aaaa!").Success, Is.False);
    }

    // Primer4.md "Unicode Compatibility Lookalikes": a FormKC-compiled
    // blocker treats math-bold and fullwidth variants as plain "admin".
    [Test]
    public void FormKC_blocker_catches_compatibility_lookalikes()
    {
        var blocker = And(Literal("admin"), Eof()).Compile(NormalizationForm.FormKC);

        Assert.That(blocker.Parse("admin").Success, Is.True);
        // U+1D41A MATHEMATICAL BOLD SMALL A + "dmin"
        Assert.That(blocker.Parse("\U0001D41Admin").Success, Is.True);
        // Fullwidth "admin"
        Assert.That(blocker.Parse("ａｄｍｉｎ").Success, Is.True);
    }

    // Primer4.md "Homoglyphs": restricting to ASCII letters rejects
    // Cyrillic, math-bold, and fullwidth lookalikes under default FormC,
    // with no FormKC needed.
    [Test]
    public void Ascii_script_restriction_rejects_homoglyphs_and_compatibility_lookalikes()
    {
        var username = And(OneOrMore(OneOf(TokenSet.Ascii.Letters)), Eof()).Compile();

        Assert.That(username.Parse("admin").Success, Is.True);
        // Cyrillic 'а' (U+0430)
        Assert.That(username.Parse("аdmin").Success, Is.False);
        // Math-bold 'a' (U+1D41A)
        Assert.That(username.Parse("\U0001D41Admin").Success, Is.False);
        // Fullwidth "admin"
        Assert.That(username.Parse("ａｄｍｉｎ").Success, Is.False);
    }

    // Primer4.md "Invisible characters": the StripInvisibles helper,
    // verbatim from the doc. UnicodeCategory.Format is the invisible set.
    private static readonly TokenSet Invisibles = TokenSet.Category(UnicodeCategory.Format);

    private static string StripInvisibles(string input) =>
        string.Concat(input.EnumerateRunes()
            .Where(r => !Invisibles.ContainsRune(r)));

    [Test]
    public void StripInvisibles_removes_zero_width_characters_so_the_blocker_catches_the_bypass()
    {
        var blockedWords = And(Literal("kill"), Eof()).Compile();

        // "ki<ZWS>ll" slips past the blocker untouched...
        string smuggled = "ki​ll";
        Assert.That(blockedWords.Parse(smuggled).Success, Is.False);

        // ...but stripping invisibles first restores "kill" and the
        // blocker catches it.
        Assert.That(StripInvisibles(smuggled), Is.EqualTo("kill"));
        Assert.That(blockedWords.Parse(StripInvisibles(smuggled)).Success, Is.True);
    }

    // Primer4.md: the "allowed"-rule username that refuses any invisible
    // in the input. NoneOf(Invisibles) is the per-character check, so
    // OneOrMore(NoneOf(Invisibles)) accepts a clean name and rejects one
    // carrying a zero-width space.
    [Test]
    public void SafeUsername_accepts_clean_name_and_rejects_invisible_laden_one()
    {
        var safeUsername = And(
            OneOrMore(NoneOf(Invisibles)),
            Eof()
        ).Compile();

        Assert.That(safeUsername.Parse("admin").Success, Is.True);
        // "ad<ZWS>min": the ZWS fails NoneOf(Invisibles).
        Assert.That(safeUsername.Parse("ad​min").Success, Is.False);
    }

    // Primer4.md: subtract ZWJ from the invisible set so emoji families
    // survive the filter while every other Format character is still
    // caught.
    [Test]
    public void Invisibles_minus_ZWJ_keeps_ZWJ_out_of_the_set()
    {
        var invisiblesKeepingZwj =
            TokenSet.Category(UnicodeCategory.Format) & ~TokenSet.Single(0x200D);

        Assert.That(invisiblesKeepingZwj.ContainsRune(0x200D), Is.False,
            "ZWJ (U+200D) is subtracted so emoji families survive the filter");
        Assert.That(invisiblesKeepingZwj.ContainsRune(0x200B), Is.True,
            "zero-width space (U+200B) is still in the invisible set");
    }
}
