using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;

namespace InductorParser.Tests;

[TestFixture]
public class NotRuleTests
{
    // Tree.ToString() assertions use PreserveAllSymbols so Not,
    // Token, and AnyToken (all default FlattenType.Delete) stay in the
    // tree and their text contributes to the concatenated view.
    private static ParseOptions Debug() => new() { PreserveAllSymbols = true };

    [Test]
    public void Not_succeeds_when_inner_fails_and_consumes_no_input()
    {
        // Not(Token('a')) on "b": Token('a') fails, Not succeeds and leaves
        // the cursor at 0. The trailing Token('b') then consumes 'b'.
        var rule = And(Not(Token('a')), Token('b'));
        var result = rule.Parse("b", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("b"));
    }

    [Test]
    public void Not_fails_when_inner_matches()
    {
        var rule = Not(Token('a')).WithError("did not want an 'a'");
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("did not want an 'a'"));
    }

    [Test]
    public void Not_does_not_advance_the_cursor_even_when_inner_consumes_before_failing()
    {
        // And(Token('a'), Token('b')) would consume two chars before failing
        // on "ax" (reads 'a', then fails on 'x'). Wrapping it in Not, the
        // outer cursor must still be 0 after Not succeeds. The trailing
        // Token('a') proves it: if Not had failed to roll back, Token('a')
        // would look at offset 2 ('<EOF>') or later.
        var rule = And(
            Not(And(Token('a'), Token('b'))),
            Token('a'));

        var result = rule.Parse("ax");

        Assert.That(result.Success, Is.False);
        // Token('a') at offset 0 succeeds. The overall parse fails because
        // input isn't fully consumed. What matters is that Not didn't
        // leave the cursor advanced. If it had, the trailing Token('a')
        // would have reported somewhere past offset 0.
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
    }

    [Test]
    public void Not_rule_based_passthrough_stops_at_the_inner_rule()
    {
        // The rule-based pass-through idiom: consume any character that
        // isn't the start of the stop rule. Here the stop is '!'. The
        // body is arbitrary text up to (but not including) it.
        var rule = And(
            ZeroOrMore(And(Not(Token('!')), AnyToken())),
            Token('!'));

        var result = rule.Parse("hello world!", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("hello world!"));
    }

    [Test]
    public void Not_disambiguates_overlapping_rules_at_the_same_cursor()
    {
        // Pattern from EricZinda/InductorProlog's PrologFunctor rule:
        //     AndExpression<Args<NotPeekExpression<VariableRule>, PrologAtom>>
        // Prolog variable names (uppercase-start) and atom names
        // (typically lowercase-start) share the same structural shape, and
        // the atom rule in that grammar is loose enough to accept either.
        // Without Not, the atom rule would happily match "Foo" and the
        // surrounding functor rule would proceed, even though the caller
        // meant "Foo" to parse as a variable. Not(variable) at the same
        // cursor says "if this looks like a variable, bail before the atom
        // branch commits,".
        var functor =
            And(
                //   variable (the thing we want to reject at this cursor):
                //     upper letter, then any letters
                Not(And(OneOf(RuneSet.Range('A', 'Z')),
                        ZeroOrMore(OneOf(RuneSet.Ascii.Letters)))),
                //   atom (deliberately case-insensitive, mirroring the
                //   real Prolog atom rule's fall-through branch):
                //     one or more letters of either case
                OneOrMore(OneOf(RuneSet.Ascii.Letters)),
                //   optional parenthesised single-letter argument
                Optional( And(Token('('),
                              OneOf(RuneSet.Ascii.Letters),
                              Token(')'))));

        // Lowercase-start parses as a functor, with or without arguments.
        Assert.That(functor.Parse("foo").Success, Is.True);
        Assert.That(functor.Parse("foo(a)").Success, Is.True);

        // Uppercase-start hits Not(variable) and the whole functor rule fails.
        Assert.That(functor.Parse("Foo").Success, Is.False);
        Assert.That(functor.Parse("X(a)").Success, Is.False);
    }

    [Test]
    public void Not_works_under_rune_lexer()
    {
        // Same negative-lookahead semantics under RuneLexer.
        var rule = And(Not(Token('a')), AnyToken());
        var result = rule.Parse("b",
            new ParseOptions { InputUnit = InputUnit.Rune, PreserveAllSymbols = true });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("b"));
    }

    [Test]
    public void Not_trace_success_produces_expected_output()
    {
        // Not opens a transaction (depth=1). Token inside opens its own
        // (depth=2) and fails on the Read. Not then emits its success
        // line at depth=1 and rolls back.
        var sink = NewSink();
        Not(Token('a')).Parse("b", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'b', Consumed: 1",
            "      FAIL | Token: found 'b', wanted 'a'",
            "   SUCC | Not: inner did not match"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Not_trace_failure_produces_expected_output()
    {
        var sink = NewSink();
        Not(Token('a')).Parse("a", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'a', Consumed: 1",
            "      SUCC | Token: found 'a'",
            "   FAIL | Not: inner matched"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }
}
