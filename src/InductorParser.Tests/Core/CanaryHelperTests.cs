using System;
using NUnit.Framework;
using static InductorParser.Tests.CanaryHelper;

namespace InductorParser.Tests.Core;

[TestFixture]
public class CanaryHelperTests
{
    [Test]
    public void Canary_ReturnsLiteral_WhenSingleCodepointMatches()
    {
        var literal = Canary("é", "Latin e with acute, precomposed", 0x00E9);
        Assert.That(literal.Length, Is.EqualTo(1));
        Assert.That((int)literal[0], Is.EqualTo(0x00E9));
    }

    [Test]
    public void Canary_ReturnsLiteral_WhenMultipleCodepointsMatch()
    {
        var literal = Canary("é", "Latin e + combining acute, decomposed", 0x0065, 0x0301);
        Assert.That(literal.Length, Is.EqualTo(2));
        Assert.That((int)literal[0], Is.EqualTo(0x0065));
        Assert.That((int)literal[1], Is.EqualTo(0x0301));
    }

    [Test]
    public void Canary_ReturnsLiteral_WhenSurrogatePairMatchesSupplementaryCodepoint()
    {
        var literal = Canary("👋", "waving hand emoji", 0x1F44B);
        Assert.That(literal.Length, Is.EqualTo(2));
        Assert.That((int)literal[0], Is.EqualTo(0xD83D));
        Assert.That((int)literal[1], Is.EqualTo(0xDC4B));
    }

    [Test]
    public void Canary_ReturnsLiteral_WhenLoneHighSurrogateMatchesCodeUnit()
    {
        // Built from a char at runtime, not written as a "\uD800"
        // literal: IL2CPP replaces an unpaired surrogate in a compiled
        // string literal with U+FFFD, and this test is about Canary
        // accepting a genuine lone surrogate, not about literal
        // corruption.
        var literal = Canary(((char)0xD800).ToString(), "lone high surrogate (min)", 0xD800);
        Assert.That(literal.Length, Is.EqualTo(1));
        Assert.That((int)literal[0], Is.EqualTo(0xD800));
    }

    [Test]
    public void Canary_Throws_WhenLengthMismatch()
    {
        var thrown = Assert.Throws<InvalidOperationException>(
            () => Canary("é", "two runes claimed as one", 0x0065));
        Assert.That(thrown!.Message, Does.Contain("two runes claimed as one"));
        Assert.That(thrown.Message, Does.Contain("U+0065"));
        Assert.That(thrown.Message, Does.Contain("U+0301"));
    }

    [Test]
    public void Canary_Throws_WhenCodepointValueDiffers()
    {
        var thrown = Assert.Throws<InvalidOperationException>(
            () => Canary("é", "wrong codepoint claimed", 0x00EA));
        Assert.That(thrown!.Message, Does.Contain("wrong codepoint claimed"));
        Assert.That(thrown.Message, Does.Contain("U+00EA"));
        Assert.That(thrown.Message, Does.Contain("U+00E9"));
    }

    [Test]
    public void Canary_Throws_WhenLiteralIsEmptyButCodepointsExpected()
    {
        Assert.Throws<InvalidOperationException>(
            () => Canary("", "expected one rune but got empty literal", 0x0041));
    }

    [Test]
    public void Canary_HandlesAsciiLiteral()
    {
        var literal = Canary("A", "ASCII capital a", 0x0041);
        Assert.That(literal.Length, Is.EqualTo(1));
        Assert.That((int)literal[0], Is.EqualTo(0x0041));
    }

    [Test]
    public void Canary_NullLiteralThrows()
    {
        Assert.Throws<ArgumentNullException>(() => Canary(null!, "x", 0x0041));
    }
}
