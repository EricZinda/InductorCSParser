using System;
using NUnit.Framework;
using InductorParser;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.TestHelpers;

namespace InductorParser.Tests;

// Tests for the Rule.Compile lifecycle. Coverage of Compile is split
// across several files by design (docs/TestArchitecture.md):
//
//   - Sealing per concrete rule type: the per-rule tests in
//     src/InductorParser.Tests/Rules/<RuleName>Tests.cs verify that
//     Flatten / WithError / As all throw on each rule after Compile
//     (universal requirement #5).
//   - Id assignment (pinned / named-hash / anonymous): IdAssignmentTests.
//   - NameOf auto-compile: NameOfTests.NameOf_auto_compiles_when_called_before_compile.
//   - LateBoundRule auto-compile through Parse, plus a regression that
//     ValidateAll walks nested rules: LateBoundRuleTests
//     (the never-bound rule is two levels deep inside FirstOf+AllOf).
//   - Cycle handling in ComputeRuleStartAll: exercised indirectly by
//     every recursive grammar test (would hang otherwise).
//
// What's left for this file: the cross-cutting Compile behaviors that
// don't belong to any one rule. The internal RuleStartRequirements
// consistency check, idempotency of Compile itself, and Parse's
// auto-compile on the success path (the LateBoundRule tests cover the
// failure path).
[TestFixture]
public class CompileTests
{
    [Test]
    public void Compile_throws_when_a_rule_reports_Advance_Never_with_non_empty_FirstConsumedRunes()
    {
        // Advance.Never means "never consumes on success," which logically
        // forces FirstConsumedRunes to be Empty. If nothing is consumed,
        // there can't be a set of possible first-consumed runes. A subclass
        // that returns a non-empty set alongside Never is violating the
        // contract, and the check here catches it at Compile time rather
        // than letting the mismatch silently corrupt an enclosing AllOfRule's
        // FirstConsumedRunes union.
        var bad = new InconsistentRuleStartRule();

        var ex = Assert.Throws<InvalidOperationException>(() => bad.Compile());
        Assert.That(ex!.Message, Does.Contain("InconsistentRuleStartRule"));
        Assert.That(ex.Message, Does.Contain("Advance.Never"));
    }

    [Test]
    public void Compile_is_idempotent()
    {
        // Compile is documented to do nothing on the second call. The
        // seal flag is the implementation, but several pieces of state
        // would become wrong if it ever re-ran: named-hash ids depend on
        // linear-probe order and could shift if pinned ids were
        // re-collected against a clean usedIds set, FirstConsumedRunes
        // and Advance are computed bottom-up and could drift, and
        // ValidateAll could throw on a graph that's already settled.
        //
        // Snapshot every reachable rule's post-Compile state (type, id,
        // name, flatten policy, FirstConsumedRunes, Advance) and verify
        // the second Compile is a true no-op against the full grammar
        // shape, not just the few ids the test happened to remember.
        // A grammar with a pinned id, a named rule, and anonymous rules
        // exercises all three id-assignment passes plus the bottom-up
        // RuleStartRequirements walk.
        var pinned = new SymbolId(SymbolRanges.CustomRangeStart + 9999);
        var named = OneOrMore(OneOf(RuneSet.Letters)).As("settingName");
        var pinnedRule = OneOrMore(OneOf(RuneSet.Digits)).As(pinned);
        var anonymous = ZeroOrMore(Grapheme('!'));
        var root = AllOf(named, pinnedRule, anonymous);

        root.Compile();
        string firstSnapshot = SnapshotGrammar(root);

        Assert.DoesNotThrow(() => root.Compile());
        string secondSnapshot = SnapshotGrammar(root);

        Assert.That(secondSnapshot, Is.EqualTo(firstSnapshot));
    }

    [Test]
    public void Parse_auto_compiles_a_grammar_that_was_not_compiled_explicitly()
    {
        // Parse() auto-compiles on first call. The failure-path version
        // (Parse on an unbound LateBoundRule throws because Validate
        // runs during the auto-compile) lives in LateBoundRuleTests.
        // This is the success-path counterpart: lock in the before-state
        // as an unassigned id, call Parse, and verify the id moved into
        // the custom range. Only Compile's named-id assignment pass can
        // produce that transition, so the after-value alone wouldn't
        // prove anything if some other code path had already assigned
        // the id. The before-check rules that out.
        //
        // .As(string) only sets Name, not Id, so an as-yet-uncompiled
        // named rule has the default SymbolId (Value 0).
        var rule = OneOrMore(OneOf(RuneSet.Letters)).As("word");
        Assert.That(rule.Id.Value, Is.EqualTo(0),
            "named rule shouldn't have an id assigned before Compile / Parse runs");

        var result = rule.Parse("hello");

        Assert.That(result.Success, Is.True);
        Assert.That(rule.Id.Value, Is.GreaterThanOrEqualTo(SymbolRanges.CustomRangeStart),
            "named rule should have a custom-range id after Parse, proving auto-compile ran");
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
        // that first rune on success.
        internal override RuleStartRequirements ComputeRuleStart()
            => new RuleStartRequirements(RuneSet.Single('x'), Advance.Never);
    }
}
