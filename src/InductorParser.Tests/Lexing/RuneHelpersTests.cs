using System;
using NUnit.Framework;
using InductorParser.Lexing;

namespace InductorParser.Tests.Lexing;

[TestFixture]
public class RuneHelpersTests
{
    [Test]
    public void RuneCount_empty_span_is_zero()
    {
        Assert.That(RuneHelpers.RuneCount(string.Empty.AsSpan()), Is.EqualTo(0));
    }

    [Test]
    public void RuneCount_counts_each_bmp_char_as_one_rune()
    {
        // Plain ASCII: char count and rune count agree.
        Assert.That(RuneHelpers.RuneCount("abc".AsSpan()), Is.EqualTo(3));
    }

    [Test]
    public void RuneCount_counts_a_surrogate_pair_as_one_rune()
    {
        // 👍 (U+1F44D) is two UTF-16 chars but one rune.
        string thumbsUp = char.ConvertFromUtf32(0x1F44D);
        Assert.That(thumbsUp.Length, Is.EqualTo(2), "precondition: astral rune is two chars");
        Assert.That(RuneHelpers.RuneCount(thumbsUp.AsSpan()), Is.EqualTo(1));
    }

    [Test]
    public void RuneCount_mixes_bmp_and_astral_runes()
    {
        // "a" + 👍 + "b": three runes, four chars.
        string text = "a" + char.ConvertFromUtf32(0x1F44D) + "b";
        Assert.That(text.Length, Is.EqualTo(4));
        Assert.That(RuneHelpers.RuneCount(text.AsSpan()), Is.EqualTo(3));
    }

    [Test]
    public void RuneCount_treats_a_high_surrogate_with_no_paired_low_as_one_rune()
    {
        // A high surrogate at the end of the span has no low surrogate inside
        // the span, so IsSurrogatePairAt is false and it counts as one rune,
        // matching how the lexer reads a stray surrogate. This is exactly the
        // prefix case: counting runes in input[0 .. position) where position
        // splits just after a high surrogate would never happen at a rune
        // boundary, but a span that ends on a lone high surrogate must still
        // terminate and count it as one.
        string loneHigh = "\uD83D"; // high surrogate of 👍, no low following
        Assert.That(RuneHelpers.RuneCount(loneHigh.AsSpan()), Is.EqualTo(1));
    }

    [Test]
    public void RuneCount_counts_a_prefix_when_handed_a_sliced_span()
    {
        // The WithinToken use: count runes consumed so far by slicing the
        // input up to the consumed char offset. 👍🏽 is two astral runes
        // (four chars); a span over the first two chars is one rune.
        string cluster = char.ConvertFromUtf32(0x1F44D) + char.ConvertFromUtf32(0x1F3FD);
        Assert.That(RuneHelpers.RuneCount(cluster.AsSpan(0, 2)), Is.EqualTo(1));
        Assert.That(RuneHelpers.RuneCount(cluster.AsSpan()), Is.EqualTo(2));
    }
}
