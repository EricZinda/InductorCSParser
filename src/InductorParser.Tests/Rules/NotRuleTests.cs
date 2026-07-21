using System;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
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
    public void Not_factory_rejects_null_inner()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => Not(null!));

        Assert.That(exception!.ParamName, Is.EqualTo("inner"));
    }

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
        var rule = Not(Token('a')).WithError("didn't want an 'a'");
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("didn't want an 'a' at line 1, column 1."));
    }

    [Test]
    public void Not_WithError_surfaces_at_lookahead_anchor_when_inner_Or_records_a_deeper_orphan_failure()
    {
        // Inner is Or(Literal("ab"), Literal("a")). On input "a":
        //   * Or first tries Literal("ab"). It reads 'a' (advance to 1),
        //     then tries to read at 1 and hits EOF. Records failure at 1
        //     with a null message.
        //   * Or rolls that child back and tries Literal("a"). Matches 'a'
        //     and Or returns success.
        //
        // Inner overall succeeded, so Not fails. The deepest recorded
        // failure (1, null) is from Or's non-taken alternative: orphan
        // information about a path the parser deliberately abandoned.
        // Not's failure is anchored at offset 0 (where Not was called),
        // so Not rolls the deepest-failure marker back to its snapshot
        // before recording its own failure with the user's WithError.
        //
        // Without the fix the orphan failure at offset 1 survived Not's
        // rollback, Not's RecordFailure at offset 0 was shallower than
        // that orphan, the user's WithError was suppressed, and the
        // reported position pointed at end-of-input ('a' was consumed
        // exploring inner) instead of at the anchor where Not's friendly
        // message belongs.
        var inner = Or(Literal("ab"), Literal("a"));
        var rule = Not(inner).WithError("did not want 'a' or 'ab' here");
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("did not want 'a' or 'ab' here at line 1, column 1."));
    }

    [Test]
    public void Not_failure_anchors_at_its_own_start_after_a_consumed_prefix()
    {
        // The other failure tests run Not as the top-level rule, so its
        // anchor is offset 0. A Not that hard-coded 0, or that reported
        // wherever its inner probe left the cursor, would still pass
        // them. Putting Not after a consumed prefix gives it a non-zero
        // start that's distinct from both.
        //
        // On "abcxy": Literal("abc") consumes 0..2, leaving the cursor at
        // 3. Not's inner Literal("xy") probes 'x' and 'y' and matches, so
        // the inner succeeded and Not fails. Not anchors its failure at
        // its own start, offset 3, not at input start (0), and not at
        // offset 5 where the probe left off.
        var rule = And(
            Literal("abc"),
            Not(Literal("xy")).WithError("expected no 'xy' after 'abc'"));
        var result = rule.Parse("abcxy");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected no 'xy' after 'abc' at line 1, column 4."));
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
                Not(And(OneOf(TokenSet.Range('A', 'Z')),
                        ZeroOrMore(OneOf(TokenSet.Ascii.Letters)))),
                //   atom (deliberately case-insensitive, mirroring the
                //   real Prolog atom rule's fall-through branch):
                //     one or more letters of either case
                OneOrMore(OneOf(TokenSet.Ascii.Letters)),
                //   optional parenthesised single-letter argument
                Optional( And(Token('('),
                              OneOf(TokenSet.Ascii.Letters),
                              Token(')'))));

        // Lowercase-start parses as a functor, with or without arguments.
        Assert.That(functor.Parse("foo").Success, Is.True);
        Assert.That(functor.Parse("foo(a)").Success, Is.True);

        // Uppercase-start hits Not(variable) and the whole functor rule fails.
        Assert.That(functor.Parse("Foo").Success, Is.False);
        Assert.That(functor.Parse("X(a)").Success, Is.False);
    }

    [Test]
    [RecursiveEngineOnly]
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
            "   SUCC | Not: inner didn't match"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    [RecursiveEngineOnly]
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

    [Test]
    public void Sealed_Not_rejects_Flatten()
    {
        var rule = Not(Token('a'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.Flatten(FlattenType.Preserve));
    }

    [Test]
    public void Sealed_Not_rejects_WithError()
    {
        var rule = Not(Token('a'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.WithError("late"));
    }

    [Test]
    public void Sealed_Not_rejects_As()
    {
        var rule = Not(Token('a'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.As("late"));
    }

    [Test]
    public void SourceRange_on_Not_reports_zero_width_at_its_anchor()
    {
        // Not succeeds when inner fails, and consumes nothing either way.
        // The Preserve'd composite records a zero-length consumed span
        // at the negative-lookahead's anchor. SourceRange reports a
        // zero-width range there, SourceText is empty. Consumers can
        // highlight "the parser asserted X isn't here" at the right
        // offset without claiming any text was matched.
        var notRule = Not(Literal("Z")).As("guard");
        var rule = And(Literal("ab"), notRule, Literal("X").Preserve()).Preserve();
        var result = rule.Parse("abX");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        var notSymbol = result.Tree!.Find(notRule)!;
        Assert.That(notSymbol.SourceText, Is.EqualTo(string.Empty));
        var range = notSymbol.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(2));
        Assert.That(range.End.CharIndex, Is.EqualTo(2));
    }

    [Test]
    public void SourceText_on_Not_returns_empty_under_every_FlattenType()
    {
        // Not is zero-width: it succeeds when its inner rule FAILS and
        // contributes no characters either way. SourceText is empty
        // regardless of FlattenType. Use Not(Literal("X")) against
        // empty input: inner fails, Not succeeds, the outer rule consumes
        // nothing, parse finishes cleanly.
        SourceTextFlattenTypeMatrixHelper.AssertSourceTextUnderEveryFlattenType(
            ruleBuilder: () => Not(Literal("X")),
            input: "",
            expectedSourceText: "");
    }

    [Test]
    public void Not_discards_inner_lookahead_failures_on_success()
    {
        // Not is lookahead: it runs its inner as a throwaway probe. When
        // Not succeeds (the inner failed, which is what Not wanted), the
        // failures the probe produced are discarded. They sit at a
        // position the parser only probed, never consumed. So the inner's
        // .WithError doesn't surface. See docs/ErrorArchitecture.md,
        // "Lookahead failures are discarded".
        var notForbidden = Not(Literal("forbidden").WithError("inner literal not satisfied"));
        var rule = And(notForbidden, Literal("xyz"));

        // Input "abc": Not succeeds (inner fails), then Literal("xyz")
        // fails at position 0 with a mechanical failure. The inner
        // WithError failure was discarded when the Not probe finished.
        var result = rule.Parse("abc");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Not.Contain("inner literal"));
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
    }
}
