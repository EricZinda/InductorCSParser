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
// parents (And / Or / BetweenInclusive) filter it out of their
// Children list. That's the fast path: Delete symbols never make it
// into the tree, so most callers never need to think about flattening.
//
// Symbol.FlattenInto / Symbol.Flatten() are the post-hoc path. The
// real-user scenario is parsing with ParseOptions.PreserveAllSymbols
// = true: every rule's Symbol survives into the tree with its real
// FlattenType, so a caller that wants the full debug shape and the
// collapsed shape from one parse can call .Flatten() on the preserved
// tree to get the latter back without re-parsing.
[TestFixture]
public class DiscardedAtParseTimeTests
{
    [Test]
    public void Optional_Whitespace_compile_then_parse_whitespace_produces_no_whitespace_nodes()
    {
        // A specific guarantee the performance story depends on: every
        // Optional(InlineWhitespace()) match, with or without actual whitespace
        // in the input, costs zero Symbol allocations, composite or leaf,
        // that survive into the tree.
        var rule = Optional(InlineWhitespace());
        rule.Compile();

        var result = rule.Parse("   ");
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        // Nothing survives into the tree. Optional defaults to
        // FlattenType.Flatten and InlineWhitespace() is FlattenType.Delete,
        // so the parse-time filter drops the inner Delete-typed
        // InlineWhitespace match and the surrounding Optional has nothing
        // to lift, leaving the root's Symbols list empty.
        Assert.That(result.Symbols, Is.Empty);
    }

