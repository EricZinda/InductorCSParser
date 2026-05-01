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
        // Anonymous AllOf rule, no user name, so the root renders with
        // the class-derived "AllOf" label. Grapheme leaves default to
        // FlattenType.Delete and are filtered at parse time under normal
        // parsing. PreserveAllSymbols keeps them so PrintTree sees
        // a shape matching the grammar one-to-one.
        var pair = AllOf(Grapheme('a'), Grapheme('1'));
        var result = pair.Parse("a1", new ParseOptions { PreserveAllSymbols = true });
        Assert.That(result.Success, Is.True);

        string expected =
            "AllOf: \"a1\"\n" +
            "  'a'\n" +
            "  '1'\n";

        Assert.That(result.Tree!.PrintTree(pair), Is.EqualTo(expected));
    }

    [Test]
    public void PrintTree_indents_nested_subtrees()
    {
        // Two levels of named AllOf wrappers so the printed tree has real
        // depth beyond just a root plus leaves. Grapheme leaves default to
        // FlattenType.Delete. PreserveAllSymbols keeps them so the
        // printed tree shows both the composites and their Grapheme children.
        var first = AllOf(Grapheme('a'), Grapheme('b')).As("first");
        var second = AllOf(Grapheme('c'), Grapheme('d')).As("second");
        var pair = AllOf(first, second).As("pair");
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
