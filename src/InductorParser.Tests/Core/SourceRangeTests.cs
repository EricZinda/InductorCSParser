using System;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// The SourceRange constructor is public so a consumer can synthesize the
// span of a compound AST node from its children's spans, the way an And
// node that joins two sub-expressions wants a range covering both:
// new SourceRange(left.Start, right.End). This fixture covers that
// construction path and the argument validation on it. Per-rule
// SourceRange behavior lives in each rule's own fixture under Rules/,
// per the TestArchitecture.md convention.
[TestFixture]
public class SourceRangeTests
{
    [Test]
    public void Consumer_builds_a_compound_span_from_two_child_ranges()
    {
        // The compound-AST-node use case: take the ranges of two named
        // children from a real parse and join them into one span, the way
        // a consumer's And/Or node would when its children each map to a
        // Symbol but the compound node itself doesn't.
        var left = Literal("one").As("left");
        var right = Literal("two").As("right");
        var pair = And(left, right).As("pair");

        var result = pair.Parse("onetwo");
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        var leftRange = result.Find(left)!.SourceRange!.Value;
        var rightRange = result.Find(right)!.SourceRange!.Value;

        var compound = new SourceRange(leftRange.Start, rightRange.End);
        Assert.That(compound.Start.CharIndex, Is.EqualTo(0));
        Assert.That(compound.End.CharIndex, Is.EqualTo(6));
        Assert.That(compound.SubstringOfInput(), Is.EqualTo("onetwo"));
    }

    [Test]
    public void Endpoints_from_different_inputs_throw_ArgumentException()
    {
        var start = SourcePosition.From("abcdef", 0);
        var end = SourcePosition.From("xyz", 2);
        var thrown = Assert.Throws<ArgumentException>(() => _ = new SourceRange(start, end))!;
        Assert.That(thrown.ParamName, Is.EqualTo("end"));
        Assert.That(thrown.Message, Does.Contain("different input strings"));
    }

    [Test]
    public void Content_equal_inputs_from_separate_string_instances_are_accepted()
    {
        // Two separately built strings with the same content index
        // identically, so the constructor accepts them: the check is
        // content equality, not reference equality.
        string first = string.Concat("hel", "lo");
        string second = string.Concat("he", "llo");
        Assert.That(ReferenceEquals(first, second), Is.False);

        var range = new SourceRange(SourcePosition.From(first, 1), SourcePosition.From(second, 4));
        Assert.That(range.SubstringOfInput(), Is.EqualTo("ell"));
    }

    [Test]
    public void End_before_start_throws_ArgumentException()
    {
        var start = SourcePosition.From("hello", 3);
        var end = SourcePosition.From("hello", 1);
        var thrown = Assert.Throws<ArgumentException>(() => _ = new SourceRange(start, end))!;
        Assert.That(thrown.ParamName, Is.EqualTo("end"));
        Assert.That(thrown.Message, Does.Contain("comes before its start"));
    }

    [Test]
    public void Start_equal_to_end_makes_an_empty_range()
    {
        var position = SourcePosition.From("hello", 2);
        var range = new SourceRange(position, position);
        Assert.That(range.SubstringOfInput(), Is.EqualTo(string.Empty));
    }

    [Test]
    public void Default_SourcePosition_reads_as_position_zero_of_empty_input()
    {
        // A consumer that stores SourcePosition in its own AST node, an
        // array, or a dictionary holds default(SourcePosition) for any slot
        // nothing assigned (an uninitialized field, a TryGetValue miss).
        // Input documents that it is never null, so the default value reads
        // as position 0 of the empty string and every member works.
        SourcePosition position = default;
        Assert.That(position.Input, Is.EqualTo(string.Empty));
        Assert.That(position.SourceLine(), Is.EqualTo(string.Empty));
        Assert.That(position.TokenColumn, Is.EqualTo(0));
        Assert.That(position.TokenColumnNumber, Is.EqualTo(1));
    }

    [Test]
    public void Default_SourceRange_reads_as_an_empty_span_of_empty_input()
    {
        SourceRange range = default;
        Assert.That(range.SubstringOfInput(), Is.EqualTo(string.Empty));
        Assert.That(range.SourceLine(), Is.EqualTo(string.Empty));
    }
}
