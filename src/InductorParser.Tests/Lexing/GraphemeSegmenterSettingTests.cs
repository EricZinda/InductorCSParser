using System;
using NUnit.Framework;
using InductorParser.Lexing;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Tests for the process-wide segmenter setting
// (GraphemeHelpers.Segmenter): Automatic resolution, explicit
// overrides, the freeze-on-first-use rule, and the test-only reset.
// The fixture mutates process-wide state, so SetUp and TearDown both
// restore a fresh unfrozen Automatic through
// GraphemeSegmentation.ResetForTesting, and the fixture is marked
// NonParallelizable so no other test can be mid-parse while the
// cluster-boundary cache is cleared.
//
// On .NET 8 the bundled and runtime implementations produce identical boundaries
// (the differential tests in GraphemeSegmentationTests hold them
// equal), so no input can tell them apart here. Asserting
// ActiveSegmenter is still asserting the dispatch: the getter and the
// dispatcher branch read the same field.
[TestFixture]
[NonParallelizable]
public class GraphemeSegmenterSettingTests
{
    [SetUp]
    public void ResetBefore() => GraphemeSegmentation.ResetForTesting();

    [TearDown]
    public void ResetAfter() => GraphemeSegmentation.ResetForTesting();

    [Test]
    public void Automatic_is_the_default_and_resolves_to_runtime_on_this_build()
    {
        Assert.That(GraphemeHelpers.Segmenter, Is.EqualTo(GraphemeSegmenter.Automatic));

        // This test project targets net8.0 only, so Automatic means the
        // runtime's StringInfo. The netstandard2.1 half (where
        // Automatic means Bundled) is asserted by the Unity PlayMode
        // smoke test, on the runtime where it matters.
        GraphemeHelpers.FirstClusterLength("a".AsSpan());
        Assert.That(GraphemeHelpers.ActiveSegmenter, Is.EqualTo(GraphemeSegmenter.Runtime));
    }

    [Test]
    public void Explicit_bundled_is_active_after_the_first_query()
    {
        GraphemeHelpers.Segmenter = GraphemeSegmenter.Bundled;
        GraphemeHelpers.FirstClusterLength("a".AsSpan());
        Assert.That(GraphemeHelpers.ActiveSegmenter, Is.EqualTo(GraphemeSegmenter.Bundled));
    }

    [Test]
    public void Explicit_runtime_is_active_after_the_first_query()
    {
        GraphemeHelpers.Segmenter = GraphemeSegmenter.Runtime;
        GraphemeHelpers.FirstClusterLength("a".AsSpan());
        Assert.That(GraphemeHelpers.ActiveSegmenter, Is.EqualTo(GraphemeSegmenter.Runtime));
    }

    [Test]
    public void Setting_twice_before_the_freeze_keeps_the_last_write()
    {
        GraphemeHelpers.Segmenter = GraphemeSegmenter.Bundled;
        GraphemeHelpers.Segmenter = GraphemeSegmenter.Runtime;
        Assert.That(GraphemeHelpers.ActiveSegmenter, Is.EqualTo(GraphemeSegmenter.Runtime));
    }

    [Test]
    public void Setting_after_a_helper_query_throws()
    {
        GraphemeHelpers.FirstClusterLength("a".AsSpan());
        var exception = Assert.Throws<InvalidOperationException>(
            () => GraphemeHelpers.Segmenter = GraphemeSegmenter.Bundled);
        Assert.That(exception!.Message, Does.Contain("before building grammars"));
    }

    [Test]
    public void Reading_the_active_segmenter_freezes()
    {
        _ = GraphemeHelpers.ActiveSegmenter;
        Assert.Throws<InvalidOperationException>(
            () => GraphemeHelpers.Segmenter = GraphemeSegmenter.Bundled);
    }

    [Test]
    public void Constructing_a_token_rule_freezes()
    {
        // Token validates its literal is one grapheme cluster through
        // GraphemeHelpers.FirstClusterLength, so grammar construction
        // is a freeze point. This is why the docs say to set the
        // property before building grammars, not just before parsing.
        Token('x');
        Assert.Throws<InvalidOperationException>(
            () => GraphemeHelpers.Segmenter = GraphemeSegmenter.Bundled);
    }

    [Test]
    public void Undefined_enum_value_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GraphemeHelpers.Segmenter = (GraphemeSegmenter)42);
    }

    [Test]
    public void Reset_restores_automatic_and_clears_the_cluster_cache()
    {
        GraphemeHelpers.Segmenter = GraphemeSegmenter.Bundled;

        // new string guarantees a distinct instance, so a cache entry
        // for an interned literal shared with another fixture can't
        // stand in for this one.
        string input = new string('q', 12);
        var lexer = new Lexer(input);
        lexer.Read();
        Assert.That(GraphemeClusterIndex.HasCachedIndexFor(input), Is.True);

        GraphemeSegmentation.ResetForTesting();

        Assert.That(GraphemeHelpers.Segmenter, Is.EqualTo(GraphemeSegmenter.Automatic));
        Assert.That(GraphemeClusterIndex.HasCachedIndexFor(input), Is.False);
        Assert.That(GraphemeHelpers.ActiveSegmenter, Is.EqualTo(GraphemeSegmenter.Runtime));
    }
}
