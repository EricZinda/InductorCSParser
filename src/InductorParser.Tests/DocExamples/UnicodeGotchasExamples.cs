using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

using static InductorParser.Tests.CanaryHelper;
namespace InductorParser.Tests.DocExamples;

// Verifies the runnable code examples in docs/UnicodeGotchas.md, plus
// the invisible-character, homoglyph, and variation-selector recipes
// that live in docs/Primer4.md (the security primer).
[TestFixture]
public class UnicodeGotchasExamples
{
    // "Identifier Matching": Identifier accepts foo, café, καλημέρα, ℼ.
    // Rejects 2foo, _foo (under strict UAX #31), and the Arabic ligature
    // ﷺ (NFKC-unstable exclusion).
    [Test]
    public void Identifier_strict_UAX31_acceptance()
    {
        var name = Identifier().As("name").Compile();

        Assert.That(name.Parse("foo").Success, Is.True);
        Assert.That(name.Parse(UnicodeExamples.CafePrecomposedGrapheme).Success, Is.True);
        Assert.That(name.Parse(UnicodeExamples.GreekKalimeraIdentifier).Success, Is.True);
        Assert.That(name.Parse(UnicodeExamples.DoubleStruckSmallPiGrapheme).Success, Is.True,
            "U+213C DOUBLE-STRUCK SMALL PI is a lowercase letter (Ll), so it's in XID_Start");

        Assert.That(name.Parse("2foo").Success, Is.False);
        Assert.That(name.Parse("_foo").Success, Is.False,
            "underscore isn't in strict XID_Start");
        Assert.That(name.Parse(UnicodeExamples.ArabicLigatureSallallahouGrapheme).Success, Is.False,
            "Arabic ligature U+FDFA, NFKC-unstable exclusion");
    }

    // "Identifier Matching" / NFC equivalence (UAX #31 R4): "café" with
    // precomposed é (U+00E9) and "café" as e+combining-acute (U+0301)
    // both parse the same way under default FormC normalization.
    [Test]
    public void Identifier_normalizes_precomposed_and_decomposed_forms()
    {
        var name = Identifier().Compile();

        string precomposed = UnicodeExamples.CafePrecomposedGrapheme;   // single rune é
        string decomposed = $"cafe{UnicodeExamples.CombiningAcuteText}"; // e + combining acute

        Assert.That(name.Parse(precomposed).Success, Is.True);
        Assert.That(name.Parse(decomposed).Success, Is.True);
    }

    // "Identifier Matching": works on Devanagari, Thai with SARA AM,
    // Greek under the lexer.
    [Test]
    public void Identifier_works_on_multi_script_input()
    {
        var name = Identifier().Compile();

        Assert.That(name.Parse(UnicodeExamples.DevanagariHindiIdentifier).Success, Is.True, "Devanagari");
        Assert.That(name.Parse(UnicodeExamples.ThaiKamGrapheme).Success, Is.True, "Thai with SARA AM");
        Assert.That(name.Parse(UnicodeExamples.GreekKalimeraIdentifier).Success, Is.True, "Greek");
    }

    // "WithinToken: general-purpose sub-grapheme matching": the
    // ASCII-only-letter example. Accepts any grapheme whose runes are
    // all ASCII letters. Rejects "é" (not ASCII) and decomposed "é"
    // (multi-rune, even though both runes are letters).
    [Test]
    public void WithinToken_ascii_only_letter_example()
    {
        var asciiOnlyLetter = WithinToken(OneOf(TokenSet.Ascii.Letters))
            .Compile();

        Assert.That(asciiOnlyLetter.Parse("a").Success, Is.True);
        Assert.That(asciiOnlyLetter.Parse("Z").Success, Is.True);
        Assert.That(asciiOnlyLetter.Parse(UnicodeExamples.LatinEAcutePrecomposedGrapheme).Success, Is.False,
            $"precomposed {UnicodeExamples.LatinEAcutePrecomposedGrapheme} isn't ASCII");
    }

