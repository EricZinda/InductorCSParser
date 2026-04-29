using System;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;
using static InductorParser.Tests.UnicodeExamples;

namespace InductorParser.Tests;

[TestFixture]
public class RuneRunRuleTests
{
    [Test]
    public void RuneRun_matches_a_run_into_one_leaf()
    {
        var result = RuneRun(RuneSet.Ascii.Letters).Parse("abcXYZ");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("abcXYZ"));
        Assert.That(result.Tree!.Children.Count, Is.EqualTo(0));
    }

    [Test]
    public void RuneRun_stops_before_first_rune_outside_the_set()
    {
        var rule = AllOf(RuneRun(RuneSet.Ascii.Letters), Token('!'));

        var result = rule.Parse("abc!");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("abc"));
    }

    [Test]
    public void RuneRun_requires_at_least_one_rune()
    {
        var result = RuneRun(RuneSet.Ascii.Letters)
            .WithError("need a letter")
            .Parse("123");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("need a letter"));
    }

    [Test]
    public void RuneRun_honors_minimum_count()
    {
        var result = RuneRun(RuneSet.Ascii.Letters, minimumCount: 4)
            .WithError("need four letters")
            .Parse("abc!");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(result.ErrorMessage, Is.EqualTo("need four letters"));
    }

    [Test]
    public void RuneRun_rejects_zero_minimum_count()
    {
        Assert.That(
            () => RuneRun(RuneSet.Ascii.Letters, minimumCount: 0),
            Throws.TypeOf<System.ArgumentOutOfRangeException>());
    }

    [Test]
    public void RuneRun_rejects_multi_rune_grapheme_under_grapheme_lexer()
    {
        var rule = RuneRun(RuneSet.Single(WavingHandRune));

        var result = rule.Parse(SkinTonedWaveGrapheme);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
    }

    [Test]
    public void RuneRun_under_rune_lexer_consumes_supplementary_runes()
    {
        var allowed = RuneSet.Single(WavingHandRune) | RuneSet.Single(MediumSkinToneRune);
        var rule = RuneRun(allowed);

        var result = rule.Parse(SkinTonedWaveGrapheme,
            new ParseOptions { InputUnit = InputUnit.Rune });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(SkinTonedWaveGrapheme));
    }

    [Test]
    public void RuneRun_trace_success_produces_expected_output()
    {
        var sink = NewSink();
        RuneRun(RuneSet.Ascii.Letters).Parse("abc",
            new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.AdvanceWhileSingleRuneIn: 'a', Consumed: 1",
            "   Lexer.AdvanceWhileSingleRuneIn: 'b', Consumed: 2",
            "   Lexer.AdvanceWhileSingleRuneIn: 'c', Consumed: 3",
            "   SUCC | RuneRun: count= 3, 3 chars, wanted one or more of '[A-Z,a-z]'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Sealed_RuneRun_rejects_Flatten()
    {
        var rule = RuneRun(RuneSet.Ascii.Letters);
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.Flatten(FlattenType.Preserve));
    }

    [Test]
    public void Sealed_RuneRun_rejects_WithError()
    {
        var rule = RuneRun(RuneSet.Ascii.Letters);
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.WithError("late"));
    }

    [Test]
    public void Sealed_RuneRun_rejects_As()
    {
        var rule = RuneRun(RuneSet.Ascii.Letters);
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.As("late"));
    }
}
