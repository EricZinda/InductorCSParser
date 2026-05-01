using System.Text;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Tests for Rules.Identifier() and the underlying RuneSet.XidStart /
// RuneSet.XidContinue tables. Two things under test:
//
//   1. UAX #31 R1 shape: "XID_Start XID_Continue*". Start-only characters
//      can't appear in Continue position and vice-versa.
//   2. The hand-curated add/exclude tables in RuneSet.Xid.cs are actually
//      wired in. Two regression tests (U+FDFA reject, U+2118 accept) verify
//      that. If someone swaps the implementation to a General_Category-only
//      approximation, both tests fail.
[TestFixture]
public class XidIdentifierTests
{
    [Test]
    public void Ascii_identifier_matches()
    {
        var result = Identifier().Parse("foo_bar2");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("foo_bar2"));
    }

    [Test]
    public void Leading_digit_fails_at_position_zero()
    {
        var result = Identifier().Parse("2foo");
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
    }

    [Test]
    public void Leading_underscore_fails_under_strict_default()
    {
        // Pc is in XID_Continue but not XID_Start. Strict UAX #31 rejects.
        var result = Identifier().Parse("_foo");
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
    }

    [Test]
    public void Leading_underscore_matches_with_extra_start_rune()
    {
        // The programming-language profile that C# / Python / Rust use:
        // union "_" into Start via extraStartRunes.
        var result = Identifier(extraStartRunes: RuneSet.Runes("_")).Parse("_foo");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("_foo"));
    }

    [Test]
    public void Extra_body_rune_lets_dollar_sign_appear_inside_identifier()
    {
        // ECMAScript-style: "$" is allowed in both positions. This test
        // exercises the body-side parameter, proving $ is accepted after
        // the first char even though Pc-less XID_Continue would reject it.
        var rule = Identifier(
            extraStartRunes: RuneSet.Runes("_$"),
            extraBodyRunes: RuneSet.Runes("$"));
        var result = rule.Parse("foo$bar");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("foo$bar"));
    }

    [Test]
    public void Greek_identifier_matches_whole_word()
    {
        // All runes are Ll (Letter, Lowercase), so pure XID_Start territory
        // after the first character. One precomposed "έ" (U+03AD) in the middle.
        var result = Identifier().Parse("καλημέρα");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("καλημέρα"));
    }

    [Test]
    public void Devanagari_identifier_with_combining_marks_matches_under_default_grapheme_lexer()
    {
        // "हिन्दी" is six runes: ह (Lo), ि (Mc), न (Lo), ् (Mn), द (Lo), ी (Mc).
        // Under the grapheme lexer these compose into three multi-rune
        // graphemes. Identifier uses WithinGrapheme internally, which
        // walks each grapheme's runes and checks them against the
        // identifier rules, so the whole word matches.
        var result = Identifier().Parse("हिन्दी");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("हिन्दी"));
    }

    [Test]
    public void Thai_identifier_with_sara_am_matches_under_default_grapheme_lexer()
    {
        // "กำ" is ก (Lo) + ำ (Mc SARA AM), which the grapheme lexer
        // bundles into a single two-rune grapheme. Identifier accepts
        // ก as Start and ำ as Continue inside the same grapheme.
        var result = Identifier().Parse("กำ");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("กำ"));
    }

    [Test]
    public void Nfc_precomposed_and_decomposed_cafe_flatten_to_same_string()
    {
        // R4: NFC equivalence is a free consequence of ParseOptions
        // defaulting to NormalizationForm.FormC. Two inputs that differ
        // only by NFC decomposition produce the same match text.
        var precomposed = Identifier().Parse("café");        // café
        var decomposed = Identifier().Parse("café");         // cafe + combining acute

        Assert.That(precomposed.Success, Is.True, precomposed.ErrorMessage);
        Assert.That(decomposed.Success, Is.True, decomposed.ErrorMessage);
        Assert.That(precomposed.Tree!.ToString(), Is.EqualTo(decomposed.Tree!.ToString()));
    }

    [Test]
    public void Nfkc_collapses_fullwidth_latin_to_plain_ascii()
    {
        // The XML doc on Identifier promises that FormKC makes fullwidth
        // "ｆｏｏ" (U+FF46, U+FF4F, U+FF4F) match plain ASCII "foo". Verifies
        // that promise: under NFKC the fullwidth letters decompose to
        // ASCII before the lexer runs, so both inputs produce the same
        // flattened match text.
        var options = new ParseOptions { NormalizeInput = System.Text.NormalizationForm.FormKC };
        var fullwidth = Identifier().Parse("ｆｏｏ", options);
        var plain = Identifier().Parse("foo", options);

        Assert.That(fullwidth.Success, Is.True, fullwidth.ErrorMessage);
        Assert.That(plain.Success, Is.True, plain.ErrorMessage);
        Assert.That(fullwidth.Tree!.ToString(), Is.EqualTo("foo"));
        Assert.That(fullwidth.Tree!.ToString(), Is.EqualTo(plain.Tree!.ToString()));
    }

    [Test]
    public void Nfkc_collapses_latin_ff_ligature_to_two_letters()
    {
        // U+FB00 LATIN SMALL LIGATURE FF. NFKC decomposes it to "ff".
        // Same promise: the ligatured and un-ligatured inputs match the
        // same identifier under FormKC.
        var options = new ParseOptions { NormalizeInput = System.Text.NormalizationForm.FormKC };
        var ligature = Identifier().Parse("ﬀoo", options);
        var plain = Identifier().Parse("ffoo", options);

        Assert.That(ligature.Success, Is.True, ligature.ErrorMessage);
        Assert.That(plain.Success, Is.True, plain.ErrorMessage);
        Assert.That(ligature.Tree!.ToString(), Is.EqualTo("ffoo"));
        Assert.That(ligature.Tree!.ToString(), Is.EqualTo(plain.Tree!.ToString()));
    }

    [Test]
    public void Nfkc_collapses_mathematical_bold_to_ascii()
    {
        // Mathematical Bold letters (U+1D400..U+1D433 for bold A..z, etc.)
        // are supplementary-plane code points NFKC-equivalent to plain
        // ASCII. This is the case that catches "𝐟𝐨𝐨" vs "foo" spoofing.
        var options = new ParseOptions { NormalizeInput = System.Text.NormalizationForm.FormKC };
        var mathBold = Identifier().Parse("𝐟𝐨𝐨", options);
        var plain = Identifier().Parse("foo", options);

        Assert.That(mathBold.Success, Is.True, mathBold.ErrorMessage);
        Assert.That(plain.Success, Is.True, plain.ErrorMessage);
        Assert.That(mathBold.Tree!.ToString(), Is.EqualTo("foo"));
        Assert.That(mathBold.Tree!.ToString(), Is.EqualTo(plain.Tree!.ToString()));
    }

    [Test]
    public void Nfc_default_does_not_collapse_fullwidth_latin()
    {
        // Counter-test: under the default NFC (not NFKC), fullwidth
        // "ｆｏｏ" stays fullwidth and produces a different match text
        // than plain "foo", even though both parse successfully. This
        // verifies that the NFKC collapses above are coming from the
        // normalization form choice, not the rule.
        var fullwidth = Identifier().Parse("ｆｏｏ");
        var plain = Identifier().Parse("foo");

        Assert.That(fullwidth.Success, Is.True, fullwidth.ErrorMessage);
        Assert.That(plain.Success, Is.True, plain.ErrorMessage);
        Assert.That(fullwidth.Tree!.ToString(), Is.Not.EqualTo(plain.Tree!.ToString()));
    }

    [Test]
    public void Nfc_disabled_stops_the_equivalence()
    {
        // With normalization turned off, the decomposed form still matches
        // (WithinGrapheme walks the grapheme's runes and Mn is in
        // XID_Continue) but the match text differs between the two
        // inputs. Proves the equivalence in the previous test comes from
        // normalization, not the rule.
        var options = new ParseOptions
        {
            NormalizeInput = null,
        };
        var precomposed = Identifier().Parse("café", options);
        var decomposed = Identifier().Parse("café", options);

        Assert.That(precomposed.Success, Is.True, precomposed.ErrorMessage);
        Assert.That(decomposed.Success, Is.True, decomposed.ErrorMessage);
        Assert.That(precomposed.Tree!.ToString(), Is.Not.EqualTo(decomposed.Tree!.ToString()));
    }

    [Test]
    public void Trailing_digits_and_letters_are_allowed()
    {
        Assert.That(Identifier().Parse("abc").Success, Is.True);
        Assert.That(Identifier().Parse("abc123").Success, Is.True);
        Assert.That(Identifier().Parse("a1b2c3").Success, Is.True);
    }

    [Test]
    public void Number_before_letters_fails_at_start()
    {
        var result = Identifier().Parse("123abc");
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
    }

    [Test]
    public void Emoji_is_not_a_continue_character()
    {
        // 😀 (U+1F600) is So (Symbol, Other). Not in XID_Continue.
        // Identifier matches "a", then Rule.Parse sees more input and fails
        // the overall parse. ErrorCharIndex points at the first unconsumed
        // char, which is the start of the emoji.
        var result = Identifier().Parse("a😀b");
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
    }

    [Test]
    public void Arabic_ligature_FDFA_is_excluded_from_start()
    {
        // U+FDFA is ARABIC LIGATURE SALLALLAHOU ALAYHE WASALLAM. It's Lo by
        // General_Category so a L-only check would admit it, but UAX #31
        // excludes it from XID_Start because its NFKC decomposition is a
        // full multi-word phrase with embedded spaces.
        //
        // This is the regression test for XidStartExclusions. If someone
        // drops the exclusion table and uses a General_Category
        // approximation, this test flips to success.
        var result = Identifier().Parse("ﷺ");
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
    }

    [Test]
    public void Script_capital_P_2118_is_added_to_start()
    {
        // U+2118 (SCRIPT CAPITAL P, ℘) is Sm by General_Category, so a
        // L-only check would reject it. UAX #31 adds it to XID_Start via
        // Other_ID_Start. This is the regression test for XidStartAdds: an
        // approximation that dropped the adds table would fail this.
        var result = Identifier().Parse("℘");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("℘"));
    }
}