    // "Matching specific languages" / "Python 3 identifiers" recipe.
    // Adds underscore to start, NFKC normalization. The form lives on
    // Compile only: Identifier defers its form-aware expansion of
    // XidStart / XidContinue to Compile time, so the caller doesn't
    // repeat the form on both calls.
    [Test]
    public void Python3_identifier_recipe()
    {
        var python = Identifier(extraStartRunes: TokenSet.Runes("_"))
            .Compile(NormalizationForm.FormKC);

        var result = python.Parse("_foo");
        Assert.That(result.Success, Is.True);
    }

    // "Matching specific languages" / "Rust identifiers" recipe. Adds `_`
    // to Start like Python 3, but Rust normalizes identifiers with NFC, not
    // NFKC (Rust Reference "Identifiers", RFC 2457), so the form is FormC,
    // not FormKC. Added as its own test because the Rust example sits in its
    // own code block in UnicodeGotchas.md.
    [Test]
    public void Rust_identifier_recipe()
    {
        var rust = Identifier(extraStartRunes: TokenSet.Runes("_"))
            .Compile(NormalizationForm.FormC);

        Assert.That(rust.Parse("_foo").Success, Is.True);

        // NFC, not NFKC, is the whole point of FormC here. U+2460 CIRCLED
        // DIGIT ONE is category No (not an identifier character), and NFC
        // leaves it unchanged, so Rust rejects "x" + circled-one. The old
        // FormKC recipe normalized that input to "x1" before lexing and
        // wrongly accepted it, treating a non-Rust identifier as valid.
        string circledDigitInput = Canary("x①", "ASCII x, U+2460 CIRCLED DIGIT ONE", 0x78, 0x2460);
        Assert.That(rust.Parse(circledDigitInput).Success, Is.False);
    }

    // Locks in that Identifier's form-aware set expansion runs at
    // Compile time. The caller only writes the form on Compile, and
    // Identifier itself doesn't take a form. Compatibility-equivalent entries in
    // XidStart and XidContinue (ligatures, fullwidth Latin, math-bold)
    // are expanded into their grapheme pieces by IdentifierRule before
    // the form-validation pass runs, so this no longer throws.
    [Test]
    public void Identifier_with_FormKC_compile_succeeds_after_deferral()
    {
        var rule = Identifier(extraStartRunes: TokenSet.Runes("_"))
            .Compile(NormalizationForm.FormKC);

        Assert.That(rule.Parse("_foo").Success, Is.True);
    }

    // "Case-Insensitive Matching Beyond ASCII", the LiteralIgnoreAsciiCase
    // leaf does ASCII case-insensitive matching (A <-> a).
    [Test]
    public void LiteralIgnoreAsciiCase_matches_ascii_case_insensitively()
    {
        var selectKeyword = LiteralIgnoreAsciiCase("select").Compile();

        Assert.That(selectKeyword.Parse("select").Success, Is.True);
        Assert.That(selectKeyword.Parse("SELECT").Success, Is.True);
        Assert.That(selectKeyword.Parse("Select").Success, Is.True);
        Assert.That(selectKeyword.Parse("SeLeCt").Success, Is.True);
    }

    // "BOM At File Start": stripping U+FEFF before parsing makes a
    // BOM-prefixed input parse against a grammar that expects a specific
    // first character.
    [Test]
    public void BOM_strip_recipe()
    {
        var grammar = And(Literal("function"), Eof()).Compile();

        string bomPlusKeyword = $"{UnicodeExamples.ByteOrderMarkText}function";
        Assert.That(grammar.Parse(bomPlusKeyword).Success, Is.False,
            "BOM at start blocks the keyword match");

        var cleaned = bomPlusKeyword.TrimStart('\uFEFF');
        Assert.That(grammar.Parse(cleaned).Success, Is.True);
    }

