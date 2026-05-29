// See FailRuleTests.cs for why this project lives outside
// InductorParser.Tests. This rule covers the bulk-scan surface:
// proving that an external Rule subclass can call
// `lexer.TickBudget()` from a public API so its inner loop is
// budget-observable the same way the built-in ScanWhile and
// ScanUntil are.

using System;
using NUnit.Framework;
using InductorParser;

namespace InductorParser.ExternalContractTests;

[TestFixture]
public class ScanRunRuleTests
{
    [Test]
    public void ScanRunRule_fires_at_parse_time_and_consumes_the_matching_run()
    {
        var rule = new ScanRunRule(rune => rune >= 'a' && rune <= 'z');

        Assert.That(rule.Parse("abc").Success, Is.True);
        // Trailing input the rule didn't consume fails the default
        // strict-consume parse.
        Assert.That(rule.Parse("abc1").Success, Is.False);
    }

    [Test]
    public void ScanRunRule_observes_Timeout_during_its_inner_scan()
    {
        // The point of this test: an external Rule subclass whose
        // TryParseRule consumes N tokens in one invocation can call
        // `lexer.TickBudget()` from its inner loop the same way the
        // built-in ScanWhile / ScanUntil do. Without the tick the
        // periodic budget check only fires on EnterRule, so a long
        // scan goes unobserved. The tick is what lets a 1-tick Timeout
        // trip mid-scan instead of after the whole run completes.
        var rule = new ScanRunRule(rune => rune >= 'a' && rune <= 'z');
        var options = new ParseOptions
        {
            Timeout = TimeSpan.FromTicks(1),
            RuleCountLimit = 0,
        };

        var result = rule.Parse(new string('a', 1_000_000), options);

        Assert.That(result.Outcome, Is.EqualTo(ParseOutcome.Timeout),
            "an external scan rule that calls lexer.TickBudget() in its " +
            "inner loop must observe Timeout mid-scan.");
    }
}
