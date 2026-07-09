using System;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;

namespace InductorParser.Tests;

[TestFixture]
public class PeekRuleTests
{
    // Tree.ToString() assertions use PreserveAllSymbols so Peek,
    // Token, and AnyToken (all default FlattenType.Delete) stay in the
    // tree and their text contributes to the concatenated view.
    private static ParseOptions Debug() => new() { PreserveAllSymbols = true };

    [Test]
    public void Peek_factory_rejects_null_inner()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => Peek(null!));

        Assert.That(exception!.ParamName, Is.EqualTo("inner"));
    }

    [Test]
    public void Peek_succeeds_when_inner_matches_and_consumes_no_input()
    {
        // Peek(Token('a')) on "a": confirms 'a' is ahead without consuming
        // it. The trailing Token('a') then consumes it for real.
        var rule = And(Peek(Token('a')), Token('a'));
        var result = rule.Parse("a", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("a"));
    }

    [Test]
    public void Peek_fails_when_inner_fails()
    {
        var rule = Peek(Token('a')).WithError("expected an 'a' ahead");
        var result = rule.Parse("b");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected an 'a' ahead at line 1, column 1."));
    }

    [Test]
    public void Peek_WithError_surfaces_at_lookahead_anchor_when_inner_records_a_deeper_failure()
    {
        // Inner is Literal("ab") which on input "ax" reads 'a' (position
        // advances to 1), then reads 'x' and mismatches against 'b'. Literal
        // records its failure at position 1 with a null message. The Peek
        // rule rolls the lexer position back to 0 and rolls the deepest-
        // failure marker back the same way, then records its own failure
        // at the lookahead anchor (position 0) with the user's WithError.
        //
        // Without the fix, Peek's deepest-marker rollback didn't happen.
        // Literal's offset-1 record survived Peek's transaction rollback,
        // Peek's RecordFailure at offset 0 was shallower and got ignored,
        // and the user saw the generic positional template pointing at the
        // 'x' past the lookahead's anchor.
        var rule = Peek(Literal("ab")).WithError("expected 'ab' ahead");
        var result = rule.Parse("ax");

        Assert.That(result.Success, Is.False);
        // Both ErrorCharIndex and ErrorMessage match the lookahead's view:
        // anchored at 0, with the user-supplied friendly message. Anything
        // else means the inner's exploration leaked past Peek's rollback.
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected 'ab' ahead at line 1, column 1."));
    }

    [Test]
    public void Peek_failure_anchors_at_its_own_start_after_a_consumed_prefix()
    {
        // The other failure tests run Peek as the top-level rule, so
        // its anchor is offset 0. A Peek that hard-coded 0, or that
        // reported wherever its inner probe left the cursor, would
        // still pass them. Putting Peek after a consumed prefix gives
        // it a non-zero start that's distinct from both.
        //
        // On "abcxz": Literal("abc") consumes 0..2, leaving the cursor
        // at 3. Peek's inner Literal("xy") reads 'x', then mismatches
        // 'z' against 'y' at offset 4, so the inner failed and Peek
        // fails. Peek anchors its failure at its own start, offset 3,
        // not at input start (0), and not at offset 4 where the probe
        // got stuck.
        var rule = And(
            Literal("abc"),
            Peek(Literal("xy")).WithError("expected 'xy' after 'abc'"));
        var result = rule.Parse("abcxz");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected 'xy' after 'abc' at line 1, column 4."));
    }

    [Test]
    public void Peek_discards_inner_WithError_because_lookahead_failures_are_dropped()
    {
        // Peek runs its inner as a throwaway probe. When the probe fails,
        // the failures it produced (including a .WithError on the
        // inner) are discarded: they sit at a position the
        // parser only probed, never consumed. With no WithError of its
        // own, Peek records a bare mechanical failure at its anchor, so the
        // user sees the generic positional template, not the inner's
        // message. See docs/ErrorArchitecture.md, "Lookahead failures are
        // discarded".
        var rule = And(
            Peek(Token('a').WithError("expected 'a' ahead")),
            Token('b'));
        var result = rule.Parse("x");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Not.Contain("expected 'a' ahead"));
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
    }

    [Test]
    public void Peek_does_not_advance_the_cursor_even_when_inner_consumes_multiple_tokens()
    {
        // Inner rule would consume two chars on success. Peek has to roll
        // those back. The trailing And(Token('a'), Token('b')) consumes them
        // for real, proving the cursor is at 0 after Peek.
        var rule = And(
            Peek(And(Token('a'), Token('b'))),
            Token('a'),
            Token('b'));

        var result = rule.Parse("ab", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("ab"));
    }

    [Test]
    public void Peek_gates_optional_else_branch_in_if_statement()
    {
        // The if-statement example from PeekRule's class comment. Peek
        // confirms the "else" keyword is ahead. If it's there, the real
        // keyword_else rule that follows consumes it for the parse tree.
        // If Peek fails the Optional short-circuits and leaves the cursor
        // wherever the then-branch ended.
        //
        // Without it the Optional's whole
        // body could start consuming whitespace and partial input before
        // discovering there's no else, and the lexer failure position
        // would point somewhere inside the abandoned attempt. The Peek
        // turns "is there an else?" into a zero-width, zero-consequence
        // check upfront.
        var keywordIf = Literal("if");
        var keywordThen = Literal("then");
        var keywordElse = Literal("else");

        var ifStatement = And(
            keywordIf, Token(' '), AnyToken(), Token(' '),
            keywordThen, Token(' '), AnyToken(),
            Optional(And(
                Token(' '),
                Peek(keywordElse),
                keywordElse, Token(' '), AnyToken())));

        var withElse = ifStatement.Parse("if x then 1 else 2", Debug());
        Assert.That(withElse.Success, Is.True, withElse.ErrorMessage);
        Assert.That(withElse.Tree!.ToString(), Is.EqualTo("if x then 1 else 2"));

        var withoutElse = ifStatement.Parse("if x then 1", Debug());
        Assert.That(withoutElse.Success, Is.True, withoutElse.ErrorMessage);
        Assert.That(withoutElse.Tree!.ToString(), Is.EqualTo("if x then 1"));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Peek_trace_success_produces_expected_output()
    {
        // Peek opens a transaction (depth=1). Token inside opens its own
        // (depth=2) and succeeds. Peek then emits its success line at
        // depth=1 and rolls back (doesn't commit).
        var sink = NewSink();
        Peek(Token('a')).Parse("a", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'a', Consumed: 1",
            "      SUCC | Token: found 'a'",
            "   SUCC | Peek: inner matched"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Peek_trace_failure_produces_expected_output()
    {
        var sink = NewSink();
        Peek(Token('a')).Parse("b", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'b', Consumed: 1",
            "      FAIL | Token: found 'b', wanted 'a'",
            "   FAIL | Peek: inner didn't match"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Sealed_Peek_rejects_Flatten()
    {
        var rule = Peek(Token('a'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.Flatten(FlattenType.Preserve));
    }

    [Test]
    public void Sealed_Peek_rejects_WithError()
    {
        var rule = Peek(Token('a'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.WithError("late"));
    }

    [Test]
    public void Sealed_Peek_rejects_As()
    {
        var rule = Peek(Token('a'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.As("late"));
    }

    [Test]
    public void SourceRange_on_Peek_reports_zero_width_at_its_anchor()
    {
        // Peek matches but consumes nothing. The Preserve'd composite's
        // recorded span is a zero-length memory at the lookahead's
        // anchor offset, so SourceRange reports a zero-width range
        // there. SourceText is empty. Consumers can highlight "the
        // parser looked ahead here" without falsely claiming the
        // lookahead content was consumed.
        var peek = Peek(Literal("X")).As("peek");
        var rule = And(Literal("ab"), peek, Literal("X").Preserve()).Preserve();
        var result = rule.Parse("abX");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        var peekSymbol = result.Tree!.Find(peek)!;
        Assert.That(peekSymbol.SourceText, Is.EqualTo(string.Empty));
        var range = peekSymbol.SourceRange!.Value;
        // Anchor sits at offset 2, right after the "ab" prefix the
        // outer And consumed before Peek ran.
        Assert.That(range.Start.CharIndex, Is.EqualTo(2));
        Assert.That(range.End.CharIndex, Is.EqualTo(2));
    }

    [Test]
    public void SourceText_on_Peek_returns_empty_under_every_FlattenType()
    {
        // Peek's consumed span is always zero-width: it's a lookahead
        // and contributes no characters. SourceText is empty regardless
        // of FlattenType. Use Peek(Eof()) so the inner succeeds against
        // empty input: the outer rule consumes nothing and the parse
        // finishes without trailing input.
        SourceTextFlattenTypeMatrixHelper.AssertSourceTextUnderEveryFlattenType(
            ruleBuilder: () => Peek(Eof()),
            input: "",
            expectedSourceText: "");
    }
}
