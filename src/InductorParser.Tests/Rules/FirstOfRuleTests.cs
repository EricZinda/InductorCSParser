using System;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;

namespace InductorParser.Tests;

[TestFixture]
public class FirstOfRuleTests
{
    [Test]
    public void FirstOf_returns_the_first_alternative_that_matches()
    {
        // Grapheme defaults to FlattenType.Delete, so the matched 'b' would
        // be filtered out of the tree at parse time. PreserveAllSymbols
        // keeps the Grapheme leaf in the tree so Tree.ToString() shows the
        // text that was actually matched.
        var rule = FirstOf(Grapheme('a'), Grapheme('b'), Grapheme('c'));
        var result = rule.Parse("b", new ParseOptions { PreserveAllSymbols = true });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("b"));
    }

    [Test]
    public void FirstOf_failure_without_WithError_falls_back_to_positional_message()
    {
        // All alternatives fail, none have WithError. Null message at
        // offset 0, positional fallback renders.
        var rule = FirstOf(Grapheme('a'), Grapheme('b'), Grapheme('c'));
        var result = rule.Parse("x");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Does.StartWith("Parse failed at offset 0"));
    }

    [Test]
    public void FirstOf_all_children_fail_at_same_position_first_writer_wins()
    {
        // All three alternatives try at offset 0 and fail. Each records at
        // pre-read position 0 with its own WithError message. Equal depth,
        // so the first-writer wins the message slot. That's Grapheme('a'),
        // which FirstOf tries first.
        var rule = FirstOf(Grapheme('a').WithError("want 'a'"),
                           Grapheme('b').WithError("want 'b'"),
                           Grapheme('c').WithError("want 'c'"));

        var result = rule.Parse("x");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("want 'a'"));
    }

    [Test]
    public void FirstOf_child_that_consumes_deeper_wins_the_position_and_message()
    {
        // First branch matches "ab" then fails on 'x' at offset 2, recording
        // its Grapheme('c') WithError there. Second branch matches "a" then
        // fails on 'b' at offset 1, recording its Grapheme('d') WithError there.
        // Deepest-wins picks offset 2, so the first branch's "need 'c'"
        // surfaces.
        var rule = FirstOf(AllOf(Grapheme('a'), Grapheme('b'), Grapheme('c').WithError("need 'c'")),
                           AllOf(Grapheme('a'), Grapheme('d').WithError("need 'd'")));

        var result = rule.Parse("abx");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
        Assert.That(result.ErrorMessage, Is.EqualTo("need 'c'"));
    }

    [Test]
    public void FirstOf_trace_success_produces_expected_output()
    {
        // Third alternative wins. Required-runes dispatch skips Grapheme('a') and
        // Grapheme('b') on lookahead 'c' (their FirstConsumedRunes don't contain 'c'
        // and neither is empty-capable), so only the matching Grapheme('c')
        // branch opens a transaction and emits trace lines. The
        // nesting remains depth 2 (FirstOf's transaction + Grapheme's transaction).
        var sink = NewSink();
        FirstOf(Grapheme('a'), Grapheme('b'), Grapheme('c')).Parse("c", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'c', Consumed: 1",
            "      SUCC | Grapheme: found 'c'",
            "   SUCC | FirstOf: symbol #2"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void FirstOf_trace_failure_produces_expected_output()
    {
        // Required-runes dispatch rules out both Grapheme('a') and Grapheme('b') on
        // lookahead 'z', so no child transaction ever opens. By the time
        // FirstOf emits its FAIL line after the loop, no transaction is open
        // and the line carries no indentation. Empty detail message means
        // there's no ": {detail}" tail, so the line reads "FAIL | FirstOf".
        var sink = NewSink();
        FirstOf(Grapheme('a'), Grapheme('b')).Parse("z", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "FAIL | FirstOf"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Sealed_FirstOf_rejects_Flatten()
    {
        var rule = FirstOf(Grapheme('a'), Grapheme('b'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.Flatten(FlattenType.Preserve));
    }

    [Test]
    public void Sealed_FirstOf_rejects_WithError()
    {
        var rule = FirstOf(Grapheme('a'), Grapheme('b'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.WithError("late"));
    }

    [Test]
    public void Sealed_FirstOf_rejects_As()
    {
        var rule = FirstOf(Grapheme('a'), Grapheme('b'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.As("late"));
    }
}
