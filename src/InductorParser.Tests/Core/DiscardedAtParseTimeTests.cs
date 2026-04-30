using System;
using System.Linq;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.TestHelpers;

namespace InductorParser.Tests;

// Parse-time Delete filtering. Rules whose effective FlattenType is Delete
// return the shared Symbol.Discarded value from TryParse, and composite
// parents (AllOf / FirstOf / BetweenInclusive) filter it out of their
// Children list. That's the fast path: Delete symbols never make it
// into the tree, so most callers never need to think about flattening.
//
// Symbol.FlattenInto / Symbol.Flatten() are the post-hoc path. The
// real-user scenario is parsing with ParseOptions.PreserveAllSymbols
// = true: every rule wrapper survives into the tree carrying its real
// FlattenType, so a caller that wants the full debug shape and the
// collapsed shape from one parse can call .Flatten() on the preserved
// tree to get the latter back without re-parsing.
[TestFixture]
public class DiscardedAtParseTimeTests
{
    [Test]
    public void OptionalWhitespace_compile_then_parse_whitespace_produces_no_whitespace_nodes()
    {
        // A specific guarantee the performance story depends on: every
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
        // two tokens inside an AllOf. The top-level Symbols list should hold
        // the two token leaves only, with the whitespace contributing
        // nothing. OneOf has FlattenType.Preserve so the token leaves
        // survive. Their Id is the code point, so we assert on that.
        var letter = OneOf(RuneSet.Ascii.Letters);
        var rule = AllOf(letter, OptionalWhitespace(), letter);
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
        Assert.That(result.ToString(), Is.EqualTo("ab"));
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

        var notResult = AllOf(Not(Token('y')), Token('x')).Parse("x");
        // Top-level AllOf holds no children because both its children were
        // Discarded. AllOf is Flatten, so its children bubble up to the
        // root list, which is empty since the children were Discarded.
        Assert.That(notResult.Success, Is.True);
        Assert.That(notResult.Symbols, Is.Empty);

        var peekResult = AllOf(Peek(Token('x')), Token('x')).Parse("x");
        Assert.That(peekResult.Success, Is.True);
        Assert.That(peekResult.Symbols, Is.Empty);
    }

    [Test]
    public void PreserveAllSymbols_disables_parse_time_Delete_filtering()
    {
        // Same grammar as the leaves-filter test, but with the debug
        // flag on: every rule wrapper around and between the letters
        // should survive into the tree so PrintTree and Find queries
        // see a shape that matches the grammar as written. Comparing
        // the full PrintTree rendering in one shot is easier to read
        // than per-child assertions, and any drift shows up as a
        // string diff naming the exact node that moved.
        var rule = AllOf(Token('a'), OptionalWhitespace(), Token('b'));
        var options = new ParseOptions { PreserveAllSymbols = true };
        var result = rule.Parse("a   b", options);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.PrintTree(), Is.EqualTo(
            "AllOf: \"a   b\"\n" +
            "  'a'\n" +
            "  ZeroOrMore: \"   \"\n" +
            "    ' '\n" +
            "    ' '\n" +
            "    ' '\n" +
            "  'b'\n"));
    }

    [Test]
    public void PreserveAllSymbols_then_post_hoc_Flatten_recovers_normal_parse_shape()
    {
        // The critical FlattenInto scenario: parse twice with the
        // same grammar and input, once with PreserveAllSymbols off (the
        // parse-time filter drops Delete and lifts Flatten on the way)
        // and once with it on (every wrapper survives, each carrying its
        // real FlattenType). Calling .Flatten() on the preserved tree
        // walks FlattenInto, which drops the Delete-typed wrappers and
        // lifts the Flatten-typed ones, and should recover the same
        // shape the parse-time filter produced. Useful when a caller
        // wants the debug-friendly tree for diagnostics and the
        // collapsed tree for downstream processing without re-parsing.
        //
        // Grammar choice: OneOf(Letters) defaults to Preserve so its
        // leaves survive both ways, OptionalWhitespace is the canonical
        // FlattenType.Delete wrapper, AllOf is FlattenType.Flatten. All
        // three flatten policies are exercised in one tree.
        var letter = OneOf(RuneSet.Ascii.Letters);
        var rule = AllOf(letter, OptionalWhitespace(), letter);

        var normal = rule.Parse("a   b");
        var preserved = rule.Parse("a   b", new ParseOptions { PreserveAllSymbols = true });

        Assert.That(normal.Success, Is.True, normal.ErrorMessage);
        Assert.That(preserved.Success, Is.True, preserved.ErrorMessage);

        // preserved.Tree is the AllOf wrapper (Flatten-typed). FlattenInto
        // lifts its children, so the result is the lifted child list.
        var flattened = preserved.Tree!.Flatten();

        Assert.That(Fingerprint(flattened), Is.EqualTo(Fingerprint(normal.Symbols)));
    }

    [Test]
    public void FlattenInto_Delete_branch_drops_a_hand_built_Delete_node()
    {
        // Low-level guard for the Delete branch of Symbol.FlattenInto.
        // The PreserveAllSymbols round-trip test above is the real-user
        // scenario; this one drives FlattenInto directly with a Symbol
        // tree built by hand so the assertion is on FlattenInto's own
        // behavior and doesn't rely on whatever wrapper FlattenTypes the
        // parser happens to produce. Useful as a regression guard if
        // Symbol.FlattenInto is edited in isolation.
        var leaf = new Symbol(new SymbolId(1), FlattenType.Delete, "x".AsMemory());
        var kept = new Symbol(new SymbolId(2), FlattenType.Preserve, "y".AsMemory());
        var composite = new Symbol(new SymbolId(3), FlattenType.Preserve,
            new Symbol[] { leaf, kept });

        var flattened = composite.Flatten();

        // After Flatten: the Delete leaf is gone, the Preserve composite
        // rebuilds with only the surviving Preserve leaf, and the rebuilt
        // composite's ToString collapses to the kept leaf's text. The
        // fingerprint encodes Id, FlattenType, text, and child structure
        // in one string, so the entire expected shape lives in one
        // EqualTo and any drift shows up as a string diff.
        Assert.That(Fingerprint(flattened), Is.EqualTo("(3|Preserve=y:[(2|Preserve=y)])"));
    }
}
