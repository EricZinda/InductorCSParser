using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests.DocExamples;

// Verifies the runnable code examples in docs/UnicodeGotchas.md.
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
        Assert.That(name.Parse("café").Success, Is.True);
        Assert.That(name.Parse("καλημέρα").Success, Is.True);
        Assert.That(name.Parse("ℼ").Success, Is.True,
            "U+2118 / nearby script-letter additions per UAX #31");

        Assert.That(name.Parse("2foo").Success, Is.False);
        Assert.That(name.Parse("_foo").Success, Is.False,
            "underscore is not in strict XID_Start");
        Assert.That(name.Parse("ﷺ").Success, Is.False,
            "Arabic ligature U+FDFA, NFKC-unstable exclusion");
    }

    // "Identifier Matching" / NFC equivalence (UAX #31 R4): "café" with
    // precomposed é (U+00E9) and "café" as e+combining-acute (U+0301)
    // both parse the same way under default FormC normalization.
    [Test]
    public void Identifier_normalizes_precomposed_and_decomposed_forms()
    {
        var name = Identifier().Compile();

        const string precomposed = "café";   // single rune é
        const string decomposed = "café"; // e + combining acute

        Assert.That(name.Parse(precomposed).Success, Is.True);
        Assert.That(name.Parse(decomposed).Success, Is.True);
    }

    // "Identifier Matching": works on Devanagari, Thai with SARA AM,
    // Greek under default GraphemeLexer.
    [Test]
    public void Identifier_works_on_multi_script_input()
    {
        var name = Identifier().Compile();

        Assert.That(name.Parse("हिन्दी").Success, Is.True, "Devanagari");
        Assert.That(name.Parse("กำ").Success, Is.True, "Thai with SARA AM");
        Assert.That(name.Parse("καλημέρα").Success, Is.True, "Greek");
    }

    // "WithinGrapheme: general-purpose sub-grapheme matching": the
    // ASCII-only-letter example. Accepts any grapheme whose runes are
    // all ASCII letters. Rejects "é" (not ASCII) and decomposed "é"
    // (multi-rune, even though both runes are letters).
    [Test]
    public void WithinGrapheme_ascii_only_letter_example()
    {
        var asciiOnlyLetter = WithinGrapheme(OneOf(RuneSet.Ascii.Letters))
            .Compile();

        Assert.That(asciiOnlyLetter.Parse("a").Success, Is.True);
        Assert.That(asciiOnlyLetter.Parse("Z").Success, Is.True);
        Assert.That(asciiOnlyLetter.Parse("é").Success, Is.False,
            "precomposed é is not ASCII");
    }

    // "Matching specific languages" / "Python 3 identifiers" recipe.
    // Adds underscore to start, NFKC normalization.
    [Test]
    public void Python3_identifier_recipe()
    {
        var python = Identifier(extraStartRunes: RuneSet.Runes("_")).Compile();

        var result = python.Parse("_foo", new ParseOptions
        {
            NormalizeInput = NormalizationForm.FormKC,
        });
        Assert.That(result.Success, Is.True);
    }

    // "Case-Insensitive Matching Beyond ASCII" — the LiteralIgnoreAsciiCase
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
        var grammar = AllOf(Literal("function"), Eof()).Compile();

        const string bomPlusKeyword = "﻿function";
        Assert.That(grammar.Parse(bomPlusKeyword).Success, Is.False,
            "BOM at start blocks the keyword match");

        var cleaned = bomPlusKeyword.TrimStart('﻿');
        Assert.That(grammar.Parse(cleaned).Success, Is.True);
    }

    // "Zero-Width and Invisible Format Characters": stripping them
    // before parsing. Soft hyphen (U+00AD) inside "ap­ple" makes
    // it not match Literal("apple") until the soft hyphen is stripped.
    [Test]
    public void Invisible_format_character_strip_recipe()
    {
        var grammar = AllOf(Literal("apple"), Eof()).Compile();

        const string withSoftHyphen = "ap­ple";
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

    // "Homoglyph Confusables": the LatinLetters set rejects Cyrillic а
    // (U+0430) but accepts Latin a (U+0061).
    [Test]
    public void Homoglyph_LatinLetters_set_rejects_Cyrillic_a()
    {
        var latinLetters =
            RuneSet.Ascii.Letters |
            RuneSet.Range(new System.Text.Rune(0x00C0), new System.Text.Rune(0x00FF));

        var rule = OneOrMore(OneOf(latinLetters)).Compile();

        Assert.That(rule.Parse("apple").Success, Is.True);
        Assert.That(rule.Parse("Cyrillicа").Success, Is.False,
            "U+0430 Cyrillic 'а' is rejected by LatinLetters");
    }

    // "Variation Selectors": stripping U+FE00..U+FE0F before parsing
    // removes the emoji-style variation selector that would otherwise
    // make byte-equality fail.
    [Test]
    public void Variation_selector_strip_recipe()
    {
        const string redHeart = "❤️"; // ❤️ (heart + VS-16)
        const string textHeart = "❤";       // ❤ (heart only)

        var grammar = AllOf(Literal(textHeart), Eof()).Compile();

        Assert.That(grammar.Parse(redHeart).Success, Is.False,
            "VS-16 makes the input differ from the variation-free literal");

        var cleaned = string.Concat(redHeart.EnumerateRunes()
            .Where(r => !(r.Value >= 0xFE00 && r.Value <= 0xFE0F)
                     && !(r.Value >= 0xE0100 && r.Value <= 0xE01EF)));

        Assert.That(grammar.Parse(cleaned).Success, Is.True);
    }

    // "CRLF Under GraphemeLexer": doc claim: Token('\n') does NOT match
    // a CRLF grapheme. Use FirstOf(Literal("\r\n"), OneOf(...)) instead.
    [Test]
    public void CRLF_token_lf_does_not_match_under_graphemelexer()
    {
        var lfOnly = AllOf(Literal("a"), Token('\n'), Literal("b"), Eof()).Compile();
        // CRLF input: under GraphemeLexer, "\r\n" is one grapheme, and
        // Token('\n') compares to a single-rune \n, so the grapheme
        // doesn't match.
        Assert.That(lfOnly.Parse("a\r\nb").Success, Is.False);
        Assert.That(lfOnly.Parse("a\nb").Success, Is.True);
    }

    // "CRLF Under GraphemeLexer" / fix recipe: a LineBreak rule that
    // accepts LF, CR, or CRLF.
    [Test]
    public void CRLF_line_break_recipe_handles_all_three()
    {
        var lineBreak = FirstOf(
            Literal("\r\n"),
            OneOf(RuneSet.Runes("\r\n"))
        );
        var grammar = AllOf(Literal("a"), lineBreak, Literal("b"), Eof()).Compile();

        Assert.That(grammar.Parse("a\r\nb").Success, Is.True);
        Assert.That(grammar.Parse("a\nb").Success, Is.True);
        Assert.That(grammar.Parse("a\rb").Success, Is.True);
    }

    // "CRLF Under GraphemeLexer" / line-comment recipe:
    //   ZeroOrMore(AllOf(Not(LineBreak), AnyToken())) stops just before
    //   any LineBreak (including CRLF) and the trailing LineBreak/EOF
    //   completes the comment.
    [Test]
    public void CRLF_line_comment_recipe()
    {
        var lineBreak = FirstOf(
            Literal("\r\n"),
            OneOf(RuneSet.Runes("\r\n"))
        );

        var lineComment = AllOf(
            Token('%'),
            ZeroOrMore(AllOf(Not(lineBreak), AnyToken())),
            FirstOf(OneOrMore(lineBreak), Eof())
        ).Compile();

        Assert.That(lineComment.Parse("% comment\r\n").Success, Is.True);
        Assert.That(lineComment.Parse("% comment\n").Success, Is.True);
        Assert.That(lineComment.Parse("% comment").Success, Is.True,
            "Eof alternative covers comment-at-end-of-input");
    }
}