    // Primer4.md "Invisible characters": stripping them
    // before parsing. Soft hyphen (U+00AD) inside "ap­ple" makes
    // it not match Literal("apple") until the soft hyphen is stripped.
    [Test]
    public void Invisible_format_character_strip_recipe()
    {
        var grammar = And(Literal("apple"), Eof()).Compile();

        string withSoftHyphen = $"ap{UnicodeExamples.SoftHyphenText}ple";
        Assert.That(grammar.Parse(withSoftHyphen).Success, Is.False,
            "soft hyphen breaks the literal match");

        var invisibleFormat = new HashSet<int>
        {
            0x200B,  // zero-width space
            0x200C,  // zero-width non-joiner
            0x200D,  // zero-width joiner
            0x00AD,  // soft hyphen
            0xFEFF,  // BOM
        };
        var cleaned = string.Concat(withSoftHyphen.EnumerateRunes()
            .Where(r => !invisibleFormat.Contains(r.Value)));

        Assert.That(grammar.Parse(cleaned).Success, Is.True);
    }

    // Primer4.md "Invisible characters": the joiner
    // characters ZWJ (U+200D) and ZWNJ (U+200C) don't come through as
    // their own tokens after a base character. They have UAX #29
    // grapheme-break properties (ZWJ and Extend), so rule GB9 glues them
    // onto the preceding character. ZWSP (U+200B) and the soft hyphen
    // (U+00AD) have no such rule and lex as their own single-rune
    // tokens. This is the coverage the doc's tokenization claim needs:
    // the strip recipe test above only exercises the soft hyphen.
    [Test]
    public void Zero_width_joiners_glue_to_preceding_grapheme()
    {
        // Built from code points, not source literals, so an invisible
        // character can't be silently stripped or mis-rendered.
        string zwj = ((char)0x200D).ToString();   // GCB = ZWJ
        string zwnj = ((char)0x200C).ToString();  // GCB = Extend
        string zwsp = ((char)0x200B).ToString();  // GCB = Other (breaks)
        string shy = ((char)0x00AD).ToString();   // soft hyphen, GCB = Other (breaks)

        // "a" + ZWJ + "b": the joiner rides along with "a", so the first
        // token is the two-char grapheme "a‍", not a bare "a". A
        // grammar that expects three standalone tokens a, joiner, b fails.
        var threeTokens = And(Token('a'), AnyToken(), Token('b'), Eof()).Compile();
        Assert.That(threeTokens.Parse("a" + zwj + "b").Success, Is.False,
            "ZWJ glues to the preceding 'a', so 'a' is not a standalone token");
        Assert.That(threeTokens.Parse("a" + zwnj + "b").Success, Is.False,
            "ZWNJ glues to the preceding 'a' the same way");

        // The grapheme the joiner glued onto matches as one Token.
        var gluedZwj = And(Token("a" + zwj), Token('b'), Eof()).Compile();
        Assert.That(gluedZwj.Parse("a" + zwj + "b").Success, Is.True,
            "'a\\u200D' is one token");
        var gluedZwnj = And(Token("a" + zwnj), Token('b'), Eof()).Compile();
        Assert.That(gluedZwnj.Parse("a" + zwnj + "b").Success, Is.True,
            "'a\\u200C' is one token");

        // A joiner only stands alone when nothing precedes it.
        var leadingJoiner = And(Token(zwj), Token('a'), Eof()).Compile();
        Assert.That(leadingJoiner.Parse(zwj + "a").Success, Is.True,
            "a leading ZWJ is its own token");

        // The genuinely breaking format chars DO come through as their
        // own single-rune tokens: a, the format char, then b.
        var zwspBreaks = And(Token('a'), Token(zwsp), Token('b'), Eof()).Compile();
        Assert.That(zwspBreaks.Parse("a" + zwsp + "b").Success, Is.True,
            "ZWSP (U+200B) is its own token");
        var softHyphenBreaks = And(Token('a'), Token(shy), Token('b'), Eof()).Compile();
        Assert.That(softHyphenBreaks.Parse("a" + shy + "b").Success, Is.True,
            "soft hyphen (U+00AD) is its own token");
    }

