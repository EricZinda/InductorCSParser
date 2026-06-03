using NUnit.Framework;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests.DocExamples;

// Verifies the runnable code examples in docs/primer1.md against their
// documented behavior. Each test mirrors a code block from the doc and
// asserts the output the doc claims.
[TestFixture]
public class Primer1Examples
{
    // primer1.md "Inductor Parser Primer 1: Getting Started"
    // The grammar built up in the doc:
    //   var target = Literal("this sequence of characters");
    //   var example = And(ZeroOrMore(And(Not(target), AnyToken())), target);
    // Doc claim:
    //   var result = example.Parse(...);
    //   Console.WriteLine(result.ToString());
    //   // The output is (with one space at the end):
    //   // How can I match anything up until
    // The trailing Literal defaults to FlattenType.Delete, so its matched
    // text drops out of the tree text and result.ToString() returns just
    // the prefix that the ZeroOrMore consumed.
    [Test]
    public void Anything_until_sequence_returns_prefix_only_via_ToString()
    {
        var target = Literal("this sequence of characters");
        var example = And(
            ZeroOrMore(And(Not(target), AnyToken())),
            target);

        var result = example.Parse("How can I match anything up until this sequence of characters");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.ToString(), Is.EqualTo("How can I match anything up until "));
    }

    // primer1.md "To help with debugging, you can flip them all to Preserve
    // with options on the Parse() method like this". The doc shows a tree
    // shape with indented nodes printed by result.PrintTree(). Verify the
    // first and last lines match exactly, plus the structural counts.
    [Test]
    public void PreserveAllSymbols_PrintTree_matches_documented_shape()
    {
        var target = Literal("this sequence of characters");
        var example = And(
            ZeroOrMore(And(Not(target), AnyToken())),
            target);

        var options = new ParseOptions { PreserveAllSymbols = true };
        var result = example.Parse(
            "How can I match anything up until this sequence of characters",
            options);

        Assert.That(result.Success, Is.True, result.ErrorMessage);

        var lines = result.PrintTree().Split('\n');

        // First three lines from the doc:
        //   And: "How can I match anything up until this sequence of characters"
        //     ZeroOrMore: "How can I match anything up until "
        //       And: "H"
        Assert.That(lines[0],
            Is.EqualTo("And: \"How can I match anything up until this sequence of characters\""));
        Assert.That(lines[1],
            Is.EqualTo("  ZeroOrMore: \"How can I match anything up until \""));
        Assert.That(lines[2], Is.EqualTo("    And: \"H\""));
        Assert.That(lines[3], Is.EqualTo("      Not: \"\""));
        Assert.That(lines[4], Is.EqualTo("      'H'"));

        // Last meaningful line (before the trailing newline split): the
        // doc shows
        //     Literal: "this sequence of characters"
        // The PrintTree output ends with a trailing newline so Split('\n')
        // produces an empty final element.
        Assert.That(lines[^1], Is.EqualTo(""));
        Assert.That(lines[^2],
            Is.EqualTo("  Literal: \"this sequence of characters\""));
    }

    // The same setup as the previous test, asserting the structural counts
    // the doc's tree-shape output implies (one inner And per consumed
    // token, each with Not+AnyToken children).
    [Test]
    public void PreserveAllSymbols_yields_grammar_shaped_tree()
    {
        var target = Literal("this sequence of characters");
        var example = And(
            ZeroOrMore(And(Not(target), AnyToken())),
            target);

        var options = new ParseOptions { PreserveAllSymbols = true };
        var result = example.Parse(
            "How can I match anything up until this sequence of characters",
            options);

        Assert.That(result.Success, Is.True, result.ErrorMessage);

        // Root: And with two children (the ZeroOrMore and the Literal).
        var root = result.Tree!;
        Assert.That(root.Children.Count, Is.EqualTo(2),
            "root And should have ZeroOrMore + Literal as children");

        // First child is the ZeroOrMore matching the prefix.
        var zeroOrMore = root.Children[0];
        Assert.That(zeroOrMore.ToString(),
            Is.EqualTo("How can I match anything up until "));

        // ZeroOrMore's children are one inner And per consumed token.
        // "How can I match anything up until " is 34 chars / 34 graphemes.
        Assert.That(zeroOrMore.Children.Count, Is.EqualTo(34));

        // Each inner And has Not + AnyToken children (Not is zero-width).
        var firstInner = zeroOrMore.Children[0];
        Assert.That(firstInner.ToString(), Is.EqualTo("H"));
        Assert.That(firstInner.Children.Count, Is.EqualTo(2));
        Assert.That(firstInner.Children[0].ToString(), Is.EqualTo("")); // Not
        Assert.That(firstInner.Children[1].ToString(), Is.EqualTo("H")); // AnyToken

        // Second top-level child is the trailing Literal.
        var literal = root.Children[1];
        Assert.That(literal.ToString(), Is.EqualTo("this sequence of characters"));
    }

    // primer1.md mid-doc: throwing on parse failure with a FormatException
    // shape. Verify the failure path returns Success=false with a meaningful
    // ErrorMessage.
    [Test]
    public void Parse_failure_carries_error_message()
    {
        var target = Literal("this sequence of characters");
        var example = And(
            ZeroOrMore(And(Not(target), AnyToken())),
            target);

        var result = example.Parse("input that doesn't contain the target");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.Not.Empty);
    }

    // primer1.md "What about Unicode?" section: a character class that
    // includes a multi-rune token (the US flag emoji) alongside ordinary
    // letter ranges:
    //   var letterOrUSFlag = OneOf(TokenSet.Letters | TokenSet.Graphemes("🇺🇸"));
    // The flag 🇺🇸 is a regional-indicator pair (two runes forming one
    // grapheme cluster), so it has to enter the set through Graphemes(...),
    // which holds multi-rune cluster members. Runes(...) walks rune by rune
    // and throws on a cluster, so the doc has to name Graphemes here.
    [Test]
    public void Letter_class_includes_multirune_USFlag_token()
    {
        var letterOrUSFlag = OneOf(TokenSet.Letters | TokenSet.Graphemes(UnicodeExamples.USFlagGrapheme));

        Assert.That(letterOrUSFlag.Parse("A").Success, Is.True);
        Assert.That(letterOrUSFlag.Parse(UnicodeExamples.USFlagGrapheme).Success, Is.True);
        Assert.That(letterOrUSFlag.Parse("3").Success, Is.False);
    }
}
