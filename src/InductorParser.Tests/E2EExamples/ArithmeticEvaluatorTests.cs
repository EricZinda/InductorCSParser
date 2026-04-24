using System;
using NUnit.Framework;

namespace InductorParser.Tests;

// Tests for the ArithmeticGrammar + ArithmeticEvaluator E2E pair. These
// assert evaluation results, which is the whole point of consuming the
// parse output: a grammar that parses successfully but evaluates to the
// wrong number is a broken grammar.
[TestFixture]
public class ArithmeticEvaluatorTests
{
    [TestCase("0", 0)]
    [TestCase("7", 7)]
    [TestCase("123", 123)]
    public void Evaluates_single_number(string input, long expected)
    {
        Assert.That(ArithmeticEvaluator.Evaluate(input), Is.EqualTo(expected));
    }

    [TestCase("1+2", 3)]
    [TestCase("10-3", 7)]
    [TestCase("2*3", 6)]
    [TestCase("20/4", 5)]
    public void Evaluates_binary_ops(string input, long expected)
    {
        Assert.That(ArithmeticEvaluator.Evaluate(input), Is.EqualTo(expected));
    }

    [TestCase("1+2*3", 7)]
    [TestCase("2*3+1", 7)]
    [TestCase("10-4/2", 8)]
    public void Multiplicative_binds_tighter_than_additive(string input, long expected)
    {
        Assert.That(ArithmeticEvaluator.Evaluate(input), Is.EqualTo(expected));
    }

    [TestCase("(1+2)*3", 9)]
    [TestCase("3*(4-1)", 9)]
    [TestCase("(1+2)*(3+4)", 21)]
    [TestCase("((1+2))", 3)]
    public void Parentheses_override_precedence(string input, long expected)
    {
        Assert.That(ArithmeticEvaluator.Evaluate(input), Is.EqualTo(expected));
    }

    [TestCase("10-3-2", 5)]
    [TestCase("20-5-3-2", 10)]
    [TestCase("100/5/2", 10)]
    [TestCase("24/2/3", 4)]
    public void Operators_are_left_associative(string input, long expected)
    {
        // Right-associative would give 10-(3-2)=9 and 100/(5/2)=40.
        // Left-associative gives (10-3)-2=5 and (100/5)/2=10.
        Assert.That(ArithmeticEvaluator.Evaluate(input), Is.EqualTo(expected));
    }

    [Test]
    public void Tolerates_whitespace_everywhere()
    {
        Assert.That(ArithmeticEvaluator.Evaluate(" 1 + 2 * ( 3 - 4 ) "), Is.EqualTo(-1));
        // Note: \r\n is a single grapheme under the default GraphemeLexer,
        // so OneOf whitespace won't accept a CRLF as one token. Stick to
        // \n or \t in inputs, or switch to InputUnit.Rune if a grammar
        // needs CRLF handling.
        Assert.That(ArithmeticEvaluator.Evaluate("\t1\n+\n2 "), Is.EqualTo(3));
    }

    [Test]
    public void Rejects_malformed_input()
    {
        Assert.Throws<FormatException>(() => ArithmeticEvaluator.Evaluate(""));
        Assert.Throws<FormatException>(() => ArithmeticEvaluator.Evaluate("1+"));
        Assert.Throws<FormatException>(() => ArithmeticEvaluator.Evaluate("1 2"));
        Assert.Throws<FormatException>(() => ArithmeticEvaluator.Evaluate("(1+2"));
        Assert.Throws<FormatException>(() => ArithmeticEvaluator.Evaluate("1+2)"));
        Assert.Throws<FormatException>(() => ArithmeticEvaluator.Evaluate("1+2 garbage"));
    }
}
