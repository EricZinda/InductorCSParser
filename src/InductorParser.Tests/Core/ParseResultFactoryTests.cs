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
}
