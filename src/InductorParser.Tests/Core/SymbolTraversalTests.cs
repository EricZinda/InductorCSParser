using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using InductorParser.SyntaxTree;

namespace InductorParser.Tests;

// Symbol.Walk() and Symbol.FindAll() were rewritten from recursive
// yield-return (O(nodes x depth), quadratic on a deep tree) to an explicit
// stack-based pre-order walk (O(nodes)). The rewrite has to yield the exact
// same nodes in the exact same order as the recursive form. These tests pin
// that order against a reference recursive implementation across several tree
// shapes, including the deep chain that motivated the change. The win itself
// is a performance property and isn't asserted as a timing here.
[TestFixture]
public class SymbolTraversalTests
{
    // The recursive form Walk/FindAll used to have, kept as the oracle.
    private static IEnumerable<Symbol> ReferenceWalk(Symbol node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in ReferenceWalk(child))
                yield return descendant;
    }

    private static IEnumerable<Symbol> ReferenceFindAll(Symbol node, SymbolId id)
    {
        if (node.Id == id) yield return node;
        foreach (var child in node.Children)
            foreach (var found in ReferenceFindAll(child, id))
                yield return found;
    }

    private static Symbol Leaf(int id) =>
        new Symbol(new SymbolId(id), FlattenType.Preserve, ReadOnlyMemory<char>.Empty, context: null);

    private static Symbol Composite(int id, params Symbol[] children) =>
        new Symbol(new SymbolId(id), FlattenType.Preserve, children, default, context: null);

    private static void AssertWalkMatchesReference(Symbol root)
    {
        var actual = root.Walk().ToArray();
        var expected = ReferenceWalk(root).ToArray();
        Assert.That(actual.Length, Is.EqualTo(expected.Length), "node count");
        for (int i = 0; i < expected.Length; i++)
            Assert.That(ReferenceEquals(actual[i], expected[i]), Is.True,
                $"Walk node #{i} (id {actual[i].Id.Value}) differs from reference (id {expected[i].Id.Value})");
    }

    private static void AssertFindAllMatchesReference(Symbol root, SymbolId id)
    {
        var actual = root.FindAll(id).ToArray();
        var expected = ReferenceFindAll(root, id).ToArray();
        Assert.That(actual.Length, Is.EqualTo(expected.Length), $"match count for id {id.Value}");
        for (int i = 0; i < expected.Length; i++)
            Assert.That(ReferenceEquals(actual[i], expected[i]), Is.True,
                $"FindAll node #{i} differs from reference for id {id.Value}");
    }

    [Test]
    public void Walk_single_leaf()
    {
        AssertWalkMatchesReference(Leaf(1));
    }

    [Test]
    public void Walk_wide_shallow_tree()
    {
        var root = Composite(1, Leaf(2), Leaf(3), Leaf(4), Leaf(5), Leaf(6));
        AssertWalkMatchesReference(root);
    }

    [Test]
    public void Walk_deep_chain_preserves_preorder()
    {
        // The shape the rewrite is about: one child per node, deep.
        Symbol node = Leaf(0);
        for (int i = 1; i <= 500; i++)
            node = Composite(i, node);
        AssertWalkMatchesReference(node);
        // Pre-order on a chain visits the outermost node first, then descends.
        var ids = node.Walk().Select(s => s.Id.Value).ToArray();
        Assert.That(ids[0], Is.EqualTo(500), "outermost node yielded first");
        Assert.That(ids[^1], Is.EqualTo(0), "innermost leaf yielded last");
    }

    [Test]
    public void Walk_mixed_tree_children_left_to_right()
    {
        // Deterministic mixed tree: varied branching and depth.
        var root = Composite(1,
            Composite(2, Leaf(3), Leaf(4)),
            Leaf(5),
            Composite(6,
                Composite(7, Leaf(8)),
                Leaf(9)));
        AssertWalkMatchesReference(root);
        Assert.That(root.Walk().Select(s => s.Id.Value).ToArray(),
            Is.EqualTo(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }));
    }

    [Test]
    public void FindAll_matches_reference_order_with_repeats_at_varied_depth()
    {
        // Id 7 appears three times at different depths and positions. The
        // iterative walk must surface them in the same pre-order the
        // recursive form did.
        var root = Composite(1,
            Composite(7, Leaf(7)),
            Leaf(2),
            Composite(3, Composite(4, Leaf(7))));
        AssertFindAllMatchesReference(root, new SymbolId(7));
        Assert.That(root.FindAll(new SymbolId(7)).Count(), Is.EqualTo(3));
    }

    [Test]
    public void FindAll_no_match_yields_empty()
    {
        var root = Composite(1, Leaf(2), Leaf(3));
        Assert.That(root.FindAll(new SymbolId(999)).ToArray(), Is.Empty);
    }

    [Test]
    public void FindAll_matches_root_itself()
    {
        var root = Composite(5, Leaf(6));
        AssertFindAllMatchesReference(root, new SymbolId(5));
        Assert.That(root.FindAll(new SymbolId(5)).Single(), Is.SameAs(root));
    }
}
