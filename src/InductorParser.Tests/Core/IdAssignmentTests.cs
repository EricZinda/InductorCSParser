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
    public void Explicit_id_survives_compile()
    {
        var explicitId = new SymbolId(SymbolRanges.CustomRangeStart + 9999);
        var rule = OneOrMore(OneOf(TokenSet.Letters)).As(explicitId);
        rule.Compile();

        Assert.That(rule.Id, Is.EqualTo(explicitId));
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
    public void Two_reachable_rules_given_the_same_explicit_SymbolId_fail_to_compile()
    {
        var explicitId = new SymbolId(SymbolRanges.CustomRangeStart + 1234);
        var firstRule = OneOrMore(OneOf(TokenSet.Letters)).As(explicitId).As("first");
        var secondRule = OneOrMore(OneOf(TokenSet.Digits)).As(explicitId).As("second");
        var doc = And(firstRule, secondRule);

        var exception = Assert.Throws<InvalidOperationException>(() => doc.Compile());
        Assert.That(exception!.Message, Does.Contain(explicitId.Value.ToString()));
        Assert.That(exception.Message, Does.Contain("first"));
        Assert.That(exception.Message, Does.Contain("second"));
    }

    [Test]
    public void Two_named_single_rune_Tokens_of_the_same_rune_get_distinct_ids()
    {
        // Token('a') auto-assigns its Id to the rune's code point in the
        // GraphemeRule constructor (Id == 0x61). Adding .As("first") /
        // .As("second") names each rule but used to leave that
        // auto-assigned id in place, so both rules silently shared
        // SymbolId(0x61). Two rules given the same explicit id throw at
        // compile, but the auto-assignment path bypassed that check.
        // Tree.Find against either
        // rule reference then returned the same node regardless of which
        // one actually matched, and NameOf could only report one of the
        // two names back. Naming a single-rune Token should give it a
        // fresh custom-range id so each named rule is uniquely findable.
        var firstA = Token('a').As("first");
        var secondA = Token('a').As("second");
        var doc = And(firstA, secondA);

        doc.Compile();

        Assert.That(firstA.Id, Is.Not.EqualTo(secondA.Id),
            "Two named Token('a') rules must get distinct ids so Tree.Find can disambiguate them.");
        Assert.That(firstA.Id.Value, Is.GreaterThanOrEqualTo(SymbolRanges.CustomRangeStart),
            "A named Token should land in the custom range, not the rune range.");
        Assert.That(secondA.Id.Value, Is.GreaterThanOrEqualTo(SymbolRanges.CustomRangeStart),
            "A named Token should land in the custom range, not the rune range.");
    }

    [Test]
    public void Named_Token_leaves_carry_the_rule_id_not_the_rune_id()
    {
        // Companion to Two_named_single_rune_Tokens_of_the_same_rune_get_distinct_ids.
        // The parse-tree leaf a named single-rune Token emits has to
        // carry the rule's (new custom-range) id, otherwise Tree.Find
        // against the rule wouldn't reach it. The leaf id is what tree
        // walkers compare against, so the rule.Id == leaf.Id symmetry
        // is what makes the distinct-id fix observable.
        var firstA = Token('a').As("first");
        var secondA = Token('a').As("second");
        var doc = And(firstA, secondA);
        var result = doc.Parse("aa");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        var foundFirst = result.Find(firstA);
        var foundSecond = result.Find(secondA);
        Assert.That(foundFirst, Is.Not.Null);
        Assert.That(foundSecond, Is.Not.Null);
        Assert.That(ReferenceEquals(foundFirst, foundSecond), Is.False,
            "Tree.Find(firstA) and Tree.Find(secondA) must return different leaves.");
    }

    [Test]
    public void Same_explicit_id_rule_referenced_twice_in_a_grammar_compiles()
    {
        // Reachability is per-rule, not per-edge. A single rule reached via
        // two parents is still one rule, so its explicit id shouldn't be
        // flagged.
        var explicitId = new SymbolId(SymbolRanges.CustomRangeStart + 4321);
        var sharedRule = OneOrMore(OneOf(TokenSet.Letters)).As(explicitId);
        var doc = And(sharedRule, sharedRule);

        Assert.DoesNotThrow(() => doc.Compile());
        Assert.That(sharedRule.Id, Is.EqualTo(explicitId));
    }

    [Test]
    public void As_SymbolId_rejects_explicit_ids_below_the_custom_range()
    {
        // The rune range (0..0x10FFFF) and built-in range
        // (0x110000..0x1FFFFF) are reserved. Setting an explicit id into
        // either would silently collide with Token('a')-style rune leaves
        // or future built-in ids, and the Compile-time duplicate-id check
        // can only catch the user's explicit ids, not the ids
        // auto-assigned by constructors. As throws at the call so the bad
        // id never reaches the grammar.
        var rule = OneOrMore(OneOf(TokenSet.Letters));

        // Rune-range id: collides with Token('a') if both are in the
        // same grammar.
        Assert.Throws<ArgumentOutOfRangeException>(() => rule.As(new SymbolId(0x61)));

        // Built-in gap id.
        Assert.Throws<ArgumentOutOfRangeException>(() => rule.As(new SymbolId(0x110000)));

        // Negative id: no meaningful identity.
        Assert.Throws<ArgumentOutOfRangeException>(() => rule.As(new SymbolId(-1)));

        // The boundary value (CustomRangeStart itself) is the first
        // legal explicit id.
        Assert.DoesNotThrow(() => rule.As(new SymbolId(SymbolRanges.CustomRangeStart)));
    }

    [Test]
    public void Pre_compiled_sub_rule_explicit_id_collides_with_unsealed_sibling()
    {
        // Two distinct reachable rules in the same grammar both set the
        // same custom-range SymbolId explicitly. One was compiled
        // standalone first, so it's already sealed by the time the larger
        // grammar reaches it. The second got its explicit id but isn't
        // sealed yet. Compile of the larger grammar should catch this just
        // like the all-unsealed version
        // (Two_reachable_rules_given_the_same_explicit_SymbolId_fail_to_compile),
        // because the second rule's id would otherwise silently shadow
        // the first one's: Tree.Find against either rule reference would
        // return the same nodes regardless of which rule actually
        // matched. Pre-compiling one branch is a real pattern when a
        // shared identifier rule lives in a library and gets reused
        // across multiple grammars.
        var explicitId = new SymbolId(SymbolRanges.CustomRangeStart + 5678);
        var preCompiled = OneOrMore(OneOf(TokenSet.Letters)).As(explicitId).As("first");
        preCompiled.Compile();

        var unsealed = OneOrMore(OneOf(TokenSet.Digits)).As(explicitId).As("second");
        var doc = And(preCompiled, unsealed);

        var exception = Assert.Throws<InvalidOperationException>(() => doc.Compile());
        Assert.That(exception!.Message, Does.Contain(explicitId.Value.ToString()));
        Assert.That(exception.Message, Does.Contain("first"));
        Assert.That(exception.Message, Does.Contain("second"));
    }

    // .As(string) and .As(SymbolId) each write a different field (Name and
    // Id), so they compose on a single instance: a rule can have both an
    // explicit id and a name without conflict. But each overload is set-once against
    // itself, because a second call to the same overload overwrites the
    // field the previous call set. The SemVer case was the motivating bug:
    // a shared OneOrMore(OneOf(digits)) rule chained .As("major") /
    // .As("minor") / .As("patch") across three positions, and all three
    // references ended up pointing at the same instance with Name="patch"
    // (last call wins). Tree.Find against any of the three then returned
    // the same node. The Compile-time duplicate-name check would catch the
    // collision when two reachable rules share a name, but only if the
    // user wrote two rule instances with the same name. Reusing one
    // instance hid the collision entirely.

    [Test]
    public void As_string_after_As_string_throws()
    {
        var rule = OneOrMore(OneOf(TokenSet.Digits)).As("major");

        var exception = Assert.Throws<InvalidOperationException>(() => rule.As("minor"));
        Assert.That(exception!.Message, Does.Contain("\"minor\""));
        Assert.That(exception.Message, Does.Contain("\"major\""));
        Assert.That(exception.Message, Does.Contain("set-once"));
    }

    [Test]
    public void As_SymbolId_after_As_SymbolId_throws()
    {
        var firstPin = new SymbolId(SymbolRanges.CustomRangeStart + 100);
        var secondPin = new SymbolId(SymbolRanges.CustomRangeStart + 200);
        var rule = OneOrMore(OneOf(TokenSet.Digits)).As(firstPin);

        var exception = Assert.Throws<InvalidOperationException>(() => rule.As(secondPin));
        Assert.That(exception!.Message, Does.Contain(secondPin.Value.ToString()));
        Assert.That(exception.Message, Does.Contain(firstPin.Value.ToString()));
        Assert.That(exception.Message, Does.Contain("set-once"));
    }

    [Test]
    public void Cross_overload_As_calls_compose_on_a_single_instance()
    {
        // .As(SymbolId) writes Id, .As(string) writes Name. Each field is
        // set-once but the two are independent. Calling one of each (in
        // either order) on the same rule produces a rule with both an
        // explicit id and a debug name, with no silent overwrite.
        var explicitId = new SymbolId(SymbolRanges.CustomRangeStart + 300);

        Assert.DoesNotThrow(() =>
        {
            var idThenName = OneOrMore(OneOf(TokenSet.Digits)).As(explicitId).As("number");
            Assert.That(idThenName.Id, Is.EqualTo(explicitId));
            Assert.That(idThenName.Name, Is.EqualTo("number"));
        });

        var otherExplicitId = new SymbolId(SymbolRanges.CustomRangeStart + 301);
        Assert.DoesNotThrow(() =>
        {
            var nameThenId = OneOrMore(OneOf(TokenSet.Digits)).As("count").As(otherExplicitId);
            Assert.That(nameThenId.Id, Is.EqualTo(otherExplicitId));
            Assert.That(nameThenId.Name, Is.EqualTo("count"));
        });
    }

    // Each test below builds a small grammar with a specific arrangement
    // of explicit-id, named, and anonymous leaves, compiles it, and asserts
    // the exact id every leaf comes out with. The model-based sweep at
    // the bottom of the file covers the same algorithm across a generated
    // parameter space. These explicit cases exist as readable
    // documentation of the corners the algorithm has to handle.

    [Test]
    public void Anonymous_ids_fill_lowest_unclaimed_slots_above_a_dense_explicit_id_block()
    {
        // Give explicit ids to the four bottom slots of the custom range
        // (+0 through +3), then add three anonymous leaves. Compile's
        // anonymous pass has to probe past the whole explicit-id block to
        // find ids for them, so the anonymous leaves should land at +4,
        // +5, +6.
        var specs = new[]
        {
            RuleSpec.ExplicitLeaf(0),
            RuleSpec.ExplicitLeaf(1),
            RuleSpec.ExplicitLeaf(2),
            RuleSpec.ExplicitLeaf(3),
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
    public void Anonymous_ids_fill_gaps_between_explicit_id_slots()
    {
        // Give explicit ids to two leaves at +0 and +2, leaving a gap at
        // +1, then add three anonymous leaves. The first anonymous leaf
        // should slot into the gap at +1, and the next two should probe
        // past +2 to land at +3 and +4.
        var specs = new[]
        {
            RuleSpec.ExplicitLeaf(0),
            RuleSpec.ExplicitLeaf(2),
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
    public void Anonymous_ids_skip_an_explicit_id_at_a_high_offset()
    {
        // Give an explicit id to one leaf far above the bottom of the
        // range, at +10, and follow it with twelve anonymous leaves. The
        // anonymous pass should fill +0 through +9 sequentially, hit the
        // +10 slot, skip it, and continue at +11 and +12. The point of
        // the test is that the anonymous counter is shared across all
        // leaves rather than restarting from the bottom for each one.
        var specs = new List<RuleSpec> { RuleSpec.ExplicitLeaf(10) };
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
    public void Named_rule_probes_past_one_explicit_id_at_its_hash_slot()
    {
        // First, compute the slot the name "collisionTest" hashes to.
        // Give one leaf an explicit id there, then add a second leaf named
        // "collisionTest". Pass 1 claims the slot for the explicit-id
        // leaf, so when pass 2 tries to place the named leaf at the same
        // slot it finds the slot already taken and probes upward to
        // slot+1.
        const string name = "collisionTest";
        int slot = Rule.HashNameToCustomRange(name);

        var explicitIdLeaf = OneOf(TokenSet.Letters).As(new SymbolId(slot));
        var namedLeaf = OneOf(TokenSet.Letters).As(name);
        var grammar = And(explicitIdLeaf, namedLeaf);
        grammar.Compile();

        Assert.That(explicitIdLeaf.Id.Value, Is.EqualTo(slot));
        Assert.That(namedLeaf.Id.Value, Is.EqualTo(slot + 1));
    }

    [Test]
    public void Named_rule_probes_past_a_run_of_explicit_ids_at_its_hash_slot()
    {
        // Same idea as the previous test, but give explicit ids to three
        // leaves contiguously starting at the named rule's hash slot. The
        // named leaf has to probe past all three to land at slot+3.
        const string name = "collisionTest";
        int slot = Rule.HashNameToCustomRange(name);

        var explicitLeaf0 = OneOf(TokenSet.Letters).As(new SymbolId(slot));
        var explicitLeaf1 = OneOf(TokenSet.Letters).As(new SymbolId(slot + 1));
        var explicitLeaf2 = OneOf(TokenSet.Letters).As(new SymbolId(slot + 2));
        var namedLeaf = OneOf(TokenSet.Letters).As(name);
        var grammar = And(explicitLeaf0, explicitLeaf1, explicitLeaf2, namedLeaf);
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

        // Explicit-id rules keep their explicit id.
        for (int i = 0; i < specs.Length; i++)
        {
            if (specs[i].Role == RuleRole.Explicit)
            {
                int expectedId = SymbolRanges.CustomRangeStart + specs[i].ExplicitOffsetFromCustomStart!.Value;
                Assert.That(
                    leaves[i].Id.Value,
                    Is.EqualTo(expectedId),
                    $"Case '{label}': explicit-id leaf {i} drifted off its id");
            }
        }
    }

    // Generates every combination over a small parameter space:
    // explicit-id offsets are any subset of {0..5} relative to
    // CustomRangeStart (64 subsets), named-rule count is 0, 1, or 2 drawn
    // in order from {alpha, beta}, and anonymous-rule count goes 0 through
    // 4. The fully-empty case is skipped because And rejects an empty
    // child list.
    private static IEnumerable<TestCaseData> IdAssignmentSweepCases()
    {
        int[] explicitOffsets = { 0, 1, 2, 3, 4, 5 };
        string[] namePool = { "alpha", "beta" };

        for (int explicitMask = 0; explicitMask < (1 << explicitOffsets.Length); explicitMask++)
        {
            for (int namedCount = 0; namedCount <= namePool.Length; namedCount++)
            {
                for (int anonymousCount = 0; anonymousCount <= 4; anonymousCount++)
                {
                    if (explicitMask == 0 && namedCount == 0 && anonymousCount == 0) continue;

                    var specs = new List<RuleSpec>();
                    for (int bit = 0; bit < explicitOffsets.Length; bit++)
                    {
                        if ((explicitMask & (1 << bit)) != 0)
                            specs.Add(RuleSpec.ExplicitLeaf(explicitOffsets[bit]));
                    }
                    for (int n = 0; n < namedCount; n++)
                        specs.Add(RuleSpec.NamedLeaf(namePool[n]));
                    for (int a = 0; a < anonymousCount; a++)
                        specs.Add(RuleSpec.AnonymousLeaf());

                    string label = $"explicit=0x{explicitMask:X2} named={namedCount} anon={anonymousCount}";
                    yield return new TestCaseData(label, specs.ToArray()).SetName(label);
                }
            }
        }
    }
}
