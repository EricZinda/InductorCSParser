using System;
using System.Collections.Generic;
using NUnit.Framework;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser.Tests;

[TestFixture]
public class SymbolImmutabilityTests
{
    [Test]
    public void Composite_symbol_copies_caller_owned_children_list()
    {
        var original = Leaf(1, "a");
        var replacement = Leaf(2, "z");
        var children = new List<Symbol> { original };

        var composite = new Symbol(new SymbolId(3), FlattenType.Preserve, children);

        children[0] = replacement;

        Assert.That(composite.Children, Is.Not.SameAs(children));
        Assert.That(composite.Children, Is.Not.InstanceOf<List<Symbol>>());
        Assert.That(composite.Children[0], Is.SameAs(original));
    }

    [Test]
    public void Composite_symbol_copies_caller_owned_children_array()
    {
        var original = Leaf(1, "a");
        var replacement = Leaf(2, "z");
        var children = new[] { original };

        var composite = new Symbol(new SymbolId(3), FlattenType.Preserve, children);

        children[0] = replacement;

        Assert.That(composite.Children as Symbol[], Is.Null);
        Assert.That(composite.Children[0], Is.SameAs(original));
    }

    [Test]
    public void Flatten_returns_read_only_list()
    {
        var leaf = Leaf(1, "a");
        var replacement = Leaf(2, "z");

        var flattened = leaf.Flatten();

        Assert.That(flattened, Is.Not.InstanceOf<List<Symbol>>());
        Assert.That(flattened as Symbol[], Is.Null);

        var writable = flattened as IList<Symbol>;
        Assert.That(writable, Is.Not.Null);
        Assert.That(writable!.IsReadOnly, Is.True);
        Assert.Throws<NotSupportedException>(() => { writable[0] = replacement; });
        Assert.That(flattened[0], Is.SameAs(leaf));
    }

    [Test]
    public void Custom_rule_helper_publishes_non_castable_read_only_children()
    {
        var rule = new CompositeBuildingRule();
        var children = new List<Symbol> { Leaf(1, "a") };

        var composite = rule.BuildComposite(children);

        // CreateCompositeFromOwnedChildren wraps the caller's list without
        // copying (the rule promised it's freshly built and won't be touched
        // again), but still publishes it through a non-castable read-only
        // surface. A custom rule using the helper can't accidentally hand a
        // consumer a tree they could mutate by casting Children back.
        Assert.That(composite.Children, Is.Not.InstanceOf<List<Symbol>>());
        Assert.That(composite.Children as Symbol[], Is.Null);

        var writable = composite.Children as IList<Symbol>;
        Assert.That(writable, Is.Not.Null);
        Assert.That(writable!.IsReadOnly, Is.True);
        Assert.Throws<NotSupportedException>(() => { writable[0] = Leaf(2, "z"); });
        Assert.That(composite.Children[0], Is.SameAs(children[0]));
    }

    private static Symbol Leaf(int id, string text) =>
        new Symbol(new SymbolId(id), FlattenType.Preserve, text.AsMemory());

    // A user-defined Rule, living in this separate test assembly, that builds
    // a composite Symbol through the protected CreateCompositeFromOwnedChildren
    // helper. Its mere existence proves the helper is reachable from a Rule
    // subclass outside InductorParser. BuildComposite exposes the result so the
    // test can inspect the published Children.
    private sealed class CompositeBuildingRule : Rule
    {
        public CompositeBuildingRule() : base(FlattenType.Preserve, emitsLeaf: false)
        {
        }

        // Never exercised: the test calls BuildComposite directly rather than
        // running a parse, so this only has to satisfy the abstract member.
        protected override Symbol? TryParseRule(Lexer lexer, int startPosition, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols) =>
            null;

        public Symbol BuildComposite(IReadOnlyList<Symbol> children) =>
            CreateCompositeFromOwnedChildren(children, default, null);
    }
}
