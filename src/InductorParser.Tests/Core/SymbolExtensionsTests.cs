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
        var word = OneOrMore(OneOf(RuneSet.Letters)).As("word").Flatten(FlattenType.Preserve);
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
}
