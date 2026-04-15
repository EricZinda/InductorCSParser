using NUnit.Framework;
using static InductorParser.Rules;

namespace InductorParser.Tests;

[TestFixture]
public class OptionalRuleTests
{
    [Test]
    public void Optional_inner_match_is_consumed()
    {
        // Optional wraps a rule; when inner matches, that input is consumed
        // and the surrounding grammar sees the post-match position.
        var rule = And(Optional(Char('-')), Char('a'));
        var result = rule.Parse("-a");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("-a"));
    }

    [Test]
    public void Optional_inner_miss_succeeds_with_no_consumption()
    {
        // Inner doesn't match; Optional still succeeds with empty and the
        // surrounding grammar runs from the same position Optional started at.
        var rule = And(Optional(Char('-')), Char('a'));
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("a"));
    }

    [Test]
    public void Optional_inner_failure_still_contributes_to_DeepestFailure()
    {
        // Known PEG heuristic quirk: an Optional whose inner fails deeper
        // than the required path can still win the error message via
        // deepest-failure-wins.
        //
        // Grammar: And(Optional(And(a, b, c-with-message)), x-with-message)
        // Input:   "abdy"
        //
        // Optional's inner reads "ab" then 'c' fails at offset 2,
        // recording "need 'c'". Optional catches, succeeds with empty.
        // Char('x') then fails at offset 0 with its own "need 'x'".
        // Deepest-wins picks offset 2: user sees "need 'c'", pointing
        // inside what was supposedly optional. Grammars that care can
        // override with a WithError at the outer required rule, but
        // that won't help here because the outer Char('x') already has
        // one and it's still shallower.
        var rule = And(Optional(And(Char('a'),
                                    Char('b'),
                                    Char('c').WithError("need 'c'"))),
                       Char('x').WithError("need 'x'"));

        var result = rule.Parse("abdy");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
        Assert.That(result.ErrorMessage, Is.EqualTo("need 'c'"));
    }
}
