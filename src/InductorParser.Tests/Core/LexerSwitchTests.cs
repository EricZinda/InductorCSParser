using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;
using static InductorParser.Tests.UnicodeExamples;

namespace InductorParser.Tests;

[TestFixture]
public class LexerSwitchTests
{
    [Test]
    public void Default_lexer_is_grapheme()
    {
        // SkinTonedWaveGrapheme is one grapheme made of two runes (4 UTF-16
        // chars). Char(WavingHandRune) expects exactly the 2-char waving-hand
        // rune. Under the grapheme lexer the single token is 4 chars, which
        // CharRule's length check rejects. Under the rune lexer the first
        // token would be just the 2-char waving hand and the parse would
        // succeed. Asserting failure here pins that no-options parsing uses
        // the grapheme lexer.
        var rule = Char(WavingHandRune);

        var result = rule.Parse(SkinTonedWaveGrapheme);

        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void Rune_lexer_via_options_parses_ascii_the_same_way_as_grapheme()
    {
        // ASCII input has one UTF-16 char per rune per grapheme, so the two
        // lexers should produce identical token streams. Parse under both
        // and assert the observable tree is the same.
        var rule = OneOrMore(RuneIn(RuneSet.Letters));

        var graphemeResult = rule.Parse("hello");
        var runeResult = rule.Parse("hello", new ParseOptions { InputUnit = InputUnit.Rune });

        Assert.That(graphemeResult.Success, Is.True, graphemeResult.ErrorMessage);
        Assert.That(runeResult.Success, Is.True, runeResult.ErrorMessage);
        Assert.That(graphemeResult.Tree!.ToString(), Is.EqualTo("hello"));
        Assert.That(runeResult.Tree!.ToString(), Is.EqualTo("hello"));
    }

    [Test]
    public void Char_with_supplementary_codepoint_matches_under_grapheme_lexer()
    {
        // Guitar is a single grapheme but two UTF-16 chars (surrogate pair).
        var rule = Char(GuitarRune);

        var result = rule.Parse(GuitarGrapheme);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void RuneIn_rejects_multi_rune_grapheme_under_grapheme_lexer()
    {
        // SkinTonedWaveGrapheme = waving hand + medium skin tone modifier.
        // Under the grapheme lexer this is one token spanning two runes, so
        // RuneIn (defined as "the token is exactly one rune in the class")
        // must fail even though it would match the first Rune of it. 
        //
        // WavingHandRune is put in the allowed set so that the reason for
        // failure is specifically "token spans two runes", not "first rune
        // isn't in the set". 
        var rule = OneOrMore(RuneIn(RuneSet.Single(WavingHandRune)));

        var result = rule.Parse(SkinTonedWaveGrapheme);

        Assert.That(result.Success, Is.False);
        // RuneIn records at transaction.StartPosition (0) when the multi-
        // rune grapheme token fails membership. OneOrMore's first inner
        // failed so it also records at offset 0. Deepest-wins lands on 0.
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
    }

    [Test]
    public void Same_input_under_rune_lexer_consumes_both_runes_separately()
    {
        // Same emoji sequence as above. Under the rune lexer, the waving
        // hand and the skin-tone modifier are two separate tokens, so a
        // RuneIn that allows both code points individually now succeeds.
        var allowed = RuneSet.Single(WavingHandRune) | RuneSet.Single(MediumSkinToneRune);
        var rule = OneOrMore(RuneIn(allowed));

        var result = rule.Parse(SkinTonedWaveGrapheme,
            new ParseOptions { InputUnit = InputUnit.Rune });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }
}
