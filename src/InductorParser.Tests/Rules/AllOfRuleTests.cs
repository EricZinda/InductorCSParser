using System;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;

namespace InductorParser.Tests;

[TestFixture]
public class AllOfRuleTests
{
    [Test]
    public void AllOf_all_children_succeed_concatenates_consumed_input()
    {
        // Token defaults to FlattenType.Delete, so its text is filtered
        // out of the tree at parse time. PreserveAllSymbols keeps
        // every grammar node so Tree.ToString() sees the full matched
        // input.
        var rule = AllOf(Token('a'), Token('b'), Token('c'));
        var result = rule.Parse("abc", new ParseOptions { PreserveAllSymbols = true });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("abc"));
    }

    [Test]
    public void AllOf_failure_without_WithError_falls_back_to_positional_message()
    {
        // No WithError on any child or on AllOf itself. Token('b') records a
        // null message at offset 1. The positional fallback renders.
        var rule = AllOf(Token('a'), Token('b'));
        var result = rule.Parse("ax");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
        Assert.That(result.ErrorMessage, Does.StartWith("Parse failed at offset 1"));
    }

    [Test]
    public void AllOf_first_child_failure_propagates_position()
    {
        // Each child has its own WithError message. When the first child
        // fails, its message wins because it's the only one that ran.
        var rule = AllOf(Token('a').WithError("need an 'a'"),
                         Token('b').WithError("need a 'b'"));

        var result = rule.Parse("xb");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("need an 'a'"));
    }

    [Test]
    public void AllOf_later_child_failure_reports_at_deeper_position()
    {
        // Token('a') succeeds. Token('b') runs at offset 1 and fails on 'x'.
        // The second child's pre-read position is deeper than anywhere
        // the first child could have recorded, and its WithError message
        // is the one that surfaces.
        var rule = AllOf(Token('a').WithError("need an 'a'"),
                         Token('b').WithError("need a 'b'"));

        var result = rule.Parse("ax");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
        Assert.That(result.ErrorMessage, Is.EqualTo("need a 'b'"));
    }

    [Test]
    public void AllOf_trace_success_produces_expected_output()
    {
        var sink = NewSink();
        AllOf(Token('a'), Token('b')).Parse("ab", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'a', Consumed: 1",
            "      SUCC | Token: found 'a'",
            "      Lexer.Read: 'b', Consumed: 2",
            "      SUCC | Token: found 'b'",
            "   SUCC | AllOf: found 2"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void AllOf_trace_failure_produces_expected_output()
    {
        // "ax" advances past 'a', then Token('b') fails at position 1
        // which is > the initial deepest (0), so the
        // Lexer.RecordFailure trace fires too.
        var sink = NewSink();
        AllOf(Token('a'), Token('b')).Parse("ax", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'a', Consumed: 1",
            "      SUCC | Token: found 'a'",
            "      Lexer.Read: 'x', Consumed: 2",
            "      FAIL | Token: found 'x', wanted 'b'",
            "      Lexer.RecordFailure: new deepest failure at char 1",
            "   FAIL | AllOf: symbol #1"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Sealed_AllOf_rejects_Flatten()
    {
        var rule = AllOf(Token('a'), Token('b'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.Flatten(FlattenType.Preserve));
    }

    [Test]
    public void Sealed_AllOf_rejects_WithError()
    {
        var rule = AllOf(Token('a'), Token('b'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.WithError("late"));
    }

    [Test]
    public void Sealed_AllOf_rejects_As()
    {
        var rule = AllOf(Token('a'), Token('b'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.As("late"));
    }
}
