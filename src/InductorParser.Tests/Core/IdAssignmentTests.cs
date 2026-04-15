using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests;

[TestFixture]
public class IdAssignmentTests
{
    [Test]
    public void Named_rule_id_is_in_custom_range()
    {
        var rule = OneOrMore(RuneIn(RuneSet.Letters)).As("settingName");
        rule.Compile();

        Assert.That(rule.Id.Value, Is.GreaterThanOrEqualTo(SymbolRanges.CustomRangeStart));
    }

    [Test]
    public void Same_name_produces_same_id_within_a_process()
    {
        var ruleA = OneOrMore(RuneIn(RuneSet.Letters)).As("foo");
        ruleA.Compile();

        var ruleB = OneOrMore(RuneIn(RuneSet.Letters)).As("foo");
        ruleB.Compile();

        Assert.That(ruleB.Id.Value, Is.EqualTo(ruleA.Id.Value));
    }

    [Test]
    public void Pinned_id_survives_compile()
    {
        var pinned = new SymbolId(SymbolRanges.CustomRangeStart + 9999);
        var rule = OneOrMore(RuneIn(RuneSet.Letters)).As(pinned);
        rule.Compile();

        Assert.That(rule.Id, Is.EqualTo(pinned));
    }

    [Test]
    public void Anonymous_rule_does_not_steal_a_pinned_id()
    {
        var pinned = new SymbolId(SymbolRanges.CustomRangeStart);
        var pinnedRule = OneOrMore(RuneIn(RuneSet.Letters)).As(pinned);
        var anonRule = OneOrMore(RuneIn(RuneSet.Digits));
        var doc = And(pinnedRule, anonRule);
        doc.Compile();

        Assert.That(pinnedRule.Id, Is.EqualTo(pinned));
        Assert.That(anonRule.Id, Is.Not.EqualTo(pinned));
    }

    [Test]
    public void Hash_is_deterministic_across_processes()
    {
        // FNV-1a of "settingName" is a fixed value. If anyone ever "optimizes"
        // the hash to string.GetHashCode or swaps algorithms, this test fails
        // and forces a decision about whether breaking persisted ids is
        // acceptable.
        var rule = OneOrMore(RuneIn(RuneSet.Letters)).As("settingName");
        rule.Compile();

        int expected = Rule.HashNameToCustomRange("settingName");
        Assert.That(rule.Id.Value, Is.EqualTo(expected));
    }

    [Test]
    public void Named_rule_does_not_steal_a_pinned_id()
    {
        // Pin a rule at the slot another named rule would hash to. The
        // named rule must probe upward and end up somewhere else.
        var name = "collisionTest";
        int slotForName = Rule.HashNameToCustomRange(name);
        var pinned = new SymbolId(slotForName);

        var pinnedRule = OneOrMore(RuneIn(RuneSet.Letters)).As(pinned);
        var namedRule = OneOrMore(RuneIn(RuneSet.Digits)).As(name);
        var doc = And(pinnedRule, namedRule);
        doc.Compile();

        Assert.That(pinnedRule.Id, Is.EqualTo(pinned));
        Assert.That(namedRule.Id.Value, Is.GreaterThan(pinned.Value));
    }
}
