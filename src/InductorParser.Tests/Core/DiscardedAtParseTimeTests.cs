using System;
using System.Linq;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Parse-time Delete filtering. Rules whose effective FlattenType is Delete
// return the shared Symbol.Discarded sentinel from TryParse, and composite
// parents (And / Or / BetweenInclusive) filter the sentinel out of their
// Children list. The post-hoc FlattenInto Delete branch still runs for
// trees built by hand.
[TestFixture]
public class DiscardedAtParseTimeTests
{
    [Test]
    public void OptionalWhitespace_compile_then_parse_whitespace_produces_no_whitespace_nodes()
    {
        // The specific guarantee the performance story depends on: every
        // OptionalWhitespace match, with or without actual whitespace in
        // the input, costs zero wrapper and zero leaf Symbol allocations
        // that survive into the tree.
        var rule = OptionalWhitespace();
        rule.Compile();

        var result = rule.Parse("   ");
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        // The root of the tree is the Discarded sentinel itself — which
        // has no children and empty text. No per-rune leaves, no wrapper
        // Symbol for the ZeroOrMore.
        Assert.That(result.Tree, Is.SameAs(Symbol.Discarded));
        Assert.That(result.Tree!.Children, Is.Empty);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(string.Empty));
    }

    [Test]
    public void OptionalWhitespace_inside_composite_leaves_no_whitespace_children()
    {
        // The realistic JSON-style shape: OptionalWhitespace sits between
        // two tokens inside an And. The And's Children should hold the
        // two tokens only, with the whitespace contributing nothing.
        // RuneIn is None-typed so the token leaves survive; their Id is
        // the code point, so we assert on that.
        var letter = RuneIn(RuneSet.Ascii.Letters);
        var rule = And(letter, OptionalWhitespace(), letter);
        var result = rule.Parse("a   b");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.Children.Count, Is.EqualTo(2));
        Assert.That(result.Tree.Children[0].Id.Value, Is.EqualTo('a'));
        Assert.That(result.Tree.Children[1].Id.Value, Is.EqualTo('b'));
        // ToString reflects what's actually in the tree: the two letter
        // leaves concatenated. The OptionalWhitespace was filtered at
        // parse time, so its text doesn't appear here. Callers who want
        // the full matched input should keep their own reference to it
        // or run with PreserveFlattenWrappers=true.
        Assert.That(result.Tree.ToString(), Is.EqualTo("ab"));
    }

    [Test]
    public void Default_Delete_leaf_rules_return_the_shared_Discarded_sentinel()
    {
        // Char, Not, Peek, Eof all default to FlattenType.Delete. Each
        // matches and returns the same singleton, so composite callers
        // can filter via reference equality.
        var charResult = Char('x').Parse("x");
        Assert.That(charResult.Tree, Is.SameAs(Symbol.Discarded));

        var notResult = And(Not(Char('y')), Char('x')).Parse("x");
        // Top-level And holds no children because both its children were
        // Discarded.
        Assert.That(notResult.Success, Is.True);
        Assert.That(notResult.Tree!.Children, Is.Empty);

        var peekResult = And(Peek(Char('x')), Char('x')).Parse("x");
        Assert.That(peekResult.Success, Is.True);
        Assert.That(peekResult.Tree!.Children, Is.Empty);
    }

    [Test]
    public void PreserveFlattenWrappers_disables_parse_time_Delete_filtering()
    {
        // Same grammar as the leaves-filter test, but with the debug
        // flag on: the Char wrappers around and between the letters
        // should survive into the tree so PrintTree and Find queries
        // see a shape that matches the grammar as written.
        var rule = And(Char('a'), OptionalWhitespace(), Char('b'));
        var options = new ParseOptions { PreserveFlattenWrappers = true };
        var result = rule.Parse("a   b", options);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        // Three children: Char('a'), OptionalWhitespace wrapper, Char('b').
        // None were filtered as Discarded.
        Assert.That(result.Tree!.Children.Count, Is.EqualTo(3));
        Assert.That(result.Tree.Children[0].FlattenType, Is.EqualTo(FlattenType.Delete));
        Assert.That(result.Tree.Children[0].ToString(), Is.EqualTo("a"));
        Assert.That(result.Tree.Children[2].ToString(), Is.EqualTo("b"));
        // And the whitespace wrapper preserves its inner rune leaves too.
        Assert.That(result.Tree.Children[1].Children.Count, Is.EqualTo(3));
    }

    [Test]
    public void Post_hoc_FlattenInto_still_drops_Delete_typed_hand_built_subtrees()
    {
        // The Discarded path only fires on Rules produced by the parser.
        // A Symbol a caller built directly with FlattenType.Delete still
        // gets filtered by Symbol.FlattenInto (the original code path),
        // so code outside the parser that builds trees manually keeps
        // working exactly as before.
        var leaf = new Symbol(new SymbolId(1), FlattenType.Delete, "x".AsMemory());
        var kept = new Symbol(new SymbolId(2), FlattenType.None, "y".AsMemory());
        var composite = new Symbol(new SymbolId(3), FlattenType.None,
            new Symbol[] { leaf, kept });

        var flattened = composite.Flatten();
        Assert.That(flattened.Count, Is.EqualTo(1));
        Assert.That(flattened[0].Id.Value, Is.EqualTo(3));
        Assert.That(flattened[0].Children.Count, Is.EqualTo(1));
        Assert.That(flattened[0].Children[0].Id.Value, Is.EqualTo(2));
    }
}
