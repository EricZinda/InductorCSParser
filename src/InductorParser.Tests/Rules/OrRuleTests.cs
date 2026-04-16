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
        // Char defaults to FlattenType.Delete, so the matched 'b' would
        // be filtered out of the tree at parse time. PreserveFlattenWrappers
        // keeps the Char leaf in the tree so Tree.ToString() shows the
        // text that was actually matched.
        var rule = Or(Char('a'), Char('b'), Char('c'));
        var result = rule.Parse("b", new ParseOptions { PreserveFlattenWrappers = true });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("b"));
    }

    [Test]
    public void Or_failure_without_WithError_falls_back_to_positional_message()
    {
        // All alternatives fail, none have WithError. Null message at
        // offset 0, positional fallback renders.
        var rule = Or(Char('a'), Char('b'), Char('c'));
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
        // so the first-writer wins the message slot — that's Char('a'),
        // which Or tries first.
        var rule = Or(Char('a').WithError("want 'a'"),
                      Char('b').WithError("want 'b'"),
                      Char('c').WithError("want 'c'"));

        var result = rule.Parse("x");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("want 'a'"));
    }

    [Test]
    public void Or_child_that_consumes_deeper_wins_the_position_and_message()
    {
        // First branch matches "ab" then fails on 'x' at offset 2, recording
        // its Char('c') WithError there. Second branch matches "a" then
        // fails on 'b' at offset 1, recording its Char('d') WithError there.
        // Deepest-wins picks offset 2, so the first branch's "need 'c'"
        // surfaces.
        var rule = Or(And(Char('a'), Char('b'), Char('c').WithError("need 'c'")),
                      And(Char('a'), Char('d').WithError("need 'd'")));

        var result = rule.Parse("abx");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
        Assert.That(result.ErrorMessage, Is.EqualTo("need 'c'"));
    }

    [Test]
    public void Or_trace_success_produces_expected_output()
    {
        // Third alternative wins; each preceding alternative gets its
        // own transaction (depth 2 inside Or's depth 1) and fails.
        var sink = NewSink();
        Or(Char('a'), Char('b'), Char('c')).Parse("c", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'c', Consumed: 1",
            "      FAIL | Char: found 'c', wanted 'a'",
            "      Lexer.Read: 'c', Consumed: 1",
            "      FAIL | Char: found 'c', wanted 'b'",
            "      Lexer.Read: 'c', Consumed: 1",
            "      SUCC | Char: found 'c'",
            "   SUCC | Or: symbol #2"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Or_trace_failure_produces_expected_output()
    {
        // Each alternative's transaction is per-iteration, not per-Or:
        // when an alternative fails, its transaction disposes at the
        // end of that for-loop iteration, so the depth returns to zero
        // before the next alternative starts. By the time Or emits its
        // FAIL line (after the loop), no transaction is open and the
        // line carries no indentation. Empty detail message means
        // there's no ": {detail}" tail, so the line reads "FAIL | Or".
        var sink = NewSink();
        Or(Char('a'), Char('b')).Parse("z", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'z', Consumed: 1",
            "      FAIL | Char: found 'z', wanted 'a'",
            "      Lexer.Read: 'z', Consumed: 1",
            "      FAIL | Char: found 'z', wanted 'b'",
            "FAIL | Or"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }
}
