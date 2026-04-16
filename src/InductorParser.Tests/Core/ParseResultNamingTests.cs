using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Tests for the ParseResult-level name-resolution helpers:
// NameOf(SymbolId), Name(Symbol), and PrintTree(). These are thin
// pass-throughs that spare callers from threading a Rule reference
// through every call site.
[TestFixture]
public class ParseResultNamingTests
{
    [Test]
    public void NameOf_resolves_root_rule_name_from_result()
    {
        var word = OneOrMore(RuneIn(RuneSet.Letters)).As("word");
        var result = word.Parse("hello");
        Assert.That(result.Success, Is.True);

        Assert.That(result.NameOf(result.Tree!.Id), Is.EqualTo("word"));
    }

    [Test]
    public void Name_resolves_symbol_to_rule_name()
    {
        var word = OneOrMore(RuneIn(RuneSet.Letters)).As("word");
        var result = word.Parse("hello");

        Assert.That(result.Name(result.Tree!), Is.EqualTo("word"));
    }

    [Test]
    public void Name_resolves_character_leaf_symbol()
    {
        var word = OneOrMore(RuneIn(RuneSet.Letters)).As("word");
        var result = word.Parse("h");
        var leaf = result.Tree!.Children[0];

        Assert.That(result.Name(leaf), Is.EqualTo("h"));
    }

    [Test]
    public void PrintTree_on_result_matches_extension_output()
    {
        var word = OneOrMore(RuneIn(RuneSet.Letters)).As("word");
        var result = word.Parse("hi");

        Assert.That(result.PrintTree(), Is.EqualTo(result.Tree!.PrintTree(word)));
    }

    [Test]
    public void PrintTree_returns_empty_string_when_parse_failed()
    {
        var word = OneOrMore(RuneIn(RuneSet.Letters)).As("word");
        var result = word.Parse("123"); // no letters, parse fails

        Assert.That(result.Success, Is.False);
        Assert.That(result.PrintTree(), Is.EqualTo(string.Empty));
    }
}
