using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using InductorParser;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.CanaryHelper;

namespace InductorParser.Tests;

// Tests for Rules.Identifier() and the underlying TokenSet.XidStart /
// TokenSet.XidContinue tables. Two things under test:
//
//   1. UAX #31 R1 shape: "XID_Start XID_Continue*". Start-only characters
//      can't appear in Continue position and vice-versa.
//   2. The hand-curated add/exclude tables in TokenSet.Xid.cs are wired
//      into the build, with both their additions and exclusions verified
//      by the tests below.
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
    public void Tamil_identifier_with_vowel_sign_and_virama_matches()
    {
        // Tamil 'tamil' is five runes: TA (Lo), MA (Lo), VOWEL SIGN I
        // (Mc), LLLA (Lo), VIRAMA (Mn). The lexer composes them into
        // three graphemes. Both the spacing vowel sign and the virama
        // are XID_Continue, so the whole word matches after the leading
        // consonant.
        var word = UnicodeExamples.TamilTamizhIdentifier;
        var result = Identifier().Parse(word);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(word));
    }

    [Test]
    public void Tibetan_identifier_with_vowel_sign_matches()
    {
        // Tibetan 'bod' (Tibet) is BA (Lo) + VOWEL SIGN O (Mn) + DA (Lo).
        // The Mn vowel sign is XID_Continue, so the word matches.
        var word = UnicodeExamples.TibetanBodIdentifier;
        var result = Identifier().Parse(word);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(word));
    }

    [Test]
    public void Tibetan_stacked_subjoined_consonant_matches()
    {
        // KA (Lo) + SUBJOINED LETTER SSA (Mn): the Tibetan stacking case.
        // The subjoined consonant is a combining mark (XID_Continue), so
        // the two-rune stack is one identifier. WithinToken walks the
        // grapheme's runes and accepts KA as Start, subjoined SSA as
        // Continue.
        var word = UnicodeExamples.TibetanStackedKaSsaGrapheme;
        var result = Identifier().Parse(word);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(word));
    }

    [Test]
    public void Hebrew_identifier_matches_whole_word()
    {
        // Hebrew 'ivrit' (Hebrew) is five Lo letters with no combining
        // marks. RTL text is stored in logical (reading) order and the
        // parser walks it in that same order, so the match text comes
        // back byte-for-byte equal to the input. The parser doesn't apply
        // the Unicode Bidirectional Algorithm; there is no visual
        // reordering to undo.
        var word = UnicodeExamples.HebrewIvritIdentifier;
        var result = Identifier().Parse(word);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(word));
    }

    [Test]
    public void Hebrew_identifier_with_niqqud_matches()
    {
        // Hebrew 'shalom' with niqqud: SHIN + QAMATS + SHIN DOT + LAMED +
        // VAV + HOLAM + FINAL MEM. The vowel points are Mn combining marks
        // (XID_Continue) that bundle with their base letters into four
        // graphemes. RTL plus combining marks in one word: the lexer's
        // grapheme bundling and the identifier rules both have to handle
        // the points mid-word.
        var word = UnicodeExamples.HebrewShalomWithNiqqudIdentifier;
        var result = Identifier().Parse(word);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(word));
    }

    [Test]
    public void Cyrillic_identifier_matches_whole_word()
    {
        // Cyrillic 'privet' (hello) is six Ll letters with no combining
        // marks, so six single-rune graphemes. A real running-text word
        // rather than the lone homoglyph 'а' the security tests use.
        var word = UnicodeExamples.CyrillicPrivetIdentifier;
        var result = Identifier().Parse(word);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(word));
    }

    [Test]
    public void Arabic_identifier_matches_whole_word()
    {
        // Arabic 'arabiyya' (Arabic) is five Lo letters with no harakat
        // (vowel marks). Like Hebrew, RTL text is stored in logical
        // (reading) order and the parser walks it in that order, so the
        // match text comes back equal to the input. The parser doesn't
        // apply the Bidirectional Algorithm, so there's no visual
        // reordering to undo.
        var word = UnicodeExamples.ArabicArabiyyaIdentifier;
        var result = Identifier().Parse(word);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(word));
    }

    [Test]
    public void Japanese_mixed_script_identifier_matches_whole_word()
    {
        // 'ひらがなカタカナ漢字' mixes all three Japanese writing systems
        // in one run: hiragana (including the precomposed voiced GA,
        // U+304C), katakana, and kanji. Every rune is Lo / XID, so the
        // lexer reads the whole mixed-script run as a single identifier.
        var word = UnicodeExamples.JapaneseHiraganaKatakanaKanjiIdentifier;
        var result = Identifier().Parse(word);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(word));
    }

    [Test]
    public void Korean_identifier_matches_syllables_and_decomposed_jamo()
    {
        // '한국어' (the Korean language) is three precomposed Hangul
        // syllables, three single-rune graphemes. Both forms a reader
        // might supply have to work: the precomposed syllables most text
        // arrives in, and the conjoining-jamo form macOS filesystems hand
        // back.
        var syllables = UnicodeExamples.KoreanHangugeoIdentifier;
        var syllableResult = Identifier().Parse(syllables);
        Assert.That(syllableResult.Success, Is.True, syllableResult.ErrorMessage);
        Assert.That(syllableResult.Tree!.ToString(), Is.EqualTo(syllables));

        // Same word in NFD (eight conjoining jamo), derived from the
        // Canary-protected constant so the decomposition can't drift.
        // Default-FormC normalization composes it back to the three
        // syllables before the identifier rules run, so it still matches.
        var decomposedJamo = syllables.Normalize(System.Text.NormalizationForm.FormD);
        Assert.That(Identifier().Parse(decomposedJamo).Success, Is.True,
            "decomposed conjoining jamo recompose to syllables under FormC");
    }

    [Test]
    public void Chinese_simplified_and_traditional_both_match_and_stay_distinct()
    {
        // 汉字 (simplified) and 漢字 (traditional) are both valid
        // identifiers (CJK ideographs are Lo). They share the second
        // character 字 but differ in the first, and the difference is a
        // genuine code-point difference, not a normalization variant: NFC
        // leaves both alone. So both parse and their match texts differ.
        var simplified = Identifier().Parse(UnicodeExamples.ChineseSimplifiedHanziIdentifier);
        var traditional = Identifier().Parse(UnicodeExamples.ChineseTraditionalHanziIdentifier);

        Assert.That(simplified.Success, Is.True, simplified.ErrorMessage);
        Assert.That(traditional.Success, Is.True, traditional.ErrorMessage);
        Assert.That(simplified.Tree!.ToString(), Is.EqualTo(UnicodeExamples.ChineseSimplifiedHanziIdentifier));
        Assert.That(traditional.Tree!.ToString(), Is.EqualTo(UnicodeExamples.ChineseTraditionalHanziIdentifier));
        Assert.That(simplified.Tree!.ToString(), Is.Not.EqualTo(traditional.Tree!.ToString()));
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

    [TestCase(NormalizationForm.FormKC)]
    [TestCase(NormalizationForm.FormKD)]
    public void Compatibility_identifier_does_not_promote_continuation_piece_to_start(NormalizationForm form)
    {
        // U+0140 LATIN SMALL LETTER L WITH MIDDLE DOT is XID_Start and
        // NFKx-decomposes to "l" + U+00B7. U+00B7 is XID_Continue only,
        // so the decomposed source character is legal because the middle
        // dot lands after a valid start. A bare U+00B7 at the beginning
        // must still be rejected.
        const string middleDot = "\u00B7";
        const string lWithMiddleDot = "\u0140";
        var grammar = And(Identifier(form), Eof());
        grammar.Compile(form);

        Assert.That(TokenSet.XidStart.ContainsRune(0x00B7), Is.False,
            "MIDDLE DOT is not an identifier-start character");
        Assert.That(TokenSet.XidContinue.ContainsRune(0x00B7), Is.True,
            "MIDDLE DOT is allowed only after the first character");

        var bareMiddleDot = grammar.Parse(middleDot + "foo");
        Assert.That(bareMiddleDot.Success, Is.False,
            "The form-aware Identifier helper must not add continuation-only decomposition pieces to XID_Start");
        Assert.That(bareMiddleDot.ErrorCharIndex, Is.EqualTo(0));

        var decomposedIntoStartThenContinue = grammar.Parse(lWithMiddleDot + "foo");
        Assert.That(decomposedIntoStartThenContinue.Success, Is.True,
            decomposedIntoStartThenContinue.ErrorMessage);
        Assert.That(decomposedIntoStartThenContinue.Tree!.ToString(), Is.EqualTo("l\u00B7foo"));
    }

    [TestCase(NormalizationForm.FormKC)]
    [TestCase(NormalizationForm.FormKD)]
    public void XidStart_compatibility_continuations_are_all_in_XidContinue(NormalizationForm form)
    {
        // Rules.Identifier expands each XID_Start character's compatibility
        // decomposition into the start set but keeps only the first rune in
        // start position; every later rune is matched against the body set.
        // That split is sound only if those later runes are all XID_Continue.
        // UAX #31 Section 5.1.3 "Identifier Closure Under Normalization"
        // guarantees exactly that. This verifies the guarantee against the
        // runtime's actual Unicode data, so Identifier can rely on it without
        // re-checking ~130K code points on every grammar build. The comment
        // in Rules.Identifier references this test by name.
        var continueSet = TokenSet.XidContinue;
        int multiGraphemeStarts = 0;
        foreach (int rune in TokenSet.XidStart.EnumerateRunes())
        {
            string entry = char.ConvertFromUtf32(rune);
            if (entry.IsNormalized(form)) continue;
            string normalized;
            try { normalized = entry.Normalize(form); }
            catch (ArgumentException) { continue; }
            if (GraphemeClusters.Count(normalized) <= 1) continue;
            multiGraphemeStarts++;
            bool firstRune = true;
            foreach (int continuation in SurrogateHelpers.EnumerateRuneValues(normalized))
            {
                if (firstRune) { firstRune = false; continue; }
                Assert.That(continueSet.ContainsRune(continuation), Is.True,
                    $"U+{rune:X4} normalizes under {form} to \"{normalized}\"; continuation " +
                    $"rune U+{continuation:X4} must be XID_Continue for the Identifier " +
                    $"start/body split to stay sound");
            }
        }
        // Non-vacuity: confirm the form actually exercised the multi-grapheme
        // path, so a future Unicode-data change that stops decomposing these
        // can't make the test pass without checking anything.
        Assert.That(multiGraphemeStarts, Is.GreaterThan(0),
            "expected at least one XID_Start character to compatibility-decompose into multiple graphemes");
    }

    [TestCase(NormalizationForm.FormKC)]
    [TestCase(NormalizationForm.FormKD)]
    public void Identifier_rejects_start_extra_whose_decomposition_tail_is_not_a_body_character(NormalizationForm form)
    {
        // U+00BD VULGAR FRACTION ONE HALF normalizes under NFKC/NFKD to
        // "1" + U+2044 FRACTION SLASH + "2". The fraction slash isn't
        // XID_Continue, so adding 1/2 as an identifier-start extra leaves its
        // middle rune unmatchable in body position. Closure covers XidStart's
        // own entries but not caller extras, so Identifier must reject this at
        // grammar-build time instead of silently building a rule that can
        // never match the decomposed input.
        const string oneHalf = "\u00BD";
        const string fractionSlash = "\u2044";
        Assert.That(TokenSet.XidContinue.ContainsRune(0x2044), Is.False,
            "FRACTION SLASH must not be XID_Continue for this test to be meaningful");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            Identifier(form, extraStartRunes: TokenSet.Runes(oneHalf)));
        Assert.That(exception!.Message, Does.Contain("extraStartRunes"));
        Assert.That(exception.Message, Does.Contain("extraBodyRunes"));
        Assert.That(exception.Message, Does.Contain(fractionSlash));

        // Supplying the missing piece in extraBodyRunes clears the rejection.
        Assert.DoesNotThrow(() =>
            Identifier(form, extraStartRunes: TokenSet.Runes(oneHalf),
                       extraBodyRunes: TokenSet.Runes(fractionSlash)));
    }

    [Test]
    public void Identifier_rejects_multi_rune_grapheme_extras()
    {
        // A TokenSet can hold multi-rune grapheme clusters, but Identifier
        // matches one code point at a time, so such an entry could never
        // match in any position. Passing one as an extra is a grammar bug,
        // and Identifier rejects it at build time rather than silently
        // building a rule with a dead entry. LatinEAcuteGrapheme is the
        // canonical two-rune / one-grapheme fixture (e + combining acute);
        // its Canary check catches an editor normalizing the literal to
        // precomposed U+00E9.
        var grapheme = TokenSet.Graphemes(UnicodeExamples.LatinEAcuteGrapheme);
        Assert.That(grapheme.HasMultiRuneGraphemes, Is.True,
            "the test fixture must actually be a multi-rune grapheme");

        var startException = Assert.Throws<InvalidOperationException>(() =>
            Identifier(extraStartRunes: grapheme));
        Assert.That(startException!.Message, Does.Contain("extraStartRunes"));

        var bodyException = Assert.Throws<InvalidOperationException>(() =>
            Identifier(extraBodyRunes: grapheme));
        Assert.That(bodyException!.Message, Does.Contain("extraBodyRunes"));
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
    public void XidStart_includes_every_curated_addition()
    {
        // Positive counterpart to the over-admission tests further down:
        // every entry XidStartAdds claims to contribute is actually present,
        // range endpoints included so a fat-fingered range gets caught.
        // U+309B and U+309C aren't here because NFKC closure drops them
        // from XID_Start; see Katakana_voicing_marks_are_excluded_by_NFKC_closure.
        var startSet = TokenSet.XidStart;
        Assert.That(startSet.ContainsRune(0x1885), Is.True, "MONGOLIAN LETTER ALI GALI BALUDA (Mn, range low)");
        Assert.That(startSet.ContainsRune(0x1886), Is.True, "MONGOLIAN LETTER ALI GALI THREE BALUDA (Mn, range high)");
        Assert.That(startSet.ContainsRune(0x2118), Is.True, "SCRIPT CAPITAL P (Sm)");
        Assert.That(startSet.ContainsRune(0x212E), Is.True, "ESTIMATED SYMBOL (So)");
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
    public void Katakana_voicing_marks_are_excluded_by_NFKC_closure()
    {
        // U+309B and U+309C are in Other_ID_Start, which puts them in
        // ID_Start. But XID_Start is ID_Start closed under NFKx (the
        // K-form normalizations, NFKC and NFKD), and both K-decompose to
        // SPACE + a combining mark (U+0020 + U+3099 / U+309A), which can't
        // be a valid identifier start. So the spec excludes them from both
        // XID_Start and XID_Continue, even though Other_ID_Start lists
        // them. Confirmed against the Unicode DerivedCoreProperties.txt:
        // 309B..309C appears under ID_Start / ID_Continue but the XID
        // sections skip from 3041..3096 straight to 309D..309E.
        Assert.That(TokenSet.XidStart.ContainsRune(0x309B), Is.False,
            "U+309B is excluded from XID_Start by NFKC closure");
        Assert.That(TokenSet.XidStart.ContainsRune(0x309C), Is.False,
            "U+309C is excluded from XID_Start by NFKC closure");
        Assert.That(TokenSet.XidContinue.ContainsRune(0x309B), Is.False,
            "U+309B is excluded from XID_Continue by NFKC closure");
        Assert.That(TokenSet.XidContinue.ContainsRune(0x309C), Is.False,
            "U+309C is excluded from XID_Continue by NFKC closure");

        // Behavioral: Identifier() rejects a bare voicing mark.
        var grammar = And(Identifier(), Eof()).Compile();
        Assert.That(grammar.Parse(UnicodeExamples.KatakanaHiraganaVoicedSoundMarkGrapheme).Success, Is.False,
            "Identifier() must not match U+309B as an identifier");
        Assert.That(grammar.Parse(UnicodeExamples.KatakanaHiraganaSemiVoicedSoundMarkGrapheme).Success, Is.False,
            "Identifier() must not match U+309C as an identifier");
    }

    [Test]
    public void XidContinue_includes_every_curated_addition()
    {
        // Positive counterpart to the over-admission tests above and to the
        // 309B / 309C tests: every entry the XidContinueAdds table claims to
        // contribute is actually present, range endpoints included so a
        // fat-fingered range (e.g. 0x1369..0x1370) is caught. Without this,
        // the curated additions are asserted only by the table's own comment.
        var continueSet = TokenSet.XidContinue;

        // Other_ID_Continue: code points the L+Nl+Mn+Mc+Nd+Pc base misses.
        Assert.That(continueSet.ContainsRune(0x00B7), Is.True, "MIDDLE DOT (Po)");
        Assert.That(continueSet.ContainsRune(0x0387), Is.True, "GREEK ANO TELEIA (Po)");
        Assert.That(continueSet.ContainsRune(0x1369), Is.True, "ETHIOPIC DIGIT ONE (range low)");
        Assert.That(continueSet.ContainsRune(0x1371), Is.True, "ETHIOPIC DIGIT NINE (range high)");
        Assert.That(continueSet.ContainsRune(0x19DA), Is.True, "NEW TAI LUE THAM DIGIT ONE (No)");
        Assert.That(continueSet.ContainsRune(0x200C), Is.True, "ZERO WIDTH NON-JOINER (Cf, added in Unicode 16)");
        Assert.That(continueSet.ContainsRune(0x200D), Is.True, "ZERO WIDTH JOINER (Cf, added in Unicode 16)");
        Assert.That(continueSet.ContainsRune(0x30FB), Is.True, "KATAKANA MIDDLE DOT (Po, added in Unicode 16)");
        Assert.That(continueSet.ContainsRune(0xFF65), Is.True, "HALFWIDTH KATAKANA MIDDLE DOT (Po, added in Unicode 16)");

        // Symbol-category Other_ID_Start members that survive NFKC closure
        // (XID_Start is a subset of XID_Continue, so they show up here too).
        // U+309B and U+309C are deliberately NOT in this list because NFKC
        // closure drops them from XID_Continue; see
        // Katakana_voicing_marks_are_excluded_by_NFKC_closure.
        Assert.That(continueSet.ContainsRune(0x2118), Is.True, "SCRIPT CAPITAL P (Sm)");
        Assert.That(continueSet.ContainsRune(0x212E), Is.True, "ESTIMATED SYMBOL (So)");

        // The "already in the union" claim in XidContinueAdds' comment: the
        // one non-symbol Other_ID_Start member, U+1885..1886 (Mn), is in
        // XidContinue via the Mn category base, NOT via the adds table.
        Assert.That(continueSet.ContainsRune(0x1885), Is.True, "MONGOLIAN LETTER ALI GALI BALUDA (Mn, range low)");
        Assert.That(continueSet.ContainsRune(0x1886), Is.True, "MONGOLIAN LETTER ALI GALI THREE BALUDA (Mn, range high)");
    }

    [Test]
    public void XidContinueExclusions_keep_their_members_out()
    {
        // Negative counterpart to XidContinue_includes_every_curated_addition:
        // every entry in XidContinueExclusions actually stays out of
        // XidContinue. Verified against Python str.isidentifier on Unicode 15,
        // which uses the same UAX #31 XID_Continue property.
        var continueSet = TokenSet.XidContinue;

        // Non-Arabic NFKC-unstable exclusions.
        Assert.That(continueSet.ContainsRune(0x037A), Is.False, "GREEK YPOGEGRAMMENI");
        Assert.That(continueSet.ContainsRune(0x2E2F), Is.False, "VERTICAL TILDE");

        // FC5E..FC63 range (Arabic ligature shadda + harakat isolated forms).
        Assert.That(continueSet.ContainsRune(0xFC5E), Is.False, "ARABIC LIGATURE SHADDA WITH DAMMATAN (range low)");
        Assert.That(continueSet.ContainsRune(0xFC63), Is.False, "ARABIC LIGATURE SHADDA WITH KASRA (range high)");

        // FDFA..FDFB Arabic phrase ligatures.
        Assert.That(continueSet.ContainsRune(0xFDFA), Is.False, "ARABIC LIGATURE SALLALLAHOU (range low)");
        Assert.That(continueSet.ContainsRune(0xFDFB), Is.False, "ARABIC LIGATURE JALLAJALALOUHOU (range high)");

        // FE70..FE7E Arabic harakat isolated forms. The whole table is
        // excluded from XID_Continue, so every entry XidStartExclusions
        // lists in this block must also be in XidContinueExclusions.
        Assert.That(continueSet.ContainsRune(0xFE70), Is.False, "ARABIC FATHATAN ISOLATED FORM");
        Assert.That(continueSet.ContainsRune(0xFE72), Is.False, "ARABIC DAMMATAN ISOLATED FORM");
        Assert.That(continueSet.ContainsRune(0xFE74), Is.False, "ARABIC KASRATAN ISOLATED FORM");
        Assert.That(continueSet.ContainsRune(0xFE76), Is.False, "ARABIC FATHA ISOLATED FORM");
        Assert.That(continueSet.ContainsRune(0xFE78), Is.False, "ARABIC DAMMA ISOLATED FORM");
        Assert.That(continueSet.ContainsRune(0xFE7A), Is.False, "ARABIC KASRA ISOLATED FORM");
        Assert.That(continueSet.ContainsRune(0xFE7C), Is.False, "ARABIC SHADDA ISOLATED FORM");
        Assert.That(continueSet.ContainsRune(0xFE7E), Is.False, "ARABIC SUKUN ISOLATED FORM");
    }

    [Test]
    public void XidContinue_keeps_sara_am_and_lao_vowel_sign_am()
    {
        // Asymmetry pin: SARA AM (U+0E33) and LAO VOWEL SIGN AM (U+0EB3)
        // are NFKC-unstable enough to be kept out of XID_Start (they appear
        // in XidStartExclusions), but they're spacing combining marks (Mc)
        // that attach to a preceding letter, so UAX #31 keeps them IN
        // XID_Continue. The two exclusion tables are deliberately asymmetric
        // here, and dropping these into XidContinueExclusions would wrongly
        // reject identifiers like Thai "kam" mid-word.
        Assert.That(TokenSet.XidContinue.ContainsRune(0x0E33), Is.True,
            "THAI CHARACTER SARA AM is in XID_Continue");
        Assert.That(TokenSet.XidContinue.ContainsRune(0x0EB3), Is.True,
            "LAO VOWEL SIGN AM is in XID_Continue");
    }

    // ----- UCD conformance checks for the spec constants -----
    //
    // Each of the six spec-named constants in TokenSet.Xid.cs has a
    // "Recipe to regenerate" block in its comment. These tests run those
    // recipes against the live UCD files and assert the hand-typed
    // contents match. If a test fails, the failure message lists the
    // code points that disagree; the fix is to update the hand-typed
    // constant per the recipe in its comment.
    //
    // The General_Category half of BuildXidStart / BuildXidContinue is
    // the .NET BCL's responsibility, and the BCL tracks whatever Unicode
    // version it ships with, which may be older than the version the
    // spec constants claim. These tests deliberately check the spec
    // constants only, not the end result TokenSet.XidStart /
    // TokenSet.XidContinue, because the latter would conflate the
    // maintainer's job (the spec constants) with the BCL's Unicode
    // version (out of our control).
    //
    // [Explicit] because they hit unicode.org. Run them on demand when
    // reviewing the constants or bumping the Unicode version. Locking to
    // a specific version (not "latest") keeps them stable across Unicode
    // releases.

    private const string UnicodeVersion = "17.0.0";
    private static readonly HashSet<string> IDStartBaseCategories =
        new() { "L&", "Lu", "Ll", "Lt", "Lm", "Lo", "Nl" };
    private static readonly HashSet<string> IDContinueBaseCategories =
        new(IDStartBaseCategories) { "Mn", "Mc", "Nd", "Pc" };

    [Test, Explicit("Fetches PropList.txt from unicode.org. Run on demand or when bumping the Unicode version the spec constants track.")]
    public async Task OtherIdStart_matches_UCD_test()
    {
        string propList = await FetchPropListAsync();
        var expected = CodePointsForProperty(propList, "Other_ID_Start");
        var actual = ExpandRanges(ReadPrivateRangeTable("OtherIdStart"));
        AssertSetsEqual("OtherIdStart", expected, actual);
    }

    [Test, Explicit("Fetches PropList.txt from unicode.org.")]
    public async Task OtherIdContinue_matches_UCD_test()
    {
        string propList = await FetchPropListAsync();
        var expected = CodePointsForProperty(propList, "Other_ID_Continue");
        var actual = ExpandRanges(ReadPrivateRangeTable("OtherIdContinue"));
        AssertSetsEqual("OtherIdContinue", expected, actual);
    }

    [Test, Explicit("Fetches DerivedCoreProperties.txt from unicode.org.")]
    public async Task NfkxClosureRemovedFromXidStart_matches_UCD_test()
    {
        string coreProps = await FetchDerivedCorePropertiesAsync();
        var expected = CodePointsForProperty(coreProps, "ID_Start");
        expected.ExceptWith(CodePointsForProperty(coreProps, "XID_Start"));
        var actual = ExpandRanges(ReadPrivateRangeTable("NfkxClosureRemovedFromXidStart"));
        AssertSetsEqual("NfkxClosureRemovedFromXidStart", expected, actual);
    }

    [Test, Explicit("Fetches DerivedCoreProperties.txt from unicode.org.")]
    public async Task NfkxClosureRemovedFromXidContinue_matches_UCD_test()
    {
        string coreProps = await FetchDerivedCorePropertiesAsync();
        var expected = CodePointsForProperty(coreProps, "ID_Continue");
        expected.ExceptWith(CodePointsForProperty(coreProps, "XID_Continue"));
        var actual = ExpandRanges(ReadPrivateRangeTable("NfkxClosureRemovedFromXidContinue"));
        AssertSetsEqual("NfkxClosureRemovedFromXidContinue", expected, actual);
    }

    [Test, Explicit("Fetches PropList.txt and DerivedGeneralCategory.txt from unicode.org.")]
    public async Task IdCategoriesInPatternSyntax_matches_UCD_test()
    {
        string propList = await FetchPropListAsync();
        string generalCategory = await FetchDerivedGeneralCategoryAsync();
        var expected = CodePointsForCategories(generalCategory, IDContinueBaseCategories);
        expected.IntersectWith(CodePointsForProperty(propList, "Pattern_Syntax"));
        var actual = ExpandRanges(ReadPrivateRangeTable("IdCategoriesInPatternSyntax"));
        AssertSetsEqual("IdCategoriesInPatternSyntax", expected, actual);
    }

    [Test, Explicit("Fetches PropList.txt and DerivedGeneralCategory.txt from unicode.org.")]
    public async Task IdCategoriesInPatternWhiteSpace_matches_UCD_test()
    {
        string propList = await FetchPropListAsync();
        string generalCategory = await FetchDerivedGeneralCategoryAsync();
        var expected = CodePointsForCategories(generalCategory, IDContinueBaseCategories);
        expected.IntersectWith(CodePointsForProperty(propList, "Pattern_White_Space"));
        var actual = ExpandRanges(ReadPrivateRangeTable("IdCategoriesInPatternWhiteSpace"));
        AssertSetsEqual("IdCategoriesInPatternWhiteSpace", expected, actual);
    }

    // Verifies that the formula's "- Pattern_Syntax - Pattern_White_Space"
    // step is realized in TokenSet.XidStart and TokenSet.XidContinue. The
    // build doesn't subtract those two properties directly; it relies on
    // IdCategoriesInPatternSyntax for the Pattern_Syntax portion and on
    // Pattern_White_Space being disjoint from the identifier base. This
    // test asserts both halves against the actual built sets: every code
    // point in (identifier base ∩ Pattern_Syntax) or (identifier base ∩
    // Pattern_White_Space) at Unicode 17.0 must be absent from XidStart
    // and XidContinue.
    [Test, Explicit("Fetches PropList.txt and DerivedGeneralCategory.txt from unicode.org.")]
    public async Task Pattern_Syntax_and_Pattern_White_Space_subtractions_apply_test()
    {
        string propList = await FetchPropListAsync();
        string generalCategory = await FetchDerivedGeneralCategoryAsync();

        var identifierBase = CodePointsForCategories(generalCategory, IDContinueBaseCategories);
        var patternSyntaxInBase = new HashSet<int>(identifierBase);
        patternSyntaxInBase.IntersectWith(CodePointsForProperty(propList, "Pattern_Syntax"));
        var patternWhiteSpaceInBase = new HashSet<int>(identifierBase);
        patternWhiteSpaceInBase.IntersectWith(CodePointsForProperty(propList, "Pattern_White_Space"));

        var failures = new List<string>();
        foreach (var codepoint in patternSyntaxInBase)
        {
            if (TokenSet.XidStart.ContainsRune(codepoint))
                failures.Add($"U+{codepoint:X4}: in Pattern_Syntax and the identifier base, but TokenSet.XidStart admits it");
            if (TokenSet.XidContinue.ContainsRune(codepoint))
                failures.Add($"U+{codepoint:X4}: in Pattern_Syntax and the identifier base, but TokenSet.XidContinue admits it");
        }
        foreach (var codepoint in patternWhiteSpaceInBase)
        {
            if (TokenSet.XidStart.ContainsRune(codepoint))
                failures.Add($"U+{codepoint:X4}: in Pattern_White_Space and the identifier base, but TokenSet.XidStart admits it");
            if (TokenSet.XidContinue.ContainsRune(codepoint))
                failures.Add($"U+{codepoint:X4}: in Pattern_White_Space and the identifier base, but TokenSet.XidContinue admits it");
        }

        Assert.That(failures, Is.Empty,
            "The Pattern_Syntax and Pattern_White_Space subtractions from the " +
            "formula must be realized in TokenSet.XidStart / TokenSet.XidContinue:\n  " +
            string.Join("\n  ", failures));
    }

    // --------- Helpers ---------

    private static async Task<string> FetchDerivedCorePropertiesAsync()
    {
        string url = $"https://www.unicode.org/Public/{UnicodeVersion}/ucd/DerivedCoreProperties.txt";
        using var http = new HttpClient();
        return await http.GetStringAsync(url);
    }

    private static async Task<string> FetchDerivedGeneralCategoryAsync()
    {
        string url = $"https://www.unicode.org/Public/{UnicodeVersion}/ucd/extracted/DerivedGeneralCategory.txt";
        using var http = new HttpClient();
        return await http.GetStringAsync(url);
    }

    private static async Task<string> FetchPropListAsync()
    {
        string url = $"https://www.unicode.org/Public/{UnicodeVersion}/ucd/PropList.txt";
        using var http = new HttpClient();
        return await http.GetStringAsync(url);
    }

    // Collect all code points covered by a single named property
    // (e.g. "XID_Start", "Other_ID_Start", "Pattern_Syntax") from a UCD
    // file in the property-per-line format that both
    // DerivedCoreProperties.txt and PropList.txt use.
    private static HashSet<int> CodePointsForProperty(string ucd, string property)
    {
        var result = new HashSet<int>();
        foreach (var (low, high, secondColumn) in ParseUcdLines(ucd))
            if (secondColumn == property)
                for (int cp = low; cp <= high; cp++)
                    result.Add(cp);
        return result;
    }

    // Collect all code points whose General_Category column matches one
    // of the given categories from DerivedGeneralCategory.txt.
    private static HashSet<int> CodePointsForCategories(string generalCategory, HashSet<string> categories)
    {
        var result = new HashSet<int>();
        foreach (var (low, high, secondColumn) in ParseUcdLines(generalCategory))
            if (categories.Contains(secondColumn))
                for (int cp = low; cp <= high; cp++)
                    result.Add(cp);
        return result;
    }

    // === Grammar for a whole UCD property file ===
    //
    // A UCD property file is a sequence of comment lines, blank lines,
    // and data lines. We build one grammar that handles all three and
    // parses the whole file in one Parse call, then FindAll the data
    // lines for processing. Dogfoods the parser through a realistic
    // multi-line file format instead of using Regex line by line.
    //
    // Data line shape: "HEX[..HEX] ; SecondColumn # ThirdColumn trailing".
    // The SecondColumn is what the calling helper filters on. In
    // DerivedCoreProperties.txt and PropList.txt it's a property name
    // (e.g. "XID_Start", "Other_ID_Continue"); in DerivedGeneralCategory.txt
    // it's a General_Category label (e.g. "Lu", "L&"). The trailing text
    // (which includes the ThirdColumn and the name comment) is consumed
    // and discarded by a body-up-to-end-of-line scan.

    // Property names and category labels are made of letters, digits,
    // underscore (XID_Start), and ampersand (the L& shorthand).
    private static readonly TokenSet UcdPropertyNameRunes =
        TokenSet.Letters | TokenSet.Digits | TokenSet.Runes("_&");

    private static readonly Rule _ucdRangeLow =
        ScanWhile(TokenSet.Ascii.HexDigits).As("low");
    private static readonly Rule _ucdRangeHigh =
        ScanWhile(TokenSet.Ascii.HexDigits).As("high");
    private static readonly Rule _ucdSecondColumn =
        ScanWhile(UcdPropertyNameRunes).As("secondColumn");

    // One data line, tagged with .As("dataLine") so the file-level parse
    // can pull all the data lines out with FindAll.
    private static readonly Rule _ucdDataLine = And(
        Optional(InlineWhitespace()),
        _ucdRangeLow,
        Optional(And(Literal(".."), _ucdRangeHigh)),
        Optional(InlineWhitespace()),
        Token(';'),
        Optional(InlineWhitespace()),
        _ucdSecondColumn,
        ScanUntil(TokenSet.LineTerminators, eofIsTerminator: true),
        EndOfLine(eofIsEol: true)).As("dataLine");

    // Comment line: leading whitespace, '#', everything up to the EOL.
    private static readonly Rule _ucdCommentLine = And(
        Optional(InlineWhitespace()),
        Token('#'),
        ScanUntil(TokenSet.LineTerminators, eofIsTerminator: true),
        EndOfLine(eofIsEol: true));

    // Blank line: only whitespace, then an EOL.
    private static readonly Rule _ucdBlankLine = And(
        Optional(InlineWhitespace()),
        EndOfLine(eofIsEol: true));

    // The whole UCD file: a sequence of lines. The Or tries comment
    // first (unambiguously identified by leading '#'), then blank, then
    // data. With ordered choice, the first matching branch wins.
    private static readonly Rule _ucdFile =
        ZeroOrMore(Or(_ucdCommentLine, _ucdBlankLine, _ucdDataLine));

    // Parse data lines from any UCD property file (DerivedCoreProperties.txt,
    // PropList.txt, or DerivedGeneralCategory.txt). One Parse call for the
    // whole file; FindAll the data lines; pull the captured pieces out of
    // each. Yields (low, high, secondColumn) for each data line.
    private static IEnumerable<(int Low, int High, string SecondColumn)> ParseUcdLines(string ucd)
    {
        var result = _ucdFile.Parse(ucd);
        if (!result.Success)
            throw new InvalidOperationException(
                $"UCD parse failed at char {result.ErrorCharIndex}: {result.ErrorMessage}");

        foreach (var dataLine in result.FindAll(_ucdDataLine))
        {
            int low = int.Parse(dataLine.Find(_ucdRangeLow)!.ToString(), NumberStyles.HexNumber);
            var highSymbol = dataLine.Find(_ucdRangeHigh);
            int high = highSymbol != null
                ? int.Parse(highSymbol.ToString(), NumberStyles.HexNumber)
                : low;
            string secondColumn = dataLine.Find(_ucdSecondColumn)!.ToString();
            yield return (low, high, secondColumn);
        }
    }

    // Read one of the private static readonly range tables from TokenSet.Xid.cs
    // via reflection. Verifies "what's in the file" without making those
    // tables part of the public or internal surface.
    private static (int Low, int High)[] ReadPrivateRangeTable(string fieldName)
    {
        var field = typeof(TokenSet).GetField(
            fieldName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException(
                $"Private field TokenSet.{fieldName} not found by reflection.");
        return ((int Low, int High)[])field.GetValue(null)!;
    }

    // Expand a list of (low, high) ranges into a flat set of code points.
    // Makes set-equality checks across differing range representations
    // (e.g. one big range vs two adjacent ranges) work uniformly.
    private static HashSet<int> ExpandRanges(IEnumerable<(int Low, int High)> ranges)
    {
        var set = new HashSet<int>();
        foreach (var (low, high) in ranges)
            for (int codepoint = low; codepoint <= high; codepoint++)
                set.Add(codepoint);
        return set;
    }

    // Compare two sets of code points; report at most 20 differences in each
    // direction so a mass divergence produces a readable failure rather than
    // an unbounded dump.
    private static void AssertSetsEqual(string label, HashSet<int> expected, HashSet<int> actual)
    {
        var missingFromActual = new List<int>();
        foreach (var codepoint in expected)
            if (!actual.Contains(codepoint)) missingFromActual.Add(codepoint);
        var extraInActual = new List<int>();
        foreach (var codepoint in actual)
            if (!expected.Contains(codepoint)) extraInActual.Add(codepoint);
        missingFromActual.Sort();
        extraInActual.Sort();

        var details = new List<string>();
        if (missingFromActual.Count > 0)
            details.Add($"Missing from {label}: " + FormatPoints(missingFromActual));
        if (extraInActual.Count > 0)
            details.Add($"Extra in {label}: " + FormatPoints(extraInActual));

        Assert.That(details, Is.Empty,
            $"{label} doesn't match the recipe applied to Unicode {UnicodeVersion}.\n  " +
            string.Join("\n  ", details));
    }

    private static string FormatPoints(List<int> points)
    {
        var parts = new List<string>();
        for (int i = 0; i < points.Count && i < 20; i++)
            parts.Add($"U+{points[i]:X4}");
        if (points.Count > 20) parts.Add($"...({points.Count - 20} more)");
        return string.Join(", ", parts);
    }
}
