using NUnit.Framework;
using static InductorParser.Rules;

namespace InductorParser.Tests;

[TestFixture]
public class ZeroOrMoreRuleTests
{
    // ZeroOrMore has no failure path at all, so this fixture only carries
    // success tests. The TestArchitecture doc calls this out explicitly.

    [Test]
    public void ZeroOrMore_with_zero_matches_succeeds_with_empty_consumption()
    {
        // Input doesn't start with 'a', so the inner rule fails on its very
        // first attempt. ZeroOrMore catches that and succeeds with zero
        // children, leaving the lexer position unchanged.
        var rule = And(ZeroOrMore(Char('a')), Char('b'));
        var result = rule.Parse("b");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("b"));
    }

    [Test]
    public void ZeroOrMore_matches_multiple_occurrences_greedily()
    {
        var rule = And(ZeroOrMore(Char('a')), Char('b'));
        var result = rule.Parse("aaab");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aaab"));
    }

    [Test]
    public void ZeroOrMore_stops_at_first_inner_mismatch_and_surrounding_rule_continues()
    {
        // Inner matches 'a' twice, then on the third try sees 'b' and the
        // inner rule fails. ZeroOrMore commits the two successful iterations
        // and hands 'b' off to the next rule in the And.
        var rule = And(ZeroOrMore(Char('a')), Char('b'), Char('c'));
        var result = rule.Parse("aabc");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aabc"));
    }
}
