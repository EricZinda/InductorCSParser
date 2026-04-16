using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;

namespace InductorParser.Tests;

[TestFixture]
public class PeekRuleTests
{
    // Tree.ToString() assertions use PreserveFlattenWrappers so Peek,
    // Char, and AnyChar (all default FlattenType.Delete) stay in the
    // tree and their text contributes to the concatenated view.
    private static ParseOptions Debug() => new() { PreserveFlattenWrappers = true };

    [Test]
    public void Peek_succeeds_when_inner_matches_and_consumes_no_input()
    {
        // Peek(Char('a')) on "a": confirms 'a' is ahead without consuming
        // it. The trailing Char('a') then consumes it for real.
        var rule = And(Peek(Char('a')), Char('a'));
        var result = rule.Parse("a", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("a"));
    }

    [Test]
    public void Peek_fails_when_inner_fails()
    {
        var rule = Peek(Char('a')).WithError("expected an 'a' ahead");
        var result = rule.Parse("b");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected an 'a' ahead"));
    }

    [Test]
    public void Peek_does_not_advance_the_cursor_even_when_inner_consumes_multiple_tokens()
    {
        // Inner rule would consume two chars on success; Peek has to roll
        // those back. The trailing And(Char('a'), Char('b')) consumes them
        // for real, proving the cursor is at 0 after Peek.
        var rule = And(
            Peek(And(Char('a'), Char('b'))),
            Char('a'),
            Char('b'));

        var result = rule.Parse("ab", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("ab"));
    }

    [Test]
    public void Peek_works_under_rune_lexer()
    {
        var rule = And(Peek(Char('x')), AnyChar());
        var result = rule.Parse("x",
            new ParseOptions { InputUnit = InputUnit.Rune, PreserveFlattenWrappers = true });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("x"));
    }

    [Test]
    public void Peek_gates_optional_else_branch_in_if_statement()
    {
        // The if-statement example from PeekRule's class comment. Peek
        // confirms the "else" keyword is ahead; if it is, the real
        // keyword_else rule that follows consumes it for the parse tree.
        // If Peek fails the Optional short-circuits and leaves the cursor
        // wherever the then-branch ended.
        //
        // Without it the Optional's whole
        // body could start consuming whitespace and partial input before
        // discovering there is no else, and the lexer failure position
        // would point somewhere inside the abandoned attempt. The Peek
        // turns "is there an else?" into a zero-width, zero-consequence
        // check upfront.
        var keywordIf = And(Char('i'), Char('f'));
        var keywordThen = And(Char('t'), Char('h'), Char('e'), Char('n'));
        var keywordElse = And(Char('e'), Char('l'), Char('s'), Char('e'));

        var ifStatement = And(
            keywordIf, Char(' '), AnyChar(), Char(' '),
            keywordThen, Char(' '), AnyChar(),
            Optional(And(
                Char(' '),
                Peek(keywordElse),
                keywordElse, Char(' '), AnyChar())));

        var withElse = ifStatement.Parse("if x then 1 else 2", Debug());
        Assert.That(withElse.Success, Is.True, withElse.ErrorMessage);
        Assert.That(withElse.Tree!.ToString(), Is.EqualTo("if x then 1 else 2"));

        var withoutElse = ifStatement.Parse("if x then 1", Debug());
        Assert.That(withoutElse.Success, Is.True, withoutElse.ErrorMessage);
        Assert.That(withoutElse.Tree!.ToString(), Is.EqualTo("if x then 1"));
    }

    [Test]
    public void Peek_trace_success_produces_expected_output()
    {
        // Peek opens a transaction (depth=1). Char inside opens its own
        // (depth=2) and succeeds. Peek then emits its success line at
        // depth=1 and rolls back (doesn't commit).
        var sink = NewSink();
        Peek(Char('a')).Parse("a", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'a', Consumed: 1",
            "      SUCC | Char: found 'a'",
            "   SUCC | Peek: inner matched"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Peek_trace_failure_produces_expected_output()
    {
        var sink = NewSink();
        Peek(Char('a')).Parse("b", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'b', Consumed: 1",
            "      FAIL | Char: found 'b', wanted 'a'",
            "   FAIL | Peek: inner did not match"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }
}
