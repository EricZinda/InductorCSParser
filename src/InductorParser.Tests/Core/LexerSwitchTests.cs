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
        // chars). Token(WavingHandRune) expects exactly the 2-Token waving-hand
        // rune. Under the grapheme lexer the single token is 4 chars, which
        // TokenRule's length check rejects. Under the rune lexer the first
        // token would be just the 2-Token waving hand and the parse would
        // succeed. Asserting failure here verifies that no-options parsing
        // uses the grapheme lexer.
        var rule = Token(WavingHandRune);

        var result = rule.Parse(SkinTonedWaveGrapheme);

        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void Rune_lexer_via_options_parses_ascii_the_same_way_as_grapheme()
    {
        // ASCII input has one UTF-16 Token per rune per grapheme, so the two
        // lexers should produce identical token streams. Parse under both
        // and assert the observable tree is the same.
        var rule = OneOrMore(OneOf(RuneSet.Letters));

        var graphemeResult = rule.Parse("hello");
        var runeResult = rule.Parse("hello", new ParseOptions { InputUnit = InputUnit.Rune });

        Assert.That(graphemeResult.Success, Is.True, graphemeResult.ErrorMessage);
        Assert.That(runeResult.Success, Is.True, runeResult.ErrorMessage);
        Assert.That(graphemeResult.ToString(), Is.EqualTo("hello"));
        Assert.That(runeResult.ToString(), Is.EqualTo("hello"));
    }

    [Test]
    public void Token_with_supplementary_codepoint_matches_under_grapheme_lexer()
    {
        // Guitar is a single grapheme but two UTF-16 chars (surrogate pair).
        var rule = Token(GuitarRune);

        var result = rule.Parse(GuitarGrapheme);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

#if !UNITY_INCLUDE_TESTS
    // CoreCLR-only: under netstandard2.1 / IL2CPP the grapheme lexer
    // segments SkinTonedWaveGrapheme as two graphemes instead of one and
    // consumes both, so ErrorCharIndex ends up at 2 rather than 0.
    // Tracked by backlog/xlll-vendor-a-uax-#29-grapheme-cluster-implementation.md.
    [Test]
    public void OneOf_rejects_multi_rune_grapheme_under_grapheme_lexer()
    {
        // SkinTonedWaveGrapheme = waving hand + medium skin tone modifier.
        // Under the grapheme lexer this is one token spanning two runes, so
        // OneOf (defined as "the token is exactly one rune in the class")
        // must fail even though it would match the first Rune of it.
        //
        // WavingHandRune is put in the allowed set so that the reason for
        // failure is specifically "token spans two runes", not "first rune
        // isn't in the set".
        var rule = OneOrMore(OneOf(RuneSet.Single(WavingHandRune)));

        var result = rule.Parse(SkinTonedWaveGrapheme);

        Assert.That(result.Success, Is.False);
        // OneOf records at transaction.StartPosition (0) when the multi-
        // rune grapheme token fails membership. OneOrMore's first inner
        // failed so it also records at offset 0. Deepest-wins lands on 0.
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
    }
#endif

    [Test]
    public void Same_input_under_rune_lexer_consumes_both_runes_separately()
    {
        // Same emoji sequence as above. Under the rune lexer, the waving
        // hand and the skin-tone modifier are two separate tokens, so a
        // OneOf that allows both code points individually now succeeds.
        var allowed = RuneSet.Single(WavingHandRune) | RuneSet.Single(MediumSkinToneRune);
        var rule = OneOrMore(OneOf(allowed));

        var result = rule.Parse(SkinTonedWaveGrapheme,
            new ParseOptions { InputUnit = InputUnit.Rune });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }
}
