using System;
using InductorParser.Lexing;
using NUnit.Framework;

namespace InductorParser.Tests;

// Tests for the public GraphemeHelpers surface. These three methods are
// what user-defined rules call for segmentation, so their edge behavior
// is public API.
[TestFixture]
public class GraphemeHelpersTests
{
    [Test]
    public void Count_counts_user_perceived_characters()
    {
        Assert.That(GraphemeHelpers.Count(""), Is.EqualTo(0));
        Assert.That(GraphemeHelpers.Count("abc"), Is.EqualTo(3));
        Assert.That(GraphemeHelpers.Count("\r\n"), Is.EqualTo(1));
        Assert.That(GraphemeHelpers.Count(UnicodeExamples.FamilyManWomanBoyGrapheme), Is.EqualTo(1));
        Assert.That(GraphemeHelpers.Count("a" + UnicodeExamples.USFlagGrapheme + "b"), Is.EqualTo(3));
    }

    [Test]
    public void FirstClusterLength_returns_the_leading_cluster_width()
    {
        Assert.That(GraphemeHelpers.FirstClusterLength("abc".AsSpan()), Is.EqualTo(1));
        Assert.That(GraphemeHelpers.FirstClusterLength("\r\nx".AsSpan()), Is.EqualTo(2));
        Assert.That(GraphemeHelpers.FirstClusterLength(
            (UnicodeExamples.USFlagGrapheme + "x").AsSpan()), Is.EqualTo(4));
        Assert.That(GraphemeHelpers.FirstClusterLength(ReadOnlySpan<char>.Empty), Is.EqualTo(0));
    }

    [Test]
    public void FirstClusterLength_reads_a_lone_surrogate_as_one_char()
    {
        Assert.That(GraphemeHelpers.FirstClusterLength(
            UnicodeExamples.HighSurrogateMinText.AsSpan()), Is.EqualTo(1));
    }

    [Test]
    public void FloorToClusterStart_pulls_a_mid_cluster_position_to_its_start()
    {
        // "a" + flag(4 chars) + "b": positions 2, 3, and 4 sit inside
        // the flag cluster that starts at 1.
        string text = "a" + UnicodeExamples.USFlagGrapheme + "b";
        Assert.That(GraphemeHelpers.FloorToClusterStart(text, 2), Is.EqualTo(1));
        Assert.That(GraphemeHelpers.FloorToClusterStart(text, 3), Is.EqualTo(1));
        Assert.That(GraphemeHelpers.FloorToClusterStart(text, 4), Is.EqualTo(1));
    }

    [Test]
    public void FloorToClusterStart_keeps_a_boundary_position_unchanged()
    {
        string text = "a" + UnicodeExamples.USFlagGrapheme + "b";
        Assert.That(GraphemeHelpers.FloorToClusterStart(text, 0), Is.EqualTo(0));
        Assert.That(GraphemeHelpers.FloorToClusterStart(text, 1), Is.EqualTo(1));
        Assert.That(GraphemeHelpers.FloorToClusterStart(text, 5), Is.EqualTo(5));
        Assert.That(GraphemeHelpers.FloorToClusterStart(text, text.Length), Is.EqualTo(text.Length));
    }

    [Test]
    public void FloorToClusterStart_clamps_out_of_range_positions()
    {
        Assert.That(GraphemeHelpers.FloorToClusterStart("ab", -3), Is.EqualTo(0));
        Assert.That(GraphemeHelpers.FloorToClusterStart("ab", 99), Is.EqualTo(2));
        // A past-the-end position inside a trailing multi-char cluster
        // clamps to the length first, then floors to the cluster start.
        string trailingFlag = "a" + UnicodeExamples.USFlagGrapheme;
        Assert.That(GraphemeHelpers.FloorToClusterStart(trailingFlag, 99),
            Is.EqualTo(trailingFlag.Length));
    }

    [Test]
    public void FloorToClusterStart_rejects_null_text()
    {
        Assert.Throws<ArgumentNullException>(
            () => GraphemeHelpers.FloorToClusterStart(null!, 0));
    }

    [Test]
    public void Count_rejects_null_text()
    {
        // Without the explicit check, text.AsSpan() maps null to an
        // empty span and Count returns 0, which is indistinguishable
        // from the empty string and hides the caller's bug. The other
        // string-typed helpers on this surface (FloorToClusterStart
        // above, NormalizationHelpers.Normalize / IsNormalized) all
        // throw for null.
        Assert.Throws<ArgumentNullException>(() => GraphemeHelpers.Count(null!));
    }
}
