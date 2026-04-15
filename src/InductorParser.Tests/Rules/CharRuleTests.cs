using System;
using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;
using static InductorParser.Tests.UnicodeExamples;

namespace InductorParser.Tests;

[TestFixture]
public class CharRuleTests
{
    [Test]
    public void Char_string_with_ASCII_grapheme_works()
    {
        var rule = Char("a");
        var result = rule.Parse("a");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Char_string_with_supplementary_rune_works()
    {
        // Guitar emoji is one rune and one grapheme, represented in UTF-16
        // as a surrogate pair. The "single rune" fast path in CharRule
        // should pin the id to GuitarRune.
        var rule = Char(GuitarGrapheme);
        var result = rule.Parse(GuitarGrapheme);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(rule.Id.Value, Is.EqualTo(GuitarRune));
    }

#if !UNITY_INCLUDE_TESTS
    // Multi-rune grapheme tests are CoreCLR-only. Under netstandard2.1 /
    // IL2CPP the BCL's grapheme segmentation splits SkinTonedWaveGrapheme
    // into two graphemes, not one, so Char(...) rejects it at construction
    // before the test body runs. Tracked by backlog/r000.
    [Test]
    public void Char_string_with_multi_rune_grapheme_matches_under_grapheme_lexer()
    {
        // SkinTonedWaveGrapheme is one grapheme made of two runes (4 UTF-16
        // chars). Under GraphemeLexer this is one token, so CharRule reads
        // one token and compares the whole expected.
        var rule = Char(SkinTonedWaveGrapheme);
        var result = rule.Parse(SkinTonedWaveGrapheme);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Char_string_with_multi_rune_grapheme_matches_under_rune_lexer()
    {
        // Same input as above but using the rune lexer. Here the lexer
        // produces two rune tokens, so CharRule reads both and compares
        // each in lockstep against the expected slices.
        var rule = Char(SkinTonedWaveGrapheme);
        var result = rule.Parse(SkinTonedWaveGrapheme,
            new ParseOptions { InputUnit = InputUnit.Rune });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }
#endif

    [Test]
    public void Char_string_with_more_than_one_grapheme_throws_at_construction()
    {
        var ex = Assert.Throws<ArgumentException>(() => Char("ab"));
        Assert.That(ex!.Message, Does.Contain("one grapheme"));
    }

    [Test]
    public void Char_string_empty_throws()
    {
        Assert.Throws<ArgumentException>(() => Char(""));
    }

    [Test]
    public void Char_Rune_overload_matches_the_corresponding_grapheme()
    {
        // Char(Rune) is a convenience overload that funnels through the
        // string ctor. Rune's own ctor enforces validity, so this overload
        // doesn't need its own argument validation.
        var rule = Char(new System.Text.Rune('='));

        var result = rule.Parse("=");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Char_int_with_surrogate_throws()
    {
        // 0xD800 is a high-surrogate code unit, not a valid scalar value.
        Assert.Throws<ArgumentOutOfRangeException>(() => Char(0xD800));
    }

    [Test]
    public void Char_int_with_out_of_range_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Char(0x110000));
        Assert.Throws<ArgumentOutOfRangeException>(() => Char(-1));
    }

    [Test]
    public void Char_char_with_surrogate_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Char('\uD800'));
    }

    [Test]
    public void Char_string_match_failure_records_position_correctly()
    {
        // Multi-rune Char fails on the grapheme-sized token that didn't
        // match. Under the error-position principle the failing read's
        // pre-read position is 0.
        var rule = Char(GuitarGrapheme).WithError("expected guitar");
        var result = rule.Parse(MusicalKeyboardGrapheme);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected guitar"));
    }

    [Test]
    public void Char_mismatch_on_single_char_input_points_at_offender()
    {
        // The original off-by-one bug: Char('a').Parse("x") should NOT
        // report "Unexpected end of input" at offset 1. Under the error-
        // position principle the failing read's pre-read position (0) is
        // what gets recorded, and Char's WithError message surfaces.
        var rule = Char('a').WithError("need 'a'");
        var result = rule.Parse("x");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("need 'a'"));
    }

    [Test]
    public void Char_EOF_on_empty_input_points_at_zero()
    {
        // Legitimate EOF case: input is genuinely empty. Char('a') opens a
        // transaction at position 0 and the lexer immediately returns EOF.
        // The failing read's pre-read position is 0, so the WithError
        // message still surfaces at offset 0 even though it's EOF.
        var rule = Char('a').WithError("need 'a'");
        var result = rule.Parse("");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("need 'a'"));
    }

    [Test]
    public void Char_mismatch_after_successful_matches_points_at_first_bad_char()
    {
        // Composite context: Char('a') commits at offset 0, then Char('b')
        // runs at offset 1, reads 'x', and records at pre-read position 1.
        // This is the "points-at-offender-in-context" test: a buggy single-
        // rune Char that hard-coded 0 (or used the outer parse's start)
        // would still pass the zero-position tests above. This one proves
        // it's actually tracking the pre-read of its own read.
        var rule = And(Char('a'),
                       Char('b').WithError("need a 'b'"));
        var result = rule.Parse("ax");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
        Assert.That(result.ErrorMessage, Is.EqualTo("need a 'b'"));
    }

    [Test]
    public void Char_int_boundary_values_are_validated_correctly()
    {
        // 0xD7FF is the last scalar before the surrogate block; 0xE000 is
        // the first after. Both should build fine.
        Assert.DoesNotThrow(() => Char(0xD7FF));
        Assert.DoesNotThrow(() => Char(0xE000));
        // 0x10FFFF is the last valid Unicode scalar. Also fine.
        Assert.DoesNotThrow(() => Char(0x10FFFF));
        // 0xDFFF is the last surrogate; still invalid.
        Assert.Throws<ArgumentOutOfRangeException>(() => Char(0xDFFF));
    }

    [Test]
    public void Char_EOF_on_empty_input_falls_back_to_positional_message_without_WithError()
    {
        // When no WithError is set, BuildErrorMessage's fallback decides
        // what to say. Pos equals input.Length here, so it renders the
        // "Unexpected end of input" branch.
        var rule = Char('a');
        var result = rule.Parse("");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Does.StartWith("Unexpected end of input"));
    }

