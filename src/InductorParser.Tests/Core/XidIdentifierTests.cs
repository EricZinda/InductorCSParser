using System.Text;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.CanaryHelper;

namespace InductorParser.Tests;

// Tests for Rules.Identifier() and the underlying TokenSet.XidStart /
// TokenSet.XidContinue tables. Two things under test:
//
//   1. UAX #31 R1 shape: "XID_Start XID_Continue*". Start-only characters
//      can't appear in Continue position and vice-versa.
//   2. The hand-curated add/exclude tables in TokenSet.Xid.cs are actually
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
        var result = Identifier(extraStartRunes: TokenSet.Runes("_")).Parse("_foo");
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
            extraStartRunes: TokenSet.Runes("_$"),
            extraBodyRunes: TokenSet.Runes("$"));
        var result = rule.Parse("foo$bar");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("foo$bar"));
    }

    [Test]
    public void Greek_identifier_matches_whole_word()
    {
        // All runes are Ll (Letter, Lowercase), so pure XID_Start territory
        // after the first character. One precomposed e-acute (U+03AD) in the middle.
        var word = UnicodeExamples.GreekKalimeraIdentifier;
        var result = Identifier().Parse(word);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(word));
    }

    [Test]
    public void Devanagari_identifier_with_combining_marks_matches()
    {
        // Devanagari 'hindi' is six runes: ha (Lo), i-vowel (Mc), na (Lo),
        // virama (Mn), da (Lo), ii-vowel (Mc).
        // The lexer composes these into three multi-rune graphemes.
        // Identifier uses WithinToken internally, which walks each
        // grapheme's runes and checks them against the identifier rules,
        // so the whole word matches.
        var word = UnicodeExamples.DevanagariHindiIdentifier;
        var result = Identifier().Parse(word);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(word));
    }

    [Test]
    public void Thai_identifier_with_sara_am_matches()
    {
        // Thai 'kam' is KO KAI (Lo) + SARA AM (Mc), which the lexer bundles
        // into a single two-rune grapheme. Identifier accepts KO KAI as
        // Start and SARA AM as Continue inside the same grapheme.
        var word = UnicodeExamples.ThaiKamGrapheme;
        var result = Identifier().Parse(word);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(word));
    }

    [Test]
    public void Nfc_precomposed_and_decomposed_cafe_flatten_to_same_string()
    {
        // R4: NFC equivalence is a free consequence of ParseOptions
        // defaulting to NormalizationForm.FormC. Two inputs that differ
        // only by NFC decomposition produce the same match text.
        var precomposedInput = UnicodeExamples.CafePrecomposedGrapheme;
        var decomposedInput = UnicodeExamples.CafeDecomposedText;
        var precomposed = Identifier().Parse(precomposedInput);
        var decomposed = Identifier().Parse(decomposedInput);

        Assert.That(precomposed.Success, Is.True, precomposed.ErrorMessage);
        Assert.That(decomposed.Success, Is.True, decomposed.ErrorMessage);
        Assert.That(precomposed.Tree!.ToString(), Is.EqualTo(decomposed.Tree!.ToString()));
    }

    [Test]
    public void Nfkc_collapses_fullwidth_latin_to_plain_ascii()
    {
        // The XML doc on Identifier promises that FormKC makes fullwidth
        // "foo" (U+FF46, U+FF4F, U+FF4F) match plain ASCII "foo". Verifies
        // that promise: under NFKC the fullwidth letters decompose to
        // ASCII before the lexer runs, so both inputs produce the same
        // flattened match text.
        var fullwidthInput = UnicodeExamples.FullwidthFooGrapheme;
        var fullwidthRule = Identifier(System.Text.NormalizationForm.FormKC);
        fullwidthRule.Compile(System.Text.NormalizationForm.FormKC);
        var plainRule = Identifier(System.Text.NormalizationForm.FormKC);
        plainRule.Compile(System.Text.NormalizationForm.FormKC);
        var fullwidth = fullwidthRule.Parse(fullwidthInput);
        var plain = plainRule.Parse("foo");

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
        var ligatureInput = UnicodeExamples.FfLigaturePlusOoText;
        var ligatureRule = Identifier(System.Text.NormalizationForm.FormKC);
        ligatureRule.Compile(System.Text.NormalizationForm.FormKC);
        var plainRule = Identifier(System.Text.NormalizationForm.FormKC);
        plainRule.Compile(System.Text.NormalizationForm.FormKC);
        var ligature = ligatureRule.Parse(ligatureInput);
        var plain = plainRule.Parse("ffoo");

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
        // ASCII. This is the case that catches math-bold 'foo' vs 'foo' spoofing.
        var mathBoldInput = UnicodeExamples.MathBoldFooIdentifier;
        var mathBoldRule = Identifier(System.Text.NormalizationForm.FormKC);
        mathBoldRule.Compile(System.Text.NormalizationForm.FormKC);
        var plainRule = Identifier(System.Text.NormalizationForm.FormKC);
        plainRule.Compile(System.Text.NormalizationForm.FormKC);
        var mathBold = mathBoldRule.Parse(mathBoldInput);
        var plain = plainRule.Parse("foo");

        Assert.That(mathBold.Success, Is.True, mathBold.ErrorMessage);
        Assert.That(plain.Success, Is.True, plain.ErrorMessage);
        Assert.That(mathBold.Tree!.ToString(), Is.EqualTo("foo"));
        Assert.That(mathBold.Tree!.ToString(), Is.EqualTo(plain.Tree!.ToString()));
    }

    [Test]
    public void Nfc_default_does_not_collapse_fullwidth_latin()
    {
        // Counter-test: under the default NFC (not NFKC), fullwidth
        // 'foo' stays fullwidth and produces a different match text
        // than plain "foo", even though both parse successfully. This
        // verifies that the NFKC collapses above are coming from the
        // normalization form choice, not the rule.
        var fullwidthInput = UnicodeExamples.FullwidthFooGrapheme;
        var fullwidth = Identifier().Parse(fullwidthInput);
        var plain = Identifier().Parse("foo");

        Assert.That(fullwidth.Success, Is.True, fullwidth.ErrorMessage);
        Assert.That(plain.Success, Is.True, plain.ErrorMessage);
        Assert.That(fullwidth.Tree!.ToString(), Is.Not.EqualTo(plain.Tree!.ToString()));
    }

    [Test]
    public void Nfc_disabled_stops_the_equivalence()
    {
        // With normalization turned off, the decomposed form still matches
        // (WithinToken walks the grapheme's runes and Mn is in
        // XID_Continue) but the match text differs between the two
        // inputs. Proves the equivalence in the previous test comes from
        // normalization, not the rule.
        var precomposedInput = UnicodeExamples.CafePrecomposedGrapheme;
        var decomposedInput = UnicodeExamples.CafeDecomposedText;
        var precomposedRule = Identifier();
        precomposedRule.Compile(null);
        var decomposedRule = Identifier();
        decomposedRule.Compile(null);
        var precomposed = precomposedRule.Parse(precomposedInput);
        var decomposed = decomposedRule.Parse(decomposedInput);

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
        // U+1F600 GRINNING FACE is So (Symbol, Other). Not in XID_Continue.
        // Identifier matches "a", then Rule.Parse sees more input and fails
        // the overall parse. ErrorCharIndex points at the first unconsumed
        // char, which is the start of the emoji.
        var input = Canary(
            "a😀b",
            "a + grinning face emoji + b",
            0x0061, 0x1F600, 0x0062);
        var result = Identifier().Parse(input);
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
        var input = UnicodeExamples.ArabicLigatureSallallahouGrapheme;
        var result = Identifier().Parse(input);
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
    }

    [Test]
    public void Script_capital_P_2118_is_added_to_start()
    {
        // U+2118 SCRIPT CAPITAL P is Sm by General_Category, so a
        // L-only check would reject it. UAX #31 adds it to XID_Start via
        // Other_ID_Start. This is the regression test for XidStartAdds: an
        // approximation that dropped the adds table would fail this.
        var input = UnicodeExamples.ScriptCapitalPGrapheme;
        var result = Identifier().Parse(input);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(input));
    }

    [Test]
    public void Katakana_voiced_sound_mark_309B_is_in_xid_start()
    {
        // U+309B KATAKANA-HIRAGANA VOICED SOUND MARK is GC=Sk
        // (Modifier_Symbol), so the L+Nl base in BuildXidStart doesn't
        // include it. UAX #31 adds it to XID_Start via Other_ID_Start
        // (PropList.txt: 309B..309C, in the property since Unicode 5.1).
        // The XidStartAdds table has to list this range explicitly because
        // the BCL's category lookup won't add it.
        var input = UnicodeExamples.KatakanaHiraganaVoicedSoundMarkGrapheme;
        var result = Identifier().Parse(input);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(input));
    }

    [Test]
    public void Katakana_semivoiced_sound_mark_309C_is_in_xid_start()
    {
        // U+309C KATAKANA-HIRAGANA SEMI-VOICED SOUND MARK is the high end
        // of the same Sk range covered by 309B's regression test. Tests the
        // upper endpoint so a future fat-fingered range like (0x309B, 0x309B)
        // would also fail this test.
        var input = UnicodeExamples.KatakanaHiraganaSemiVoicedSoundMarkGrapheme;
        var result = Identifier().Parse(input);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(input));
    }

    [Test]
    public void Katakana_voiced_sound_mark_309B_is_in_xid_continue()
    {
        // 309B is in XID_Continue too (XID_Start is a subset of
        // XID_Continue). Same Sk gap exists in XidContinueAdds. Use a
        // Hiragana letter as the start so this test exercises the
        // continue-position table specifically; if XidStartAdds were
        // fixed but XidContinueAdds were not, the start-position test
        // above would pass while this one would fail.
        var input = Canary("か゛", "hiragana ka + katakana-hiragana voiced sound mark (U+309B)", 0x304B, 0x309B);
        var result = Identifier().Parse(input);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(input));
    }

    [Test]
    public void Katakana_semivoiced_sound_mark_309C_is_in_xid_continue()
    {
        // Upper endpoint of the Sk range in continue position. Same shape
        // as the 309B continue-position test, locking in coverage of both
        // ends of the (0x309B, 0x309C) range in XidContinueAdds.
        var input = Canary("か゜", "hiragana ka + katakana-hiragana semi-voiced sound mark (U+309C)", 0x304B, 0x309C);
        var result = Identifier().Parse(input);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(input));
    }
}
