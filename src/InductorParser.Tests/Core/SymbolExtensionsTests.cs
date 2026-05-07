using System;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Tests for Symbol.PrintTree, the extension that renders a raw
// Symbol tree using a Rule for name resolution. For ParseResult-
// driven debug output, see ParseResultNamingTests.
[TestFixture]
public class SymbolExtensionsTests
{
    [Test]
    public void PrintTree_renders_named_root_and_character_leaves()
    {
        var word = OneOrMore(OneOf(TokenSet.Letters)).As("word").Flatten(FlattenType.Preserve);
        var result = word.Parse("hi");
        Assert.That(result.Success, Is.True);

        string expected =
            "word: \"hi\"\n" +
            "  'h'\n" +
            "  'i'\n";

        Assert.That(result.Tree!.PrintTree(word), Is.EqualTo(expected));
    }

    [Test]
    public void PrintTree_renders_unnamed_composite_with_class_name()
    {
        // Anonymous And rule, no user name, so the root renders with
        // the class-derived "And" label. Token leaves default to
        // FlattenType.Delete and are filtered at parse time under normal
        // parsing. PreserveAllSymbols keeps them so PrintTree sees
        // a shape matching the grammar one-to-one.
        var pair = And(Token('a'), Token('1'));
        var result = pair.Parse("a1", new ParseOptions { PreserveAllSymbols = true });
        Assert.That(result.Success, Is.True);

        string expected =
            "And: \"a1\"\n" +
            "  'a'\n" +
            "  '1'\n";

        Assert.That(result.Tree!.PrintTree(pair), Is.EqualTo(expected));
    }

    [Test]
    public void PrintTree_indents_nested_subtrees()
    {
        // Two levels of named And wrappers so the printed tree has real
        // depth beyond just a root plus leaves. Token leaves default to
        // FlattenType.Delete. PreserveAllSymbols keeps them so the
        // printed tree shows both the composites and their Token children.
        var first = And(Token('a'), Token('b')).As("first");
        var second = And(Token('c'), Token('d')).As("second");
        var pair = And(first, second).As("pair");
        var result = pair.Parse("abcd", new ParseOptions { PreserveAllSymbols = true });
        Assert.That(result.Success, Is.True);

        string expected =
            "pair: \"abcd\"\n" +
            "  first: \"ab\"\n" +
            "    'a'\n" +
            "    'b'\n" +
            "  second: \"cd\"\n" +
            "    'c'\n" +
            "    'd'\n";

        Assert.That(result.Tree!.PrintTree(pair), Is.EqualTo(expected));
    }

    [Test]
    public void PrintTree_renders_named_single_rune_Token_with_user_supplied_name()
    {
        // A named single-rune Token has a leaf whose Id is in the
        // character range (0..0x10FFFF) but whose user-supplied name
        // differs from the rune's own text. PrintTree uses the long
        // `<name>: "<text>"` form for character leaves whose NameOf
        // returns something other than the rune text.
        var aChar = Token('a').As("aChar").Preserve();
        var result = aChar.Parse("a");
        Assert.That(result.Success, Is.True);

        Assert.That(result.Tree!.PrintTree(aChar), Is.EqualTo("aChar: \"a\"\n"));
    }

    [Test]
    public void PrintTree_unnamed_single_rune_Token_keeps_compact_rune_form()
    {
        // The compact `'c'` form is the right shape for unnamed single-
        // rune Tokens: the leaf's name and matched text are both the rune
        // itself, so a separate `c: "c"` would be redundant.
        var rule = Token('a').Preserve();
        var result = rule.Parse("a");
        Assert.That(result.Success, Is.True);

        Assert.That(result.Tree!.PrintTree(rule), Is.EqualTo("'a'\n"));
    }

    [Test]
    public void PrintTree_renders_invalid_scalar_id_as_U_FFFD()
    {
        // A leaf whose Id lands in the character range numerically but
        // isn't a valid Unicode scalar (a surrogate half in 0xD800..
        // 0xDFFF) renders as the Unicode replacement character. Locks
        // the fallback against drift to a different placeholder.
        var rule = Token('a').Preserve();
        rule.Compile();
        var staleLeaf = new Symbol(new SymbolId(0xD800), FlattenType.Preserve, "a".AsMemory());

        Assert.That(staleLeaf.PrintTree(rule), Is.EqualTo("'�'\n"));
    }
}