#if !UNITY_INCLUDE_TESTS
    // Same CoreCLR-only rationale as the multi-rune grapheme tests above:
    // Char(SkinTonedWaveGrapheme) throws at construction on netstandard2.1 /
    // IL2CPP because that runtime's grapheme segmentation sees two
    // graphemes where net8.0 sees one.
    [Test]
    public void Char_multi_rune_mismatch_on_first_token_reports_at_zero()
    {
        // Under RuneLexer, SkinTonedWaveGrapheme tokenizes into two rune
        // tokens. Input "xy" is two single-char tokens. CharRule's lockstep
        // fails at the first iteration where tokenStart is 0, so that's
        // where the WithError message surfaces.
        var rule = Char(SkinTonedWaveGrapheme).WithError("expected wave");
        var result = rule.Parse("xy", new ParseOptions { InputUnit = InputUnit.Rune });

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected wave"));
    }

    [Test]
    public void Char_multi_rune_mismatch_on_second_token_reports_at_second_token_start()
    {
        // Under RuneLexer, SkinTonedWaveGrapheme tokenizes into two rune
        // tokens of 2 chars each. Input (WavingHandGrapheme + "xy") has the
        // first rune matching then diverges. Second iteration's pre-read
        // position is 2, and that's where the offender starts. Not 0
        // (whole-match start), not 4 (post-read).
        var rule = Char(SkinTonedWaveGrapheme).WithError("expected wave");
        var result = rule.Parse(WavingHandGrapheme + "xy",
            new ParseOptions { InputUnit = InputUnit.Rune });

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected wave"));
    }
#endif

    [Test]
    public void Char_trace_success_produces_expected_output()
    {
        var sink = NewSink();
        Char('a').Parse("a", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: 'a', Consumed: 1",
            "   SUCC | Char: found 'a'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Char_trace_failure_produces_expected_output()
    {
        var sink = NewSink();
        Char('a').Parse("x", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: 'x', Consumed: 1",
            "   FAIL | Char: found 'x', wanted 'a'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }
}
