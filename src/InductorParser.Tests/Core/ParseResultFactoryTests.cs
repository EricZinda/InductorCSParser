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
    [TestCase(ParseOutcome.Success)]
    [TestCase(ParseOutcome.GrammarMismatch)]
    [TestCase(ParseOutcome.MalformedInput)]
    [TestCase(ParseOutcome.Timeout)]
    [TestCase(ParseOutcome.RuleCountLimitExceeded)]
    [TestCase(ParseOutcome.DepthLimitExceeded)]
    [TestCase(ParseOutcome.Canceled)]
    public void Completed_results_distinguish_success_from_failure_and_NotRun(ParseOutcome outcome)
    {
        var grammar = Eof();
        var result = outcome switch
        {
            ParseOutcome.Success => grammar.Parse(string.Empty),
            ParseOutcome.GrammarMismatch => grammar.Parse("x"),
            ParseOutcome.MalformedInput => ParseResult.MalformedInput(0, "bad input", "x", grammar),
            _ => ParseResult.Aborted(outcome, 0, "aborted", "x", grammar)
        };

        Assert.That(result.Outcome, Is.EqualTo(outcome));
        Assert.That(result.Success, Is.EqualTo(outcome == ParseOutcome.Success));
        Assert.That(result.ErrorPosition.HasValue, Is.EqualTo(outcome != ParseOutcome.Success));
        Assert.That(result.ToDebugString(), Does.StartWith(outcome.ToString()));
    }

    [Test]
    public void ErrorCharIndex_factory_rejects_out_of_range_values()
    {
        // ErrorCharIndex must be in [0, input.Length]. Out-of-range
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
        // Success property is true despite holding an abort message and no
        // symbols.
        var grammar = Literal("hi");
        grammar.Compile();
        var input = "ab";

        Assert.Throws<System.ArgumentException>(() =>
            ParseResult.Aborted(ParseOutcome.NotRun, errorCharIndex: 0, message: "x", input: input, grammar: grammar));
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
        // Both parser engines use these factories, which enforce a consistent
        // non-null result shape.
        // Successful results need the grammar for symbol names and tree rendering.
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
    public void MalformedInput_factory_builds_a_non_success_result()
    {
        // The MalformedInput factory mirrors Failed: a non-Success result
        // with a message and an offending index, but with its own outcome
        // so callers can tell "the input isn't valid Unicode" apart from a
        // plain grammar mismatch.
        var grammar = Literal("hi");
        grammar.Compile();
        var input = "ab";

        var result = ParseResult.MalformedInput(errorCharIndex: 1, message: "bad", input: input, grammar: grammar);

        Assert.That(result.Outcome, Is.EqualTo(ParseOutcome.MalformedInput));
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("bad"));
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));

        // Same range enforcement and null-argument checks as the other factories.
        Assert.Throws<System.ArgumentOutOfRangeException>(() =>
            ParseResult.MalformedInput(errorCharIndex: 999, message: "x", input: input, grammar: grammar));
        Assert.Throws<System.ArgumentNullException>(() =>
            ParseResult.MalformedInput(0, null!, input, grammar));
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
