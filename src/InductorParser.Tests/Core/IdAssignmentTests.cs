using System;
using System.Collections.Generic;
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
        var rule = OneOrMore(OneOf(TokenSet.Letters)).As("settingName");
        rule.Compile();

        Assert.That(rule.Id.Value, Is.GreaterThanOrEqualTo(SymbolRanges.CustomRangeStart));
    }

    [Test]
    public void Same_name_produces_same_id_within_a_process()
    {
        var ruleA = OneOrMore(OneOf(TokenSet.Letters)).As("foo");
        ruleA.Compile();

        var ruleB = OneOrMore(OneOf(TokenSet.Letters)).As("foo");
        ruleB.Compile();

        Assert.That(ruleB.Id.Value, Is.EqualTo(ruleA.Id.Value));
    }

    [Test]
    public void Pinned_id_survives_compile()
    {
        var pinned = new SymbolId(SymbolRanges.CustomRangeStart + 9999);
        var rule = OneOrMore(OneOf(TokenSet.Letters)).As(pinned);
        rule.Compile();

        Assert.That(rule.Id, Is.EqualTo(pinned));
    }

    [Test]
    public void Hash_is_deterministic_across_processes()
    {
        // FNV-1a of "settingName" is a fixed value. If anyone ever "optimizes"
        // the hash to string.GetHashCode or swaps algorithms, this test fails
        // and forces a decision about whether breaking persisted ids is
        // acceptable.
        var rule = OneOrMore(OneOf(TokenSet.Letters)).As("settingName");
        rule.Compile();

        int expected = Rule.HashNameToCustomRange("settingName");
        Assert.That(rule.Id.Value, Is.EqualTo(expected));
    }

    [Test]
    public void Two_reachable_rules_with_the_same_name_fail_to_compile()
    {
        var firstRule = OneOrMore(OneOf(TokenSet.Letters)).As("foo");
        var secondRule = OneOrMore(OneOf(TokenSet.Digits)).As("foo");
        var doc = And(firstRule, secondRule);

        var exception = Assert.Throws<InvalidOperationException>(() => doc.Compile());
        Assert.That(exception!.Message, Does.Contain("foo"));
    }

    [Test]
    public void Same_named_rule_referenced_twice_in_a_grammar_compiles()
    {
        // Reachability is per-rule, not per-edge. A single rule reached via
        // two parents is still one rule, so its name should not be flagged.
        var sharedRule = OneOrMore(OneOf(TokenSet.Letters)).As("shared");
        var doc = And(sharedRule, sharedRule);

        Assert.DoesNotThrow(() => doc.Compile());
    }

    [Test]
    public void Two_reachable_rules_pinned_to_the_same_SymbolId_fail_to_compile()
    {
        var pinned = new SymbolId(SymbolRanges.CustomRangeStart + 1234);
        var firstRule = OneOrMore(OneOf(TokenSet.Letters)).As(pinned).As("first");
        var secondRule = OneOrMore(OneOf(TokenSet.Digits)).As(pinned).As("second");
        var doc = And(firstRule, secondRule);

        var exception = Assert.Throws<InvalidOperationException>(() => doc.Compile());
        Assert.That(exception!.Message, Does.Contain(pinned.Value.ToString()));
        Assert.That(exception.Message, Does.Contain("first"));
        Assert.That(exception.Message, Does.Contain("second"));
    }

    [Test]
    public void Same_pinned_rule_referenced_twice_in_a_grammar_compiles()
    {
        // Reachability is per-rule, not per-edge. A single rule reached via
        // two parents is still one rule, so its pin shouldn't be flagged.
        var pinned = new SymbolId(SymbolRanges.CustomRangeStart + 4321);
        var sharedRule = OneOrMore(OneOf(TokenSet.Letters)).As(pinned);
        var doc = And(sharedRule, sharedRule);

        Assert.DoesNotThrow(() => doc.Compile());
        Assert.That(sharedRule.Id, Is.EqualTo(pinned));
    }

    [Test]
    public void As_SymbolId_rejects_pins_below_the_custom_range()
    {
        // The rune range (0..0x10FFFF) and built-in range
        // (0x110000..0x1FFFFF) are reserved. Pinning a rule into either
        // would silently collide with Token('a')-style rune leaves or
        // future built-in ids, and the Compile-time duplicate-pin check
        // can only catch user pins, not auto-pins from constructors. As
        // throws at the call site so the bad pin never reaches the
        // grammar.
        var rule = OneOrMore(OneOf(TokenSet.Letters));

        // Rune-range pin: collides with Token('a') if both are in the
        // same grammar.
        Assert.Throws<ArgumentOutOfRangeException>(() => rule.As(new SymbolId(0x61)));

        // Built-in gap pin.
        Assert.Throws<ArgumentOutOfRangeException>(() => rule.As(new SymbolId(0x110000)));

        // Negative pin: no meaningful identity.
        Assert.Throws<ArgumentOutOfRangeException>(() => rule.As(new SymbolId(-1)));

        // The boundary value (CustomRangeStart itself) is the first
        // legal pin.
        Assert.DoesNotThrow(() => rule.As(new SymbolId(SymbolRanges.CustomRangeStart)));
    }

    [Test]
    public void Pre_compiled_sub_rule_pin_collides_with_unsealed_sibling_pin()
    {
        // Two distinct reachable rules in the same grammar both pin the
        // same custom-range SymbolId. One was compiled standalone first,
        // so it's already sealed by the time the larger grammar reaches
        // it. The second was pinned but isn't sealed yet. Compile of the
        // larger grammar should catch this just like the all-unsealed
        // version (Two_reachable_rules_pinned_to_the_same_SymbolId_fail_to_compile),
        // because the second rule's pin would otherwise silently shadow
        // the first one's: Tree.Find against either rule reference would
        // return the same nodes regardless of which rule actually
        // matched. Pre-compiling one branch is a real pattern when a
        // shared identifier rule lives in a library and gets reused
        // across multiple grammars.
        var pinned = new SymbolId(SymbolRanges.CustomRangeStart + 5678);
        var preCompiled = OneOrMore(OneOf(TokenSet.Letters)).As(pinned).As("first");
        preCompiled.Compile();

        var unsealed = OneOrMore(OneOf(TokenSet.Digits)).As(pinned).As("second");
        var doc = And(preCompiled, unsealed);

        var exception = Assert.Throws<InvalidOperationException>(() => doc.Compile());
        Assert.That(exception!.Message, Does.Contain(pinned.Value.ToString()));
        Assert.That(exception.Message, Does.Contain("first"));
        Assert.That(exception.Message, Does.Contain("second"));
    }

    // Each test below builds a small grammar with a specific arrangement
    // of pinned, named, and anonymous leaves, compiles it, and asserts
    // the exact id every leaf comes out with. The model-based sweep at
    // the bottom of the file covers the same algorithm across a generated
    // parameter space. These explicit cases exist as readable
    // documentation of the corners the algorithm has to handle.

    [Test]
    public void Anonymous_ids_fill_lowest_unclaimed_slots_above_a_dense_pin_block()
    {
        // Pin the four bottom slots of the custom range (+0 through +3),
        // then add three anonymous leaves. Compile's anonymous pass has
        // to probe past the whole pin block to find ids for them, so the
        // anonymous leaves should land at +4, +5, +6.
        var specs = new[]
        {
            RuleSpec.PinnedLeaf(0),
            RuleSpec.PinnedLeaf(1),
            RuleSpec.PinnedLeaf(2),
            RuleSpec.PinnedLeaf(3),
            RuleSpec.AnonymousLeaf(),
            RuleSpec.AnonymousLeaf(),
            RuleSpec.AnonymousLeaf(),
        };
        var grammar = IdAssignmentReferenceModel.BuildGrammar(specs, out var leaves);
        grammar.Compile();

        int start = SymbolRanges.CustomRangeStart;
        Assert.That(leaves[4].Id.Value, Is.EqualTo(start + 4));
        Assert.That(leaves[5].Id.Value, Is.EqualTo(start + 5));
        Assert.That(leaves[6].Id.Value, Is.EqualTo(start + 6));
    }

    [Test]
    public void Anonymous_ids_fill_gaps_between_pinned_slots()
    {
        // Pin two leaves at +0 and +2, leaving a gap at +1, then add
        // three anonymous leaves. The first anonymous leaf should slot
        // into the gap at +1, and the next two should probe past +2 to
        // land at +3 and +4.
        var specs = new[]
        {
            RuleSpec.PinnedLeaf(0),
            RuleSpec.PinnedLeaf(2),
            RuleSpec.AnonymousLeaf(),
            RuleSpec.AnonymousLeaf(),
            RuleSpec.AnonymousLeaf(),
        };
        var grammar = IdAssignmentReferenceModel.BuildGrammar(specs, out var leaves);
        grammar.Compile();

        int start = SymbolRanges.CustomRangeStart;
        Assert.That(leaves[2].Id.Value, Is.EqualTo(start + 1));
        Assert.That(leaves[3].Id.Value, Is.EqualTo(start + 3));
        Assert.That(leaves[4].Id.Value, Is.EqualTo(start + 4));
    }

    [Test]
    public void Anonymous_ids_skip_a_pin_at_a_high_offset()
    {
        // Pin one leaf far above the bottom of the range, at +10, and
        // follow it with twelve anonymous leaves. The anonymous pass
        // should fill +0 through +9 sequentially, hit the +10 pin, skip
        // it, and continue at +11 and +12. The point of the test is that
        // the anonymous counter is shared across all leaves rather than
        // restarting from the bottom for each one.
        var specs = new List<RuleSpec> { RuleSpec.PinnedLeaf(10) };
        for (int i = 0; i < 12; i++) specs.Add(RuleSpec.AnonymousLeaf());

        var grammar = IdAssignmentReferenceModel.BuildGrammar(specs, out var leaves);
        grammar.Compile();

        int start = SymbolRanges.CustomRangeStart;
        var anonymousIds = new List<int>();
        for (int i = 1; i < leaves.Length; i++) anonymousIds.Add(leaves[i].Id.Value);

        var expected = new List<int>();
        for (int offset = 0; offset <= 9; offset++) expected.Add(start + offset);
        expected.Add(start + 11);
        expected.Add(start + 12);

        Assert.That(anonymousIds, Is.EqualTo(expected));
    }

    [Test]
    public void Named_rule_probes_past_one_pin_at_its_hash_slot()
    {
        // First, compute the slot the name "collisionTest" hashes to.
        // Pin one leaf there, then add a second leaf named
        // "collisionTest". Pass 1 claims the slot for the pinned leaf,
        // so when pass 2 tries to place the named leaf at the same slot
        // it finds the slot already taken and probes upward to slot+1.
        const string name = "collisionTest";
        int slot = Rule.HashNameToCustomRange(name);

        var pinnedLeaf = OneOf(TokenSet.Letters).As(new SymbolId(slot));
        var namedLeaf = OneOf(TokenSet.Letters).As(name);
        var grammar = And(pinnedLeaf, namedLeaf);
        grammar.Compile();

        Assert.That(pinnedLeaf.Id.Value, Is.EqualTo(slot));
        Assert.That(namedLeaf.Id.Value, Is.EqualTo(slot + 1));
    }

    [Test]
    public void Named_rule_probes_past_a_run_of_pins_at_its_hash_slot()
    {
        // Same idea as the previous test, but pin three leaves
        // contiguously starting at the named rule's hash slot. The named
        // leaf has to probe past all three pins to land at slot+3.
        const string name = "collisionTest";
        int slot = Rule.HashNameToCustomRange(name);

        var pin0 = OneOf(TokenSet.Letters).As(new SymbolId(slot));
        var pin1 = OneOf(TokenSet.Letters).As(new SymbolId(slot + 1));
        var pin2 = OneOf(TokenSet.Letters).As(new SymbolId(slot + 2));
        var namedLeaf = OneOf(TokenSet.Letters).As(name);
        var grammar = And(pin0, pin1, pin2, namedLeaf);
        grammar.Compile();

        Assert.That(namedLeaf.Id.Value, Is.EqualTo(slot + 3));
    }

    // Drives the parameterized sweep. Each case builds a grammar from a
    // generated spec list, compiles it, and checks that every leaf's id
    // matches what the reference model predicts. A change to the
    // algorithm in Rule.cs will fail this on every affected case, so
    // either the change is fixed or the model is updated deliberately.
    [TestCaseSource(nameof(IdAssignmentSweepCases))]
    public void Compiled_ids_match_the_reference_model(string label, RuleSpec[] specs)
    {
        var grammar = IdAssignmentReferenceModel.BuildGrammar(specs, out var leaves);
        grammar.Compile();

        var predicted = IdAssignmentReferenceModel.PredictIds(specs);

        for (int i = 0; i < specs.Length; i++)
        {
            Assert.That(
                leaves[i].Id.Value,
                Is.EqualTo(predicted[i]),
                $"Case '{label}', leaf {i} ({specs[i].Role}): " +
                $"actual {leaves[i].Id.Value}, predicted {predicted[i]}");
        }

        // Universal property: every assigned id is unique across the
        // grammar (root + leaves) and lives in the custom range.
        var allIds = new HashSet<int> { grammar.Id.Value };
        for (int i = 0; i < leaves.Length; i++)
        {
            Assert.That(
                allIds.Add(leaves[i].Id.Value),
                Is.True,
                $"Case '{label}': duplicate id {leaves[i].Id.Value} on leaf {i}");
            Assert.That(
                leaves[i].Id.Value,
                Is.GreaterThanOrEqualTo(SymbolRanges.CustomRangeStart),
                $"Case '{label}': leaf {i} id below custom range");
        }

        // Pinned rules keep their pin.
        for (int i = 0; i < specs.Length; i++)
        {
            if (specs[i].Role == RuleRole.Pinned)
            {
                int expectedPin = SymbolRanges.CustomRangeStart + specs[i].PinOffsetFromCustomStart!.Value;
                Assert.That(
                    leaves[i].Id.Value,
                    Is.EqualTo(expectedPin),
                    $"Case '{label}': pinned leaf {i} drifted off its pin");
            }
        }
    }

    // Generates every combination over a small parameter space: pin
    // offsets are any subset of {0..5} relative to CustomRangeStart (64
    // subsets), named-rule count is 0, 1, or 2 drawn in order from
    // {alpha, beta}, and anonymous-rule count goes 0 through 4. The
    // fully-empty case is skipped because And rejects an empty child
    // list.
    private static IEnumerable<TestCaseData> IdAssignmentSweepCases()
    {
        int[] pinOffsets = { 0, 1, 2, 3, 4, 5 };
        string[] namePool = { "alpha", "beta" };

        for (int pinMask = 0; pinMask < (1 << pinOffsets.Length); pinMask++)
        {
            for (int namedCount = 0; namedCount <= namePool.Length; namedCount++)
            {
                for (int anonymousCount = 0; anonymousCount <= 4; anonymousCount++)
                {
                    if (pinMask == 0 && namedCount == 0 && anonymousCount == 0) continue;

                    var specs = new List<RuleSpec>();
                    for (int bit = 0; bit < pinOffsets.Length; bit++)
                    {
                        if ((pinMask & (1 << bit)) != 0)
                            specs.Add(RuleSpec.PinnedLeaf(pinOffsets[bit]));
                    }
                    for (int n = 0; n < namedCount; n++)
                        specs.Add(RuleSpec.NamedLeaf(namePool[n]));
                    for (int a = 0; a < anonymousCount; a++)
                        specs.Add(RuleSpec.AnonymousLeaf());

                    string label = $"pins=0x{pinMask:X2} named={namedCount} anon={anonymousCount}";
                    yield return new TestCaseData(label, specs.ToArray()).SetName(label);
                }
            }
        }
    }
}
