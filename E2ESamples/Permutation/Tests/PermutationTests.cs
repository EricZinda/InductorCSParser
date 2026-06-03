// Three fixtures, matching the other E2E samples' layout:
//   * GoldenInputs   every ordering of the three attributes parses to the
//                    same Box. That equivalence across orderings is the whole
//                    reason a permutation parser exists.
//   * RejectInputs   a missing attribute, a duplicate, empty input, and
//                    trailing junk each fail with a sensible position and
//                    message.
//   * PermutationCore exercises the custom rule directly on parsec's canonical
//                    "permutation of characters" shape, independent of the Box
//                    grammar.

using System;
using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;
using PermutationSample.Rewrite;

namespace PermutationSample.Tests;

[TestFixture]
public class GoldenInputs
{
    // All six orderings of the three attributes, every one Box(4, 5, 6).
    [TestCase("width=4 height=5 depth=6")]
    [TestCase("width=4 depth=6 height=5")]
    [TestCase("height=5 width=4 depth=6")]
    [TestCase("height=5 depth=6 width=4")]
    [TestCase("depth=6 width=4 height=5")]
    [TestCase("depth=6 height=5 width=4")]
    public void Every_ordering_parses_to_the_same_box(string input)
    {
        var box = BoxParser.Parse(input);
        Assert.That(box, Is.EqualTo(new Box(4, 5, 6)));
    }

    [Test]
    public void Reads_multi_digit_values()
    {
        var box = BoxParser.Parse("depth=6 width=40 height=500");
        Assert.That(box, Is.EqualTo(new Box(40, 500, 6)));
    }
}

[TestFixture]
public class RejectInputs
{
    [Test]
    public void Missing_attribute_fails_after_the_ones_present()
    {
        const string input = "width=4 height=5";
        Assert.That(BoxParser.TryParse(input, out _, out var error), Is.False);
        // The parser got as far as end-of-input looking for the third
        // attribute, so the failure lands there.
        Assert.That(error!.CharIndex, Is.EqualTo(input.Length));
        Assert.That(error.Message, Is.EqualTo("expected the 'depth' attribute"));
    }

    [Test]
    public void Duplicate_attribute_fails_where_a_new_one_was_expected()
    {
        // width twice, height never. After the first width, the parser is at
        // the second "width" looking for one of the remaining attributes.
        Assert.That(BoxParser.TryParse("width=4 width=5 depth=6", out _, out var error), Is.False);
        Assert.That(error!.CharIndex, Is.EqualTo(8));
        Assert.That(error.Message, Is.EqualTo("expected the 'height' attribute"));
    }

    [Test]
    public void Empty_input_fails_at_the_start()
    {
        Assert.That(BoxParser.TryParse("", out _, out var error), Is.False);
        Assert.That(error!.CharIndex, Is.EqualTo(0));
        Assert.That(error.Message, Is.EqualTo("expected the 'width' attribute"));
    }

    [Test]
    public void Trailing_text_after_all_attributes_is_rejected()
    {
        const string input = "width=4 height=5 depth=6 extra";
        Assert.That(BoxParser.TryParse(input, out _, out var error), Is.False);
        Assert.That(error!.Message, Is.EqualTo("unexpected text after the box attributes"));
    }
}

[TestFixture]
public class PermutationCore
{
    // parsec's canonical example: a permutation of the characters a, b, c.
    // Built fresh per test so each call compiles its own grammar.
    private static Rule AbcInAnyOrder() =>
        new PermutationRule(Literal("a"), Literal("b"), Literal("c"));

    [TestCase("abc")]
    [TestCase("acb")]
    [TestCase("bac")]
    [TestCase("bca")]
    [TestCase("cab")]
    [TestCase("cba")]
    public void Matches_any_ordering(string input)
    {
        var result = AbcInAnyOrder().Parse(input);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Fails_when_an_item_is_missing()
    {
        var result = AbcInAnyOrder().Parse("ab");
        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void Fails_when_an_item_repeats()
    {
        // Each item matches exactly once, so the leftover 'a' has no item to
        // consume it and the trailing-input check rejects the parse.
        var result = AbcInAnyOrder().Parse("abca");
        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void Constructor_rejects_an_empty_item_list()
    {
        Assert.Throws<ArgumentException>(() => new PermutationRule());
    }

    [Test]
    public void Constructor_rejects_a_null_item_list()
    {
        Assert.Throws<ArgumentNullException>(() => new PermutationRule(null!));
    }
}