    // Primer4.md "Homoglyphs": restricting a rule to one script's
    // letters. A custom Latin set rejects Cyrillic а (U+0430) but
    // accepts Latin a (U+0061).
    [Test]
    public void Homoglyph_LatinLetters_set_rejects_Cyrillic_a()
    {
        var latinLetters =
            (TokenSet.Ascii.Letters | TokenSet.Range(new System.Text.Rune(0x00C0), new System.Text.Rune(0x00FF)))
            & TokenSet.Letters;

        var rule = OneOrMore(OneOf(latinLetters)).Compile();

        Assert.That(rule.Parse("apple").Success, Is.True);
        Assert.That(rule.Parse($"Cyrillic{UnicodeExamples.CyrillicSmallAGrapheme}").Success, Is.False,
            $"U+0430 Cyrillic '{UnicodeExamples.CyrillicSmallAGrapheme}' is rejected by LatinLetters");

        // The set is documented as "Latin-1 Supplement letters", so it must
        // reject the two non-letters that sit inside the U+00C0..U+00FF block:
        // U+00D7 MULTIPLICATION SIGN and U+00F7 DIVISION SIGN (both Sm). A
        // bare Range(0x00C0, 0x00FF) wrongly admits them. Intersecting with
        // TokenSet.Letters drops them.
        string timesInput = Canary("a×b", "ASCII a, U+00D7 MULTIPLICATION SIGN, ASCII b", 0x61, 0xD7, 0x62);
        string divideInput = Canary("a÷b", "ASCII a, U+00F7 DIVISION SIGN, ASCII b", 0x61, 0xF7, 0x62);
        Assert.That(rule.Parse(timesInput).Success, Is.False,
            "U+00D7 MULTIPLICATION SIGN is not a letter and must not be in LatinLetters");
        Assert.That(rule.Parse(divideInput).Success, Is.False,
            "U+00F7 DIVISION SIGN is not a letter and must not be in LatinLetters");
    }

    // Primer4.md "Homoglyphs": a single-script letter set built as a
    // confusable-resistant identifier character class, so it must reject
    // the non-letters that sit inside the U+0370..U+03FF Greek and Coptic
    // block. U+037E GREEK QUESTION MARK (Po) renders as ';' and U+0387
    // GREEK ANO TELEIA (Po) renders as '·': both are punctuation
    // confusables, exactly the kind of character that section promises to
    // keep out of identifiers. A naive Range(0x0370, 0x03FF) admits them.
    [Test]
    public void Homoglyph_Greek_set_rejects_non_letters()
    {
        var greek =
            TokenSet.Range(new System.Text.Rune(0x0370), new System.Text.Rune(0x03FF)) & TokenSet.Letters;

        var rule = OneOrMore(OneOf(greek)).Compile();

        // Greek letters are still accepted.
        Assert.That(rule.Parse(UnicodeExamples.GreekKalimeraIdentifier).Success, Is.True, "Greek letters");

        // The non-letters parked in the Greek and Coptic block must be
        // rejected. Built from the scalar value directly so no source-editing
        // tool can silently swap U+037E for an ASCII ';' lookalike.
        string questionMark = new System.Text.Rune(0x037E).ToString(); // GREEK QUESTION MARK (looks like ';')
        string anoTeleia = new System.Text.Rune(0x0387).ToString();    // GREEK ANO TELEIA (looks like '·')
        Assert.That(rule.Parse(questionMark).Success, Is.False,
            "U+037E GREEK QUESTION MARK is punctuation, not a letter, and must not be in the Greek set");
        Assert.That(rule.Parse(anoTeleia).Success, Is.False,
            "U+0387 GREEK ANO TELEIA is punctuation, not a letter, and must not be in the Greek set");
    }

