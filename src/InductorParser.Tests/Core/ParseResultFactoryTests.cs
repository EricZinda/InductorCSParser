using System;
using System.Collections.Generic;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests;

[TestFixture]
public class ParseResultFactoryTests
{
    [Test]
    public void ErrorCharIndex_factory_rejects_out_of_range_values()
    {
        // ErrorCharIndex must be in [0, input.Length]; out-of-range
        // throws, input.Length is the inclusive upper bound.
        var grammar = Literal("hi");
        grammar.Compile();
        var input = "ab";

        Assert.Throws<System.ArgumentOutOfRangeException>(() =>
            ParseResult.Failed(errorCharIndex: 999, message: "x", input: input, grammar: grammar));
        Assert.Throws<System.ArgumentOutOfRangeException>(() =>
            ParseResult.Aborted(ParseOutcome.Timeout, errorCharIndex: -5, message: "x", input: input, grammar: grammar));

        var atBoundary = ParseResult.Failed(errorCharIndex: input.Length, message: "x", input: input, grammar: grammar);
        Assert.That(atBoundary.ErrorCharIndex, Is.EqualTo(input.Length));
        Assert.That(atBoundary.ErrorCharIndex, Is.EqualTo(atBoundary.ErrorPosition!.Value.CharIndex));
    }

    [Test]
    public void Aborted_factory_rejects_non_abort_outcomes()
    {
        // ParseResult.Aborted is for the four budget/cancellation outcomes
        // only. Accepting Success would produce a contradictory result whose
        // Success property is true despite carrying an abort message and no
        // symbols. 
        var grammar = Literal("hi");
        grammar.Compile();
        var input = "ab";

        Assert.Throws<System.ArgumentException>(() =>
            ParseResult.Aborted(ParseOutcome.Success, errorCharIndex: 0, message: "x", input: input, grammar: grammar));
        Assert.Throws<System.ArgumentException>(() =>
            ParseResult.Aborted(ParseOutcome.GrammarMismatch, errorCharIndex: 0, message: "x", input: input, grammar: grammar));
        Assert.Throws<System.ArgumentException>(() =>
            ParseResult.Aborted((ParseOutcome)999, errorCharIndex: 0, message: "x", input: input, grammar: grammar));
    }

    [Test]
    public void Factories_reject_null_required_arguments()
    {
        // The public factories are for custom parse drivers, but they still
        // need to enforce the same non-null shape Rule.Parse produces. In
        // particular, a null grammar on a Succeeded result contradicts the
        // default-struct check in ParseResult.Success: Outcome is Success, but
        // Success reports false because _grammar is null.
        var grammar = Literal("hi");
        grammar.Compile();
        var symbols = System.Array.Empty<Symbol>();

        Assert.Throws<System.ArgumentNullException>(() =>
            ParseResult.Succeeded(null!, "hi", grammar));
        Assert.Throws<System.ArgumentNullException>(() =>
            ParseResult.Succeeded(symbols, null!, grammar));
        Assert.Throws<System.ArgumentNullException>(() =>
            ParseResult.Succeeded(symbols, "hi", null!));

        Assert.Throws<System.ArgumentNullException>(() =>
            ParseResult.Failed(0, null!, "hi", grammar));
        Assert.Throws<System.ArgumentNullException>(() =>
            ParseResult.Failed(0, "x", null!, grammar));
        Assert.Throws<System.ArgumentNullException>(() =>
            ParseResult.Failed(0, "x", "hi", null!));

        Assert.Throws<System.ArgumentNullException>(() =>
            ParseResult.Aborted(ParseOutcome.Timeout, 0, null!, "hi", grammar));
        Assert.Throws<System.ArgumentNullException>(() =>
            ParseResult.Aborted(ParseOutcome.Timeout, 0, "x", null!, grammar));
        Assert.Throws<System.ArgumentNullException>(() =>
            ParseResult.Aborted(ParseOutcome.Timeout, 0, "x", "hi", null!));
    }

    [Test]
    public void Succeeded_factory_copies_symbols_list()
    {
        var grammar = Literal("a");
        grammar.Compile();
        var original = new Symbol(new SymbolId(1), FlattenType.Preserve, "a".AsMemory());
        var replacement = new Symbol(new SymbolId(2), FlattenType.Preserve, "z".AsMemory());
        var symbols = new List<Symbol> { original };

        var result = ParseResult.Succeeded(symbols, "a", grammar);

        symbols[0] = replacement;

        Assert.That(result.Symbols, Is.Not.SameAs(symbols));
        Assert.That(result.Symbols, Is.Not.InstanceOf<List<Symbol>>());
        Assert.That(result.Symbols[0], Is.SameAs(original));
    }

    [Test]
    public void Parsed_result_symbols_do_not_expose_mutable_array()
    {
        var grammar = Token('a').Preserve();
        var result = grammar.Parse("a");
        var replacement = new Symbol(new SymbolId(2), FlattenType.Preserve, "z".AsMemory());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Symbols as Symbol[], Is.Null);

        var writable = result.Symbols as IList<Symbol>;
        Assert.That(writable, Is.Not.Null);
        Assert.That(writable!.IsReadOnly, Is.True);
        Assert.Throws<NotSupportedException>(() => { writable[0] = replacement; });
        Assert.That(result.ToString(), Is.EqualTo("a"));
    }
}
