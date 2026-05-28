using System;
using System.Linq;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Tests for the ParseResult-level Find / FindAll helpers. These walk
// every top-level Symbol so a grammar author can extract named
// children without first figuring out whether the root rule kept its
// wrapper (FlattenType.Preserve) or lifted its children up
// (FlattenType.Flatten). The flatten-root case is the one that
// previously surprised users: result.Tree returns null there, and
// result.Tree.Find blows up with a NullReferenceException.
[TestFixture]
public class ParseResultFindTests
{
    [Test]
    public void Find_works_on_preserve_root()
    {
        var typeRule = ZeroOrMore(OneOf(TokenSet.Ascii.Letters)).As("type").Flatten(FlattenType.Preserve);
        var rule = And(typeRule, Literal(": "), AnyToken()).Flatten(FlattenType.Preserve);
        rule.Compile();
        var result = rule.Parse("ab: c");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Tree, Is.Not.Null);
        Assert.That(result.Find(typeRule), Is.Not.Null);
        Assert.That(result.Find(typeRule)!.ToString(), Is.EqualTo("ab"));
    }

    [Test]
    public void Find_works_on_flatten_root()
    {
        // The motivating case from the backlog item: And defaults to
        // FlattenType.Flatten, so the parse produces a list of top-level
        // children rather than a single root Symbol. result.Tree is
        // null here, and result.Tree.Find would NRE. result.Find still
        // works because it walks every top-level Symbol.
        var typeRule = ZeroOrMore(OneOf(TokenSet.Ascii.Letters)).As("type").Flatten(FlattenType.Preserve);
        var rule = And(typeRule, Literal(": "), AnyToken());
        rule.Compile();
        var result = rule.Parse("ab: c");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Tree, Is.Null);
        Assert.That(result.Find(typeRule), Is.Not.Null);
        Assert.That(result.Find(typeRule)!.ToString(), Is.EqualTo("ab"));
    }

    [Test]
    public void Find_by_SymbolId_overload_matches_by_id()
    {
        var typeRule = ZeroOrMore(OneOf(TokenSet.Ascii.Letters)).As("type").Flatten(FlattenType.Preserve);
        var rule = And(typeRule, Literal(": "), AnyToken());
        rule.Compile();
        var result = rule.Parse("ab: c");

        Assert.That(result.Find(typeRule.Id), Is.Not.Null);
        Assert.That(result.Find(typeRule.Id)!.ToString(), Is.EqualTo("ab"));
    }

    [Test]
    public void Find_returns_null_when_rule_is_not_in_tree()
    {
        var typeRule = ZeroOrMore(OneOf(TokenSet.Ascii.Letters)).As("type").Flatten(FlattenType.Preserve);
        var otherRule = OneOrMore(OneOf(TokenSet.Ascii.Digits)).As("other").Flatten(FlattenType.Preserve);
        var rule = And(typeRule, Literal(": "), AnyToken());
        rule.Compile();
        var result = rule.Parse("ab: c");

        Assert.That(result.Find(otherRule), Is.Null);
    }

    [Test]
    public void Find_returns_null_on_failed_parse()
    {
        var typeRule = OneOrMore(OneOf(TokenSet.Ascii.Letters)).As("type").Flatten(FlattenType.Preserve);
        var rule = And(typeRule, Literal(": "), AnyToken());
        rule.Compile();
        var result = rule.Parse("123");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Find(typeRule), Is.Null);
    }

    [Test]
    public void FindAll_yields_every_match_under_flatten_root()
    {
        // The word rule appears once per repetition, and the outer
        // root is OneOrMore (Flatten) so the top-level Symbols list
        // is the flat sequence of word + space + word + space + ...
        // FindAll has to descend into each one to surface every word.
        var word = OneOrMore(OneOf(TokenSet.Ascii.Letters)).As("word").Flatten(FlattenType.Preserve);
        var rule = OneOrMore(And(word, Optional(Literal(" "))));
        rule.Compile();
        var result = rule.Parse("ab cd ef");

        Assert.That(result.Success, Is.True);
        var words = result.FindAll(word).Select(s => s.ToString()).ToArray();
        Assert.That(words, Is.EqualTo(new[] { "ab", "cd", "ef" }));
    }

    [Test]
    public void FindAll_yields_empty_on_failed_parse()
    {
        var word = OneOrMore(OneOf(TokenSet.Ascii.Letters)).As("word").Flatten(FlattenType.Preserve);
        var rule = OneOrMore(And(word, Optional(Literal(" "))));
        rule.Compile();
        var result = rule.Parse("123");

        Assert.That(result.Success, Is.False);
        Assert.That(result.FindAll(word).ToArray(), Is.Empty);
    }

    [Test]
    public void Find_rejects_null_rule()
    {
        // Find(Rule rule) rejects a null rule with ArgumentNullException,
        // matching the public API shape used by Rule.Parse / Rule.As /
        // Rule.WithError. Without the null check, the forwarding body
        // `Find(rule.Id)` would dereference rule.Id and throw a raw
        // NullReferenceException instead.
        var word = OneOrMore(OneOf(TokenSet.Ascii.Letters)).As("word").Flatten(FlattenType.Preserve);
        var rule = And(word, Eof());
        rule.Compile();
        var result = rule.Parse("ab");

        var exception = Assert.Throws<ArgumentNullException>(() => result.Find((Rule)null!));
        Assert.That(exception!.ParamName, Is.EqualTo("rule"));
    }

    [Test]
    public void FindAll_rejects_null_rule()
    {
        // FindAll(Rule rule) rejects a null rule with ArgumentNullException.
        // FindAll(SymbolId) is iterator-bodied, but the public Rule overload
        // is a regular method whose forwarding expression `FindAll(rule.Id)`
        // would evaluate rule.Id eagerly. The exception fires immediately on
        // the call rather than at first enumeration.
        var word = OneOrMore(OneOf(TokenSet.Ascii.Letters)).As("word").Flatten(FlattenType.Preserve);
        var rule = And(word, Eof());
        rule.Compile();
        var result = rule.Parse("ab");

        var exception = Assert.Throws<ArgumentNullException>(() => result.FindAll((Rule)null!));
        Assert.That(exception!.ParamName, Is.EqualTo("rule"));
    }
}
