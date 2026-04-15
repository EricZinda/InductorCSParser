using System;
using NUnit.Framework;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Tests for the Rule.Compile lifecycle: once a rule has been compiled, the
// graph is sealed and structural modifiers are rejected. Id assignment is a
// separate aspect of Compile and lives in IdAssignmentTests.
[TestFixture]
public class CompileTests
{
    [Test]
    public void Sealed_rule_rejects_Flatten()
    {
        var rule = OneOrMore(RuneIn(RuneSet.Letters));
        rule.Compile();

        Assert.Throws<InvalidOperationException>(() => rule.Flatten(FlattenType.None));
    }

    [Test]
    public void Sealed_rule_rejects_WithError()
    {
        var rule = OneOrMore(RuneIn(RuneSet.Letters));
        rule.Compile();

        Assert.Throws<InvalidOperationException>(() => rule.WithError("late"));
    }

    [Test]
    public void Sealed_rule_rejects_As()
    {
        var rule = OneOrMore(RuneIn(RuneSet.Letters));
        rule.Compile();

        Assert.Throws<InvalidOperationException>(() => rule.As("late"));
    }
}
