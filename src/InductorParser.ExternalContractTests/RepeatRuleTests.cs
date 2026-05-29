// See FailRuleTests.cs for why this project lives outside
// InductorParser.Tests. This rule covers the composite-with-inner-
// children shape.

using NUnit.Framework;
using static InductorParser.Rules;

namespace InductorParser.ExternalContractTests;

[TestFixture]
public class RepeatRuleTests
{
    [Test]
    public void RepeatRule_fires_at_parse_time_and_delegates_to_its_inner()
    {
        var rule = new RepeatRule(3, Token('a'));

        Assert.That(rule.Parse("aaa").Success, Is.True);
        Assert.That(rule.Parse("aax").Success, Is.False);
    }
}
