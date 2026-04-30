using System.Collections.Generic;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// A leaf is Pinned (.As(SymbolId)), Named (.As(string)), or Anonymous
// (no .As call, picks up an id from pass 3).
public enum RuleRole { Pinned, Named, Anonymous }

// Describes one leaf in a generated test grammar. Pin offsets are stored
// relative to SymbolRanges.CustomRangeStart so test cases read clearly:
// PinnedLeaf(0) means "first slot in the custom range" instead of a giant
// absolute number that depends on what CustomRangeStart happens to equal.
public sealed record RuleSpec(RuleRole Role, int? PinOffsetFromCustomStart, string? Name)
{
    public static RuleSpec PinnedLeaf(int offsetFromCustomStart) =>
        new(RuleRole.Pinned, offsetFromCustomStart, null);

    public static RuleSpec NamedLeaf(string name) =>
        new(RuleRole.Named, null, name);

    public static RuleSpec AnonymousLeaf() =>
        new(RuleRole.Anonymous, null, null);
}

// Independent reimplementation of Rule.Compile's three-pass id assignment.
// The parameterized sweep in IdAssignmentTests builds a grammar from each
// spec list two ways, through the production code (BuildGrammar then
// Compile) and through this model (PredictIds), and asserts the assigned
// ids match. So this file is the spec the production code is checked
// against. If you intentionally change the algorithm in Rule.cs, you'll
// need to update this model to match.
public static class IdAssignmentReferenceModel
{
    // Far enough above the sweep's pin offsets (0..5) and the named-rule
    // hash slots that it never collides with anything the sweep cares
    // about. The actual value doesn't matter as long as it's well outside
    // the test's working range.
    public const int RootPinOffset = 999_999;

    // Compute the id Rule.Compile should assign to each spec, in the same
    // order the spec list is in.
    public static int[] PredictIds(IReadOnlyList<RuleSpec> specs)
    {
        var result = new int[specs.Count];

        // Record the AllOf root's pin as used. The passes below only
        // iterate the spec list, so they wouldn't see the root otherwise.
        var usedIds = new HashSet<int>
        {
            SymbolRanges.CustomRangeStart + RootPinOffset
        };

        // Pass 1: claim every pinned id up front so passes 2 and 3 will
        // probe past them.
        for (int i = 0; i < specs.Count; i++)
        {
            if (specs[i].Role == RuleRole.Pinned)
            {
                int id = SymbolRanges.CustomRangeStart + specs[i].PinOffsetFromCustomStart!.Value;
                result[i] = id;
                usedIds.Add(id);
            }
        }

        // Pass 2: each named rule starts at its name's hash slot and
        // probes upward past any slot pass 1 already grabbed. Same hash
        // function the production code uses, so a name lands on the same
        // slot in both.
        for (int i = 0; i < specs.Count; i++)
        {
            if (specs[i].Role == RuleRole.Named)
            {
                int slot = Rule.HashNameToCustomRange(specs[i].Name!);
                while (!usedIds.Add(slot)) slot++;
                result[i] = slot;
            }
        }

        // Pass 3: anonymous rules fill from the bottom of the custom range
        // upward, skipping anything passes 1 and 2 already used. nextAnon
        // is shared across every anonymous rule in the grammar, so they
        // get distinct increasing ids and the probe never walks back over
        // ground it already covered.
        int nextAnon = SymbolRanges.CustomRangeStart;
        for (int i = 0; i < specs.Count; i++)
        {
            if (specs[i].Role == RuleRole.Anonymous)
            {
                while (!usedIds.Add(nextAnon)) nextAnon++;
                result[i] = nextAnon;
                nextAnon++;
            }
        }

        return result;
    }

    // The `leaves` out-parameter hands each leaf back in spec order so
    // callers can assert per-leaf ids by index instead of walking the
    // returned grammar.
    public static Rule BuildGrammar(IReadOnlyList<RuleSpec> specs, out Rule[] leaves)
    {
        leaves = new Rule[specs.Count];
        for (int i = 0; i < specs.Count; i++)
        {
            // OneOf has no Rule children, so each leaf is one rule with
            // nothing hidden underneath. That's what keeps the spec list
            // one-to-one with the rules the production passes assign and
            // makes the model's predictions match what Compile produces.
            Rule leaf = OneOf(RuneSet.Letters);
            switch (specs[i].Role)
            {
                case RuleRole.Pinned:
                    leaf = leaf.As(new SymbolId(SymbolRanges.CustomRangeStart + specs[i].PinOffsetFromCustomStart!.Value));
                    break;
                case RuleRole.Named:
                    leaf = leaf.As(specs[i].Name!);
                    break;
            }
            leaves[i] = leaf;
        }

        // Pin the AllOf root at RootPinOffset so it doesn't claim an
        // anonymous id from the low end of the custom range and shift the
        // expected ids of every anonymous leaf below it.
        return AllOf(leaves).As(new SymbolId(SymbolRanges.CustomRangeStart + RootPinOffset));
    }
}
