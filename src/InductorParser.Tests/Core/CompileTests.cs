using System;
using NUnit.Framework;
using InductorParser;
using InductorParser.Lexing;
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

        Assert.Throws<InvalidOperationException>(() => rule.Flatten(FlattenType.Preserve));
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

    [Test]
    public void Compile_throws_when_a_rule_reports_Advance_Never_with_non_empty_FirstConsumedRunes()
    {
        // Advance.Never means "never consumes on success," which logically
        // forces FirstConsumedRunes to be Empty. If nothing is consumed,
        // there can't be a set of possible first-consumed runes. A subclass
        // that returns a non-empty set alongside Never is violating the
        // contract, and the check here catches it at Compile time rather
        // than letting the mismatch silently corrupt an enclosing AndRule's
        // FirstConsumedRunes union.
        var bad = new InconsistentRuleStartRule();

        var ex = Assert.Throws<InvalidOperationException>(() => bad.Compile());
        Assert.That(ex!.Message, Does.Contain("InconsistentRuleStartRule"));
        Assert.That(ex.Message, Does.Contain("Advance.Never"));
    }

    // Subclass that deliberately violates the RuleStartRequirements invariant. Lives
    // here and not in the main InductorParser assembly because the check
    // is defensive against authoring mistakes, not behavior any in-tree
    // rule produces. InternalsVisibleTo makes the internal virtual
    // overridable from the test assembly.
    private sealed class InconsistentRuleStartRule : Rule
    {
        public InconsistentRuleStartRule() : base(FlattenType.Preserve) { }

        internal override Symbol? TryParseRule(Lexer lexer, FlattenType effectiveFlattenType, System.Collections.Generic.List<Symbol>? outputSymbols) => null;

        // Return the set of runes this rule might consume first (can be a superset)
        // (RuneSet.Empty when Advance.Never. RuneSet.Universe means "I don't know").
        // Then say whether the rule Always / Sometimes / Never consumes at least
        // that first character on success.
        internal override RuleStartRequirements ComputeRuleStart()
            => new RuleStartRequirements(RuneSet.Single('x'), Advance.Never);
    }
}
