using NUnit.Framework;
using static InductorParser.Rules;

namespace InductorParser.Tests;

[TestFixture]
public class AndRuleTests
{
    [Test]
    public void And_all_children_succeed_concatenates_consumed_input()
    {
        var rule = And(Char('a'), Char('b'), Char('c'));
        var result = rule.Parse("abc");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("abc"));
    }

    [Test]
    public void And_failure_without_WithError_falls_back_to_positional_message()
    {
        // No WithError on any child or on And itself. Char('b') records a
        // null message at offset 1; the positional fallback renders.
        var rule = And(Char('a'), Char('b'));
        var result = rule.Parse("ax");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
        Assert.That(result.ErrorMessage, Does.StartWith("Parse failed at offset 1"));
    }

    [Test]
    public void And_first_child_failure_propagates_position()
    {
        // Each child has its own WithError message. When the first child
        // fails, its message wins because it's the only one that ran.
        var rule = And(Char('a').WithError("need an 'a'"),
                       Char('b').WithError("need a 'b'"));

        var result = rule.Parse("xb");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("need an 'a'"));
    }

    [Test]
    public void And_later_child_failure_reports_at_deeper_position()
    {
        // Char('a') succeeds. Char('b') runs at offset 1 and fails on 'x'.
        // The second child's pre-read position is deeper than anywhere
        // the first child could have recorded, and its WithError message
        // is the one that surfaces.
        var rule = And(Char('a').WithError("need an 'a'"),
                       Char('b').WithError("need a 'b'"));

        var result = rule.Parse("ax");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
        Assert.That(result.ErrorMessage, Is.EqualTo("need a 'b'"));
    }
}
