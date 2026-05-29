using System;
using NUnit.Framework;
using static InductorParser.Rules;

namespace InductorParser.Tests;

[TestFixture]
public class ParseInputValidationTests
{
    [Test]
    public void Parse_null_input_throws_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => Token('a').Parse(null!));
    }

    [Test]
    public void Parse_null_options_throws_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => Token('a').Parse("a", null!));
    }

    [Test]
    public void Parse_null_input_still_throws_when_cancellation_is_already_canceled()
    {
        var cancellation = new ParseCancellation();
        cancellation.Cancel();

        var options = new ParseOptions { Cancellation = cancellation };

        Assert.Throws<ArgumentNullException>(() => Token('a').Parse(null!, options));
    }
}