    [Test]
    public void Optional_Whitespace_inside_composite_leaves_no_whitespace_children()
    {
        // The realistic JSON-style shape: Optional(InlineWhitespace()) sits between
        // two tokens inside an And. The top-level Symbols list should hold
        // the two token leaves only, with the whitespace contributing
        // nothing. OneOf has FlattenType.Preserve so the token leaves
        // survive. Their Id is the code point, so we assert on that.
        var letter = OneOf(TokenSet.Ascii.Letters);
        var rule = And(letter, Optional(InlineWhitespace()), letter);
        var result = rule.Parse("a   b");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Symbols.Count, Is.EqualTo(2));
        Assert.That(result.Symbols[0].Id.Value, Is.EqualTo('a'));
        Assert.That(result.Symbols[1].Id.Value, Is.EqualTo('b'));
        // Concatenated text reflects what's actually in the tree: the two
        // letter leaves. The Optional(InlineWhitespace()) was filtered at parse
        // time, so its text doesn't appear here. Callers who want the full
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
        // flag on: every rule's Symbol around and between the letters
        // should survive into the tree so PrintTree and Find queries
        // see a shape that matches the grammar as written. Comparing
        // the full PrintTree rendering in one shot is easier to read
        // than per-child assertions, and any drift shows up as a
        // string diff naming the exact node that moved.
        var rule = And(Token('a'), Optional(InlineWhitespace()), Token('b'));
        var options = new ParseOptions { PreserveAllSymbols = true };
        var result = rule.Parse("a   b", options);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.PrintTree(), Is.EqualTo(
            "And: \"a   b\"\n" +
            "  'a'\n" +
            "  Optional: \"   \"\n" +
            "    OneOrMore: \"   \"\n" +
            "      ' '\n" +
            "      ' '\n" +
            "      ' '\n" +
            "  'b'\n"));
    }

    [Test]
    public void PreserveAllSymbols_then_post_hoc_Flatten_recovers_normal_parse_shape()
    {
        // The critical FlattenInto scenario: parse twice with the
        // same grammar and input, once with PreserveAllSymbols off (the
        // parse-time filter drops Delete and lifts Flatten on the way)
        // and once with it on (every Symbol survives, each with its
        // real FlattenType). Calling .Flatten() on the preserved tree
        // walks FlattenInto, which drops the Delete-typed Symbols and
        // lifts the Flatten-typed ones, and should recover the same
        // shape the parse-time filter produced. Useful when a caller
        // wants the debug-friendly tree for diagnostics and the
        // collapsed tree for downstream processing without re-parsing.
        //
        // Grammar choice: OneOf(Letters) defaults to Preserve so its
        // leaves survive both ways, Optional(InlineWhitespace()) gives a
        // Flatten Symbol (Optional) around a Delete Symbol (InlineWhitespace),
        // and And is Flatten. All three flatten policies are exercised
        // in one tree.
        var letter = OneOf(TokenSet.Ascii.Letters);
        var rule = And(letter, Optional(InlineWhitespace()), letter);

        var normal = rule.Parse("a   b");
        var preserved = rule.Parse("a   b", new ParseOptions { PreserveAllSymbols = true });

        Assert.That(normal.Success, Is.True, normal.ErrorMessage);
        Assert.That(preserved.Success, Is.True, preserved.ErrorMessage);

        // preserved.Tree is the And's Symbol (Flatten-typed). FlattenInto
        // lifts its children, so the result is the lifted child list.
        var flattened = preserved.Tree!.Flatten();

        Assert.That(Fingerprint(flattened), Is.EqualTo(Fingerprint(normal.Symbols)));
    }

    [Test]
    public void FlattenInto_Flatten_branch_keeps_a_hand_built_Flatten_leaf()
    {
        // A leaf with FlattenType.Flatten survives in the parse tree under
        // PreserveAllSymbols=true, because parse-time leaf construction stamps
        // the rule's FlattenType onto the leaf and PreserveAllSymbols suppresses
        // the parse-time lift. Symbol.FlattenInto's Flatten branch then has to
        // handle that leaf shape: a leaf has no children to lift, so the
        // intent of "Flatten" on a leaf is the same as parse-time's "add
        // myself to outputSymbols" branch (see OneOfRule / GraphemeRule's
        // Flatten case). Dropping the leaf here would silently lose its
        // text. See backlog b4xv.
        var leaf = new Symbol(new SymbolId(1), FlattenType.Flatten, "x".AsMemory());
        var composite = new Symbol(new SymbolId(2), FlattenType.Preserve, new[] { leaf });

        var flattened = composite.Flatten();

        Assert.That(string.Concat(flattened.Select(s => s.ToString())), Is.EqualTo("x"));
    }

    [Test]
    public void PreserveAllSymbols_then_post_hoc_Flatten_keeps_Float_minus_sign()
    {
        // Real-world repro: Float() in Rules.cs uses
        // Token('-').Flatten(FlattenType.Flatten) for the optional leading
        // minus sign. Under PreserveAllSymbols=true the '-' lands in the
        // tree as a Flatten-typed leaf. Calling .Flatten() on the preserved
        // tree to recover the normal-parse shape used to drop the '-'
        // because Symbol.FlattenInto's Flatten branch only iterated
        // Children (empty for a leaf) and never added the leaf itself.
        var rule = Float();
        var normal = rule.Parse("-1.5");
        var preserved = rule.Parse("-1.5", new ParseOptions { PreserveAllSymbols = true });

        Assert.That(normal.Success, Is.True, normal.ErrorMessage);
        Assert.That(preserved.Success, Is.True, preserved.ErrorMessage);

        var flattened = preserved.Tree!.Flatten();
        string flattenedText = string.Concat(flattened.Select(s => s.ToString()));
        string normalText = string.Concat(normal.Symbols.Select(s => s.ToString()));

        Assert.That(flattenedText, Is.EqualTo(normalText),
            "post-hoc Flatten should recover the same text as a normal parse");
    }

    [Test]
    public void FlattenInto_Delete_branch_drops_a_hand_built_Delete_node()
    {
        // Low-level test of the Delete branch of Symbol.FlattenInto.
        // The PreserveAllSymbols round-trip test above is the real-user
        // scenario. This one drives FlattenInto directly with a Symbol
        // tree built by hand so the assertion is on FlattenInto's own
        // behavior and doesn't rely on whatever FlattenTypes the
        // parser happens to produce. Useful as a regression test if
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
