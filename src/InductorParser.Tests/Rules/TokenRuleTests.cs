using System;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;
using static InductorParser.Tests.UnicodeExamples;

namespace InductorParser.Tests;

[TestFixture]
public class TokenRuleTests
{
    [Test]
    public void Token_string_with_ASCII_grapheme_works()
    {
        var rule = Token("a");
        var result = rule.Parse("a");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Token_string_with_supplementary_rune_works()
    {
        // Guitar emoji is one rune and one grapheme, represented in UTF-16
        // as a surrogate pair. The "single rune" fast path in TokenRule
        // should set the id to GuitarRune.
        var rule = Token(GuitarGrapheme);
        var result = rule.Parse(GuitarGrapheme);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(rule.Id.Value, Is.EqualTo(GuitarRune));
    }

    // Multi-rune grapheme tests. LatinEAcuteGrapheme (e + combining acute)
    // segments the same way on every runtime including legacy StringInfo,
    // because the base+combining-mark rule predates UAX #29. The known-broken
    // multi-rune categories (skin tone, ZWJ, regional indicator, SARA AM)
    // are exercised separately at the bottom of the file under #if.
    [Test]
    public void Token_string_with_multi_rune_grapheme_matches_under_grapheme_lexer()
    {
        // LatinEAcuteGrapheme is one grapheme made of two runes (2 UTF-16
        // chars). Under GraphemeLexer this is one token, so TokenRule reads
        // one token and compares the whole expected.
        //
        // NormalizeInput = null because the default NFC would rewrite the
        // decomposed "e\u0301" input to the precomposed "\u00E9" before
        // the lexer saw it, and the whole point of this test is that the
        // rule and input sit in the same decomposed form at match time.
        // NormalizationTests covers the NFC-on behavior separately.
        var rule = Token(LatinEAcuteGrapheme);
        var result = rule.Parse(LatinEAcuteGrapheme,
            new ParseOptions { NormalizeInput = null });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Token_string_with_multi_rune_grapheme_matches_under_rune_lexer()
    {
        // Same input as above but using the rune lexer. Here the lexer
        // produces two rune tokens, so TokenRule reads both and compares
        // each in lockstep against the corresponding section of the expected.
        var rule = Token(LatinEAcuteGrapheme);
        var result = rule.Parse(LatinEAcuteGrapheme,
            new ParseOptions { InputUnit = InputUnit.Rune, NormalizeInput = null });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Token_string_with_more_than_one_grapheme_throws_at_construction()
    {
        var ex = Assert.Throws<ArgumentException>(() => Token("ab"));
        Assert.That(ex!.Message, Does.Contain("one grapheme"));
    }

    [Test]
    public void Token_string_empty_throws()
    {
        Assert.Throws<ArgumentException>(() => Token(""));
    }

    [Test]
    public void Token_Rune_overload_matches_the_corresponding_grapheme()
    {
        // Token(Rune) is a convenience overload that funnels through the
        // string ctor. Rune's own ctor enforces validity, so this overload
        // doesn't need its own argument validation.
        var rule = Token(new System.Text.Rune('='));

        var result = rule.Parse("=");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Token_int_with_surrogate_throws()
    {
        // 0xD800 is a high-surrogate code unit, not a valid scalar value.
        Assert.Throws<ArgumentOutOfRangeException>(() => Token(0xD800));
    }

    [Test]
    public void Token_int_with_out_of_range_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Token(0x110000));
        Assert.Throws<ArgumentOutOfRangeException>(() => Token(-1));
    }

    [Test]
    public void Token_char_with_surrogate_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Token('\uD800'));
    }

    [Test]
    public void Token_string_match_failure_records_position_correctly()
    {
        // Multi-rune Token fails on the grapheme-sized token that didn't
        // match. Under the error-position principle the failing read's
        // pre-read position is 0.
        var rule = Token(GuitarGrapheme).WithError("expected guitar");
        var result = rule.Parse(MusicalKeyboardGrapheme);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected guitar"));
    }

