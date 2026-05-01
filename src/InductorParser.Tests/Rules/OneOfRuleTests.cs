using System;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;
using static InductorParser.Tests.UnicodeExamples;

namespace InductorParser.Tests;

[TestFixture]
public class OneOfRuleTests
{
    [Test]
    public void OneOf_matches_a_letter_and_returns_a_single_rune_symbol()
    {
        var rule = OneOf(TokenSet.Letters);
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("a"));
    }

    [Test]
    public void OneOf_mismatch_without_WithError_falls_back_to_positional_message()
    {
        // No WithError anywhere, so OneOf records a null message at offset
        // 0 and BuildErrorMessage's positional fallback decides what to say.
        var rule = OneOrMore(OneOf(TokenSet.Letters));
        var result = rule.Parse("1abc");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Does.StartWith("Parse failed at offset 0"));
    }

    [Test]
    public void OneOf_EOF_on_empty_input_points_at_zero()
    {
        // OneOrMore requires at least one letter. Empty input can't satisfy
        // that. OneOf sees EOF on its first read and records at its pre-
        // read position 0 with its WithError message.
        var rule = OneOrMore(OneOf(TokenSet.Letters).WithError("need a letter"));

        var result = rule.Parse("");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("need a letter"));
    }

    [Test]
    public void OneOf_mismatch_at_start_points_at_offender()
    {
        // '1' is at offset 0. Not a letter. OneOf records its WithError
        // message at pre-read position 0.
        var rule = OneOrMore(OneOf(TokenSet.Letters).WithError("need a letter"));

        var result = rule.Parse("1abc");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("need a letter"));
    }

    [Test]
    public void OneOf_mismatch_after_successful_matches_points_at_first_bad_char()
    {
        // OneOrMore(Letters) commits "abc" up to offset 3. Then Token(';')
        // runs at offset 3, reads '1', and records its own WithError at
        // pre-read offset 3. That's deeper than the letter's WithError
        // (which is at offset 3 too, from the OneOrMore's terminating
        // attempt, but recorded first). First-writer at equal depth wins.
        //
        // To make the test unambiguous we only put a WithError on Token(';')
        // so there's no contention.
        var rule = AllOf(OneOrMore(OneOf(TokenSet.Letters)),
                       Token(';').WithError("expected ';'"));

        var result = rule.Parse("abc1");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected ';'"));
    }

    [Test]
    [RecursiveEngineOnly]
    public void OneOf_trace_success_produces_expected_output()
    {
        var sink = NewSink();
        OneOf(TokenSet.Ascii.Letters).Parse("x", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: 'x', Consumed: 1",
            "   SUCC | OneOf: found 'x', wanted one of '[A-Z,a-z]'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    [RecursiveEngineOnly]
    public void OneOf_trace_failure_produces_expected_output()
    {
        var sink = NewSink();
        OneOf(TokenSet.Ascii.Letters).Parse("1", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: '1', Consumed: 1",
            "   FAIL | OneOf: found '1', wanted one of '[A-Z,a-z]'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Sealed_OneOf_rejects_Flatten()
    {
        var rule = OneOf("abc");
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.Flatten(FlattenType.Preserve));
    }

    [Test]
    public void Sealed_OneOf_rejects_WithError()
    {
        var rule = OneOf("abc");
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.WithError("late"));
    }

    [Test]
    public void Sealed_OneOf_rejects_As()
    {
        var rule = OneOf("abc");
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.As("late"));
    }

    // Multi-rune grapheme support -------------------------------------------

    [Test]
    public void OneOf_matches_a_multi_rune_grapheme_under_grapheme_lexer()
    {
        // OneOf(set) where set has multi-rune entries: the lexer
        // hands back the whole grapheme as one token with RuneValue
        // == -1, and OneOf uses the multi-rune-array path to match it.
        // NormalizeInput stays default; the test inputs aren't
        // affected by NFC.
        var rule = OneOf(TokenSet.Runes(USFlagGrapheme + WomanShruggingGrapheme));

        Assert.That(rule.Parse(USFlagGrapheme).Success, Is.True);
        Assert.That(rule.Parse(WomanShruggingGrapheme).Success, Is.True);
        // A different multi-rune grapheme isn't a member.
        Assert.That(rule.Parse(SkinTonedWaveGrapheme).Success, Is.False);
        // EOF still fails.
        Assert.That(rule.Parse("").Success, Is.False);
    }

    [Test]
    public void OneOf_mixed_set_matches_both_letters_and_a_multi_rune_Token()
    {
        // Letters | Runes(USFlag) is the canonical mixed set: a
        // big rune-only class plus a single multi-rune entry. OneOf
        // uses the rune intervals for letter tokens and the
        // multi-rune array for the flag token.
        var rule = OneOf(TokenSet.Letters | TokenSet.Runes(USFlagGrapheme));

        Assert.That(rule.Parse("a").Success, Is.True);
        Assert.That(rule.Parse(USFlagGrapheme).Success, Is.True);
        // A digit isn't a letter and isn't the flag.
        Assert.That(rule.Parse("1").Success, Is.False);
    }

}