    // Primer4.md "Invisible characters" (the variation-selector entries
    // in the widened Invisibles set): stripping U+FE00..U+FE0F before
    // parsing removes the emoji-style variation selector that would
    // otherwise make exact string matching fail.
    [Test]
    public void Variation_selector_strip_recipe()
    {
        string redHeart = Canary("❤️", "heavy black heart + variation selector-16", 0x2764, 0xFE0F); // ❤️ (heart + VS-16)
        string textHeart = UnicodeExamples.HeavyBlackHeartGrapheme;       // ❤ (heart only)

        var grammar = And(Literal(textHeart), Eof()).Compile();

        Assert.That(grammar.Parse(redHeart).Success, Is.False,
            "VS-16 makes the input differ from the variation-free literal");

        var cleaned = string.Concat(redHeart.EnumerateRunes()
            .Where(r => !(r.Value >= 0xFE00 && r.Value <= 0xFE0F)
                     && !(r.Value >= 0xE0100 && r.Value <= 0xE01EF)));

        Assert.That(grammar.Parse(cleaned).Success, Is.True);
    }

    // "CRLF Line Endings": doc claim: Token('\n') DOESN'T match
    // a CRLF grapheme. Use Or(Literal("\r\n"), OneOf(...)) instead.
    [Test]
    public void CRLF_token_lf_does_not_match()
    {
        var lfOnly = And(Literal("a"), Token('\n'), Literal("b"), Eof()).Compile();
        // CRLF input: the lexer reads "\r\n" as one grapheme, and
        // Token('\n') compares to a single-rune \n, so the grapheme
        // doesn't match.
        Assert.That(lfOnly.Parse("a\r\nb").Success, Is.False);
        Assert.That(lfOnly.Parse("a\nb").Success, Is.True);
    }

    // "CRLF Line Endings" / fix recipe: the built-in EndOfLine() tries
    // the CRLF pair as a unit before the single-rune terminators, so
    // one rule covers LF, CR, and CRLF inputs.
    [Test]
    public void CRLF_end_of_line_recipe_handles_all_three()
    {
        var grammar = And(Literal("a"), EndOfLine(), Literal("b"), Eof()).Compile();

        Assert.That(grammar.Parse("a\r\nb").Success, Is.True);
        Assert.That(grammar.Parse("a\nb").Success, Is.True);
        Assert.That(grammar.Parse("a\rb").Success, Is.True);
    }

    // "CRLF Line Endings" / whitespace recipes: AnyWhitespace() spans a
    // CRLF because it composes EndOfLine() first, while
    // InlineWhitespace() rejects every line terminator including the
    // CRLF pair.
    [Test]
    public void CRLF_whitespace_recipes_split_on_line_terminators()
    {
        var acrossLines = And(Literal("a"), AnyWhitespace(), Literal("b"), Eof()).Compile();
        Assert.That(acrossLines.Parse("a \r\n b").Success, Is.True);

        var sameLine = And(Literal("a"), InlineWhitespace(), Literal("b"), Eof()).Compile();
        Assert.That(sameLine.Parse("a b").Success, Is.True);
        Assert.That(sameLine.Parse("a\r\nb").Success, Is.False,
            "InlineWhitespace rejects line terminators, including the CRLF pair");
    }

    // "CRLF Line Endings" / line-comment recipe: the rule-based stop
    // Not(EndOfLine()) refuses the CRLF token that a single-rune
    // NoneOf set would silently eat, and eofIsEol: true covers a
    // comment at end of input.
    [Test]
    public void CRLF_line_comment_recipe()
    {
        var lineComment = And(
            Token('%'),
            ZeroOrMore(And(Not(EndOfLine()), AnyToken())),
            EndOfLine(eofIsEol: true)
        ).Compile();

        Assert.That(lineComment.Parse("% comment\r\n").Success, Is.True);
        Assert.That(lineComment.Parse("% comment\n").Success, Is.True);
        Assert.That(lineComment.Parse("% comment").Success, Is.True,
            "eofIsEol: true covers comment-at-end-of-input");
    }
}