    [Test]
    public void Token_mismatch_on_single_char_input_points_at_offender()
    {
        // The original off-by-one bug: Token('a').Parse("x") should NOT
        // report "Unexpected end of input" at offset 1. Under the error-
        // position principle the failing read's pre-read position (0) is
        // what gets recorded, and Token's WithError message surfaces.
        var rule = Token('a').WithError("need 'a'");
        var result = rule.Parse("x");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("need 'a'"));
    }

    [Test]
    public void Token_EOF_on_empty_input_points_at_zero()
    {
        // Legitimate EOF case: input is genuinely empty. Token('a') opens a
        // transaction at position 0 and the lexer immediately returns EOF.
        // The failing read's pre-read position is 0, so the WithError
        // message still surfaces at offset 0 even though it's EOF.
        var rule = Token('a').WithError("need 'a'");
        var result = rule.Parse("");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("need 'a'"));
    }

    [Test]
    public void Token_mismatch_after_successful_matches_points_at_first_bad_char()
    {
        // Composite context: Token('a') commits at offset 0, then Token('b')
        // runs at offset 1, reads 'x', and records at pre-read position 1.
        // This is the "points-at-offender-in-context" test: a buggy single-
        // rune Token that hard-coded 0 (or used the outer parse's start)
        // would still pass the zero-position tests above. This one proves
        // it's actually tracking the pre-read of its own read.
        var rule = AllOf(Token('a'),
                       Token('b').WithError("need a 'b'"));
        var result = rule.Parse("ax");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
        Assert.That(result.ErrorMessage, Is.EqualTo("need a 'b'"));
    }

    [Test]
    public void Token_int_boundary_values_are_validated_correctly()
    {
        // 0xD7FF is the last scalar before the surrogate block. 0xE000 is
        // the first after. Both should build fine.
        Assert.DoesNotThrow(() => Token(0xD7FF));
        Assert.DoesNotThrow(() => Token(0xE000));
        // 0x10FFFF is the last valid Unicode scalar. Also fine.
        Assert.DoesNotThrow(() => Token(0x10FFFF));
        // 0xDFFF is the last surrogate. Still invalid.
        Assert.Throws<ArgumentOutOfRangeException>(() => Token(0xDFFF));
    }

    [Test]
    public void Token_EOF_on_empty_input_falls_back_to_positional_message_without_WithError()
    {
        // When no WithError is set, BuildErrorMessage's fallback decides
        // what to say. Pos equals input.Length here, so it renders the
        // "Unexpected end of input" branch.
        var rule = Token('a');
        var result = rule.Parse("");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Does.StartWith("Unexpected end of input"));
    }

    // Position-reporting tests for multi-rune Token under RuneLexer. Same
    // reasoning as the multi-rune grapheme tests above: LatinEAcuteGrapheme
    // segments identically on every runtime, so these don't need a gate.
    [Test]
    public void Token_multi_rune_mismatch_on_first_token_reports_at_zero()
    {
        // Under RuneLexer, LatinEAcuteGrapheme tokenizes into two rune
        // tokens ('e' and U+0301). Input "xy" is two single-char tokens.
        // TokenRule's lockstep fails at the first iteration where tokenStart
        // is 0, so that's where the WithError message surfaces.
        var rule = Token(LatinEAcuteGrapheme).WithError("expected e-acute");
        var result = rule.Parse("xy", new ParseOptions { InputUnit = InputUnit.Rune });

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected e-acute"));
    }

    [Test]
    public void Token_multi_rune_mismatch_on_second_token_reports_at_second_token_start()
    {
        // Under RuneLexer, LatinEAcuteGrapheme tokenizes into two BMP rune
        // tokens of 1 char each. Input "ex" has the first rune matching
        // then diverges. Second iteration's pre-read position is 1, and
        // that's where the offender starts. Not 0 (whole-match start),
        // not 2 (post-read).
        var rule = Token(LatinEAcuteGrapheme).WithError("expected e-acute");
        var result = rule.Parse("ex",
            new ParseOptions { InputUnit = InputUnit.Rune });

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected e-acute"));
    }

    [Test]
    public void Token_trace_success_produces_expected_output()
    {
        var sink = NewSink();
        Token('a').Parse("a", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: 'a', Consumed: 1",
            "   SUCC | Token: found 'a'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Token_trace_failure_produces_expected_output()
    {
        var sink = NewSink();
        Token('a').Parse("x", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: 'x', Consumed: 1",
            "   FAIL | Token: found 'x', wanted 'a'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

#if !UNITY_INCLUDE_TESTS
    // Known-broken-on-legacy-StringInfo cases. Each test documents one
    // UAX #29 rule category that pre-.NET 5 / IL2CPP StringInfo doesn't
    // implement. Gated to net8.0 / CoreCLR because Token(...) rejects these
    // at construction on the legacy walker (it sees more than one grapheme
    // and throws). See docs/UnicodeGotchas.md "Pre-.NET 5 Grapheme
    // Segmentation" and backlog/r000.

    [Test]
    public void Token_with_skin_tone_modifier_sequence_matches_one_grapheme_on_uax29_runtime()
    {
        // Modifier sequence: base emoji + skin-tone modifier. UAX #29 rule
        // GB10/GB11. Two runes, one grapheme on UAX #29. Legacy splits.
        var rule = Token(SkinTonedWaveGrapheme);
        var result = rule.Parse(SkinTonedWaveGrapheme);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Token_with_zwj_emoji_sequence_matches_one_grapheme_on_uax29_runtime()
    {
        // ZWJ sequence: base + ZWJ + joiner + variation selector. UAX #29
        // rule GB11 with extended pictographic. Four runes, one grapheme on
        // UAX #29. Legacy splits at every ZWJ.
        var rule = Token(WomanShruggingGrapheme);
        var result = rule.Parse(WomanShruggingGrapheme);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Token_with_regional_indicator_pair_matches_one_grapheme_on_uax29_runtime()
    {
        // Regional indicator pair: two RI code points form one flag. UAX #29
        // rule GB12/GB13. Two runes, one grapheme on UAX #29. Legacy splits.
        var rule = Token(USFlagGrapheme);
        var result = rule.Parse(USFlagGrapheme);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Token_with_thai_sara_am_matches_one_grapheme_on_uax29_runtime()
    {
        // Thai SARA AM: consonant + SARA AM forms one extended grapheme
        // cluster. The canonical SpacingMark case from UAX #29 rule GB9a.
        // Two runes, one grapheme on UAX #29. Legacy splits.
        var rule = Token(ThaiKamGrapheme);
        var result = rule.Parse(ThaiKamGrapheme);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }
#endif

    [Test]
    public void Sealed_Token_rejects_Flatten()
    {
        var rule = Token('a');
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.Flatten(FlattenType.Preserve));
    }

    [Test]
    public void Sealed_Token_rejects_WithError()
    {
        var rule = Token('a');
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.WithError("late"));
    }

    [Test]
    public void Sealed_Token_rejects_As()
    {
        var rule = Token('a');
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.As("late"));
    }
}
