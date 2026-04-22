using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;

namespace InductorParser.Tests;

[TestFixture]
public class OrRuleTests
{
    [Test]
    public void Or_returns_the_first_alternative_that_matches()
    {
        // Token defaults to FlattenType.Delete, so the matched 'b' would
        // be filtered out of the tree at parse time. PreserveFlattenWrappers
        // keeps the Token leaf in the tree so Tree.ToString() shows the
        // text that was actually matched.
        var rule = Or(Token('a'), Token('b'), Token('c'));
        var result = rule.Parse("b", new ParseOptions { PreserveFlattenWrappers = true });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("b"));
    }

    [Test]
    public void Or_failure_without_WithError_falls_back_to_positional_message()
    {
        // All alternatives fail, none have WithError. Null message at
        // offset 0, positional fallback renders.
        var rule = Or(Token('a'), Token('b'), Token('c'));
        var result = rule.Parse("x");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Does.StartWith("Parse failed at offset 0"));
    }

    [Test]
    public void Or_all_children_fail_at_same_position_first_writer_wins()
    {
        // All three alternatives try at offset 0 and fail. Each records at
        // pre-read position 0 with its own WithError message. Equal depth,
        // so the first-writer wins the message slot — that's Token('a'),
        // which Or tries first.
        var rule = Or(Token('a').WithError("want 'a'"),
                      Token('b').WithError("want 'b'"),
                      Token('c').WithError("want 'c'"));

        var result = rule.Parse("x");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("want 'a'"));
    }

    [Test]
    public void Or_child_that_consumes_deeper_wins_the_position_and_message()
    {
        // First branch matches "ab" then fails on 'x' at offset 2, recording
        // its Token('c') WithError there. Second branch matches "a" then
        // fails on 'b' at offset 1, recording its Token('d') WithError there.
        // Deepest-wins picks offset 2, so the first branch's "need 'c'"
        // surfaces.
        var rule = Or(And(Token('a'), Token('b'), Token('c').WithError("need 'c'")),
                      And(Token('a'), Token('d').WithError("need 'd'")));

        var result = rule.Parse("abx");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
        Assert.That(result.ErrorMessage, Is.EqualTo("need 'c'"));
    }

    [Test]
    public void Or_trace_success_produces_expected_output()
    {
        // Third alternative wins. Required-runes dispatch skips Token('a') and
        // Token('b') on lookahead 'c' — their FirstConsumedRunes don't contain 'c'
        // and neither is empty-capable — so only the matching Token('c')
        // branch opens a transaction and emits trace lines. The
        // nesting remains depth 2 (Or's transaction + Token's transaction).
        var sink = NewSink();
        Or(Token('a'), Token('b'), Token('c')).Parse("c", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'c', Consumed: 1",
            "      SUCC | Token: found 'c'",
            "   SUCC | Or: symbol #2"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Or_trace_failure_produces_expected_output()
    {
        // Required-runes dispatch rules out both Token('a') and Token('b') on
        // lookahead 'z', so no child transaction ever opens. By the time
        // Or emits its FAIL line after the loop, no transaction is open
        // and the line carries no indentation. Empty detail message means
        // there's no ": {detail}" tail, so the line reads "FAIL | Or".
        var sink = NewSink();
        Or(Token('a'), Token('b')).Parse("z", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "FAIL | Or"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }
}
