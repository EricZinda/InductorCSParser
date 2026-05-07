using System;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;
using static InductorParser.Tests.UnicodeExamples;

namespace InductorParser.Tests;

[TestFixture]
public class ScanWhileRuleTests
{
    [Test]
    public void ScanWhile_matches_a_run_into_one_leaf()
    {
        var result = ScanWhile(TokenSet.Ascii.Letters).Parse("abcXYZ");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("abcXYZ"));
        Assert.That(result.Tree!.Children.Count, Is.EqualTo(0));
    }

    [Test]
    public void ScanWhile_stops_before_first_rune_outside_the_set()
    {
        var rule = And(ScanWhile(TokenSet.Ascii.Letters), Token('!'));

        var result = rule.Parse("abc!");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("abc"));
    }

    [Test]
    public void ScanWhile_requires_at_least_one_rune()
    {
        var result = ScanWhile(TokenSet.Ascii.Letters)
            .WithError("need a letter")
            .Parse("123");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("need a letter"));
    }

    [Test]
    public void ScanWhile_honors_minimum_count()
    {
        var result = ScanWhile(TokenSet.Ascii.Letters, minimumCount: 4)
            .WithError("need four letters")
            .Parse("abc!");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(result.ErrorMessage, Is.EqualTo("need four letters"));
    }

    [Test]
    public void ScanWhile_rejects_zero_minimum_count()
    {
        Assert.That(
            () => ScanWhile(TokenSet.Ascii.Letters, minimumCount: 0),
            Throws.TypeOf<System.ArgumentOutOfRangeException>());
    }

    [Test]
    public void ScanWhile_rejects_multi_rune_grapheme_under_grapheme_lexer()
    {
        var rule = ScanWhile(TokenSet.Single(WavingHandRune));

        var result = rule.Parse(SkinTonedWaveGrapheme);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
    }

    [Test]
    [RecursiveEngineOnly]
    public void ScanWhile_trace_success_produces_expected_output()
    {
        var sink = NewSink();
        ScanWhile(TokenSet.Ascii.Letters).Parse("abc",
            new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.AdvanceWhileRuneIn: 'a', Consumed: 1",
            "   Lexer.AdvanceWhileRuneIn: 'b', Consumed: 2",
            "   Lexer.AdvanceWhileRuneIn: 'c', Consumed: 3",
            "   SUCC | ScanWhile: count= 3, 3 chars, wanted one or more of '[A-Z,a-z]'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Sealed_ScanWhile_rejects_Flatten()
    {
        var rule = ScanWhile(TokenSet.Ascii.Letters);
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.Flatten(FlattenType.Preserve));
    }

    [Test]
    public void Sealed_ScanWhile_rejects_WithError()
    {
        var rule = ScanWhile(TokenSet.Ascii.Letters);
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.WithError("late"));
    }

    [Test]
    public void Sealed_ScanWhile_rejects_As()
    {
        var rule = ScanWhile(TokenSet.Ascii.Letters);
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.As("late"));
    }

    // Multi-rune grapheme support -------------------------------------------

    [Test]
    public void ScanWhile_with_multi_rune_set_consumes_a_run_of_graphemes()
    {
        // Set: { USFlag, WomanShrugging }. Input: USFlag + WomanShrugging.
        // ScanWhile should consume both emoji graphemes as one leaf.
        var rule = ScanWhile(TokenSet.Runes(USFlagGrapheme + WomanShruggingGrapheme));

        var result = rule.Parse(USFlagGrapheme + WomanShruggingGrapheme);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(USFlagGrapheme + WomanShruggingGrapheme));
    }

    [Test]
    public void ScanWhile_with_mixed_set_stops_at_first_token_outside_the_set()
    {
        // Mix of letters and one multi-rune entry. The scan should
        // pull as many letters or USFlag tokens as possible and stop
        // at the first token that's neither. AllowTrailingInput lets
        // the parse succeed even though ScanWhile doesn't consume the
        // trailing WomanShrugging that stopped it.
        var rule = ScanWhile(TokenSet.Ascii.Letters | TokenSet.Runes(USFlagGrapheme));
        var input = "abc" + USFlagGrapheme + "d" + WomanShruggingGrapheme;

        var result = rule.Parse(input, new ParseOptions { AllowTrailingInput = true });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("abc" + USFlagGrapheme + "d"));
    }
}
