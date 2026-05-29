// See FailRuleTests.cs for why this project lives outside
// InductorParser.Tests. This rule covers the leaf-with-input-
// consumption-and-success-path shape.

using NUnit.Framework;

namespace InductorParser.ExternalContractTests;

[TestFixture]
public class OneRuneRuleTests
{
    [Test]
    public void OneRuneRule_fires_at_parse_time_for_both_match_and_no_match()
    {
        var letter = new OneRuneRule("a letter", c => c is >= 'a' and <= 'z');

        Assert.That(letter.Parse("a").Success, Is.True);
        Assert.That(letter.Parse("9").Success, Is.False);
    }
}
