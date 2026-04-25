using System;
using System.Linq;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Parse-time Delete filtering. Rules whose effective FlattenType is Delete
// return the shared Symbol.Discarded value from TryParse, and composite
// parents (And / Or / BetweenInclusive) filter it out of their
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

        // Nothing survives into the tree. OptionalWhitespace is a
        // FlattenType.Flatten wrapper around FlattenType.Delete
        // children, so the parse-time filter drops every whitespace
        // rune and the root's Symbols list comes back empty.
        Assert.That(result.Symbols, Is.Empty);
    }

    [Test]
    public void OptionalWhitespace_inside_composite_leaves_no_whitespace_children()
    {
        // The realistic JSON-style shape: OptionalWhitespace sits between
        // two tokens inside an And. The top-level Symbols list should hold
        // the two token leaves only, with the whitespace contributing
        // nothing. OneOf has FlattenType.Preserve so the token leaves
        // survive. Their Id is the code point, so we assert on that.
        var letter = OneOf(RuneSet.Ascii.Letters);
        var rule = And(letter, OptionalWhitespace(), letter);
        var result = rule.Parse("a   b");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Symbols.Count, Is.EqualTo(2));
        Assert.That(result.Symbols[0].Id.Value, Is.EqualTo('a'));
        Assert.That(result.Symbols[1].Id.Value, Is.EqualTo('b'));
        // Concatenated text reflects what's actually in the tree: the two
        // letter leaves. The OptionalWhitespace was filtered at parse time,
        // so its text doesn't appear here. Callers who want the full
        // matched input should keep their own reference to it or run with
        // PreserveAllSymbols=true.
        Assert.That(string.Concat(result.Symbols), Is.EqualTo("ab"));
    }

    [Test]
    public void Default_Delete_leaf_rules_return_the_shared_Discarded_value()
    {
        // Token, Not, Peek, Eof all default to FlattenType.Delete. Each
        // matches and contributes nothing at parse time, so a Delete rule
        // at the root produces an empty Symbols list.
        var charResult = Token('x').Parse("x");
        Assert.That(charResult.Success, Is.True);
        Assert.That(charResult.Symbols, Is.Empty);

        var notResult = And(Not(Token('y')), Token('x')).Parse("x");
        // Top-level And holds no children because both its children were
        // Discarded. And is Flatten, so its children bubble up to the
        // root list, which is empty since the children were Discarded.
        Assert.That(notResult.Success, Is.True);
        Assert.That(notResult.Symbols, Is.Empty);

        var peekResult = And(Peek(Token('x')), Token('x')).Parse("x");
        Assert.That(peekResult.Success, Is.True);
        Assert.That(peekResult.Symbols, Is.Empty);
    }

    [Test]
    public void PreserveAllSymbols_disables_parse_time_Delete_filtering()
    {
        // Same grammar as the leaves-filter test, but with the debug
        // flag on: the Token wrappers around and between the letters
        // should survive into the tree so PrintTree and Find queries
        // see a shape that matches the grammar as written.
        var rule = And(Token('a'), OptionalWhitespace(), Token('b'));
        var options = new ParseOptions { PreserveAllSymbols = true };
        var result = rule.Parse("a   b", options);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        // Three children: Token('a'), OptionalWhitespace wrapper, Token('b').
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
        var kept = new Symbol(new SymbolId(2), FlattenType.Preserve, "y".AsMemory());
        var composite = new Symbol(new SymbolId(3), FlattenType.Preserve,
            new Symbol[] { leaf, kept });

        var flattened = composite.Flatten();
        Assert.That(flattened.Count, Is.EqualTo(1));
        Assert.That(flattened[0].Id.Value, Is.EqualTo(3));
        Assert.That(flattened[0].Children.Count, Is.EqualTo(1));
        Assert.That(flattened[0].Children[0].Id.Value, Is.EqualTo(2));
    }
}
