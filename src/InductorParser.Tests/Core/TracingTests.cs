using System.Threading;
using NUnit.Framework;
using InductorParser.Lexing;
using InductorParser.Tracing;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;

namespace InductorParser.Tests;

// Cross-cutting trace-format tests. Per-rule trace output (Token, OneOf,
// Eof, And, Or, OneOrMore, ZeroOrMore, Optional) is in
// each rule's own test file, so the failure surfaces right next to the
// rule being edited. This file covers the concerns that aren't any one
// rule's property
[TestFixture]
public class TracingTests
{
    [Test]
    public void Null_TraceSink_parses_without_throwing()
    {
        // Smoke test: parse with the default (null) TraceSink. Catches
        // regressions where someone adds code that dereferences
        // _traceSink without a null check before the handler runs.
        // That kind of bug would throw a NullReferenceException here
        // rather than silently misbehaving. The stronger guarantee
        // ("nothing inside an interpolation hole runs when tracing is
        // off") is proven by Off_path_does_not_evaluate_interpolated_arguments
        // below, using a side-effect counter to verify no work happens.
        var rule = OneOrMore(OneOf(TokenSet.Ascii.Letters));
        var result = rule.Parse("abc", new ParseOptions());
        Assert.That(result.Success, Is.True);
    }

    [Test]
    public void Default_ParseOptions_has_null_TraceSink_and_Diagnostic_level()
    {
        var options = new ParseOptions();
        Assert.That(options.TraceSink, Is.Null);
        Assert.That(options.TraceLevel, Is.EqualTo(TraceLevel.Diagnostic));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Rule_name_appears_in_trace_label()
    {
        // .As("settingName") sets the user label. Rule.TraceLabel joins
        // it with the rule's class name via ":", producing
        // "settingName:OneOrMore" as the full trace label.
        var sink = NewSink();
        var settingName = OneOrMore(OneOf(TokenSet.Ascii.Letters))
            .As("settingName");
        settingName.Parse("foo", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'f', Consumed: 1",
            "      SUCC | OneOf: found 'f', wanted one of '[A-Z,a-z]'",
            "      Lexer.Read: 'o', Consumed: 2",
            "      SUCC | OneOf: found 'o', wanted one of '[A-Z,a-z]'",
            "      Lexer.Read: 'o', Consumed: 3",
            "      SUCC | OneOf: found 'o', wanted one of '[A-Z,a-z]'",
            "      Lexer.Read: '<EOF>', Consumed: 3",
            "      FAIL | OneOf: found '<EOF>', wanted one of '[A-Z,a-z]'",
            "      Lexer.RecordFailure: new deepest failure at char 3",
            "   SUCC | settingName:OneOrMore: count= 3"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    [RecursiveEngineOnly]
    public void WithError_message_in_trace_escapes_control_chars()
    {
        // The user's .WithError text is appended to the FAIL trace body
        // by Rule.AppendErrorMessage AFTER the interpolation handler
        // runs, so the handler's auto-escape (which covers every $"..."
        // hole in a rule's trace body) doesn't reach it. A control char
        // in the user's message used to splice verbatim into the FAIL
        // line and break the one-event-per-line layout. AppendErrorMessage
        // routes the message through DisplayEscape.Escape for the trace
        // path. The stored _errorMessage stays the user's exact text so
        // ParseResult.ErrorMessage still shows it verbatim.
        var sink = NewSink();
        var rule = Token('a').WithError("line1\nline2");
        rule.Parse("x", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: 'x', Consumed: 1",
            "   FAIL | Token: found 'x', wanted 'a' \"line1U+000Aline2\"",
            "   Lexer.RecordFailure: first named failure at char 0"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    [RecursiveEngineOnly]
    public void WithError_message_appears_in_quotes_after_trace_body_on_failure()
    {
        // .WithError() is the user-facing error message, not a rule
        // identity. It's the friendly thing a grammar author wants the
        // end user to see when a parse fails. In trace output it gets
        // appended after the trace body in quotes, so a reader sees
        // both what the rule actually tried ("found 'x', wanted 'a'")
        // and the friendly message that would surface on a real parse
        // failure ("expected an A"). It DOESN'T appear as part of the
        // trace label. That position is reserved for .As() names.
        var sink = NewSink();
        var rule = Token('a').WithError("expected an A");
        rule.Parse("x", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: 'x', Consumed: 1",
            "   FAIL | Token: found 'x', wanted 'a' \"expected an A\"",
            "   Lexer.RecordFailure: first named failure at char 0"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Lexer_Read_emits_one_line_per_token()
    {
        var sink = NewSink();
        var rule = And(Token('a'), Token('b'), Token('c'));
        rule.Parse("abc", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'a', Consumed: 1",
            "      SUCC | Token: found 'a'",
            "      Lexer.Read: 'b', Consumed: 2",
            "      SUCC | Token: found 'b'",
            "      Lexer.Read: 'c', Consumed: 3",
            "      SUCC | Token: found 'c'",
            "   SUCC | And: found 3"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Lexer_Read_trace_escapes_control_chars_in_token_text()
    {
        // Lexer.Read's trace line quotes the consumed token text between
        // single quotes. For a CRLF cluster (one token, two control-char
        // runes), splicing the raw chars verbatim would dump a literal
        // CR+LF into the middle of the trace line, breaking the
        // one-event-per-line layout every other trace assertion relies
        // on. Same shape TokenSet.ToString already escapes via
        // AppendGraphemeForDisplay, and the same shape PrintTree escapes
        // in its short form: control characters (Cc) and line / paragraph
        // separators (Zl / Zp) render as U+XXXX so the trace stays one
        // line per event.
        var sink = NewSink();
        var lexer = new Lexer("\r\n", traceSink: sink, traceLevel: TraceLevel.Diagnostic);
        lexer.Read();

        // Without the fix, the sink contains a literal CR+LF between the
        // single quotes, so the line splits into three physical lines
        // (an empty "Lexer.Read: '" line, an empty middle line, and the
        // trailing "', Consumed: 2" line). With the fix, both CR and LF
        // escape to U+XXXX and the line stays intact.
        string expected = "Lexer.Read: 'U+000DU+000A', Consumed: 2\n";
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Rule_match_trace_escapes_control_chars_in_matched_token_text()
    {
        // Lexer.Read's own trace line now escapes control chars (see the
        // test above), but every rule that matches a token ALSO emits its
        // own "found '<token>'" line built from
        // lexer.Input.Substring(token.Offset, token.Length). AnyToken is
        // the simplest: it succeeds on any single token and quotes the
        // matched text. For an LF token the raw splice dumps a literal
        // newline into the middle of the SUCC line, splitting the
        // one-event-per-line layout. The matched text must escape to
        // U+XXXX the same way Lexer.Read does.
        var sink = NewSink();
        var rule = AnyToken();
        rule.Parse("\n", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: 'U+000A', Consumed: 1",
            "   SUCC | AnyToken: found 'U+000A'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Rule_mismatch_trace_escapes_control_chars_in_matched_token_text()
    {
        // The failure path has the same leak: OneOf's "found '<token>',
        // wanted one of '...'" line quotes the rejected token's raw text.
        // A mechanical failure at char 0 records no Lexer.RecordFailure
        // trace line (0 is not strictly past the initial deepest of 0), so
        // the trace is just the Read line and the FAIL line. The rejected
        // LF must escape to U+XXXX so the FAIL line stays one physical line.
        var sink = NewSink();
        var rule = OneOf(TokenSet.Ascii.Letters);
        rule.Parse("\n", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: 'U+000A', Consumed: 1",
            "   FAIL | OneOf: found 'U+000A', wanted one of '[A-Z,a-z]'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Token_match_trace_escapes_control_chars_in_expected_literal()
    {
        // The other half of the leak is the rule's stored expected text:
        // GraphemeRule's success line is "found '{_expected}'", and for
        // Token('\n') that _expected is a raw LF. Preserve() keeps the
        // match in the tree but the trace concern is independent of the
        // flatten policy. The expected literal must escape to U+XXXX too.
        var sink = NewSink();
        var rule = Token('\n').Preserve();
        rule.Parse("\n", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: 'U+000A', Consumed: 1",
            "   SUCC | Token: found 'U+000A'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    [RecursiveEngineOnly]
    public void WithinToken_match_trace_escapes_control_chars_in_outer_token_text()
    {
        // WithinToken's success line quotes the outer token text via
        // outerLexer.Input.AsSpan(token.Offset, token.Length), which
        // renders the raw chars when interpolated. A single-rune LF token
        // matched by the inner rule must escape to U+XXXX so the SUCC line
        // stays one physical line.
        var sink = NewSink();
        var rule = WithinToken(AnyToken());
        rule.Parse("\n", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: 'U+000A', Consumed: 1",
            "   SUCC | WithinToken: token 'U+000A' matched inner rule"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Trace_label_escapes_control_chars_in_rule_name()
    {
        // The trace label is "{Name}:{ruleClassName}", where Name is the
        // user's .As("...") string spliced verbatim by WriteTraceLine.
        // .As(string) doesn't validate the name's content, so a name
        // carrying a control / line-separator char (a name built from
        // data, a stray "\n" in a constant) used to dump that char
        // straight into the SUCC / FAIL line and split the
        // one-event-per-line layout. Every other dynamic text on a trace
        // line, the matched token, the stored literal, a .WithError
        // message, already escapes to U+XXXX. The label has to match so
        // the same control char in a rule name renders as U+XXXX too.
        var sink = NewSink();
        var rule = OneOf(TokenSet.Ascii.Digits).As("dig\nit");
        rule.Parse("5", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: '5', Consumed: 1",
            "   SUCC | dig" + "U+000A" + "it:OneOf: found '5', wanted one of '[0-9]'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Deepest_failure_update_is_announced_in_trace()
    {
        // The deepest-failure trace fires only when the new position is
        // strictly greater than the previous deepest. And(a, b) on "ax"
        // advances past 'a' then fails at position 1, which is > 0, so
        // the announcement appears.
        var sink = NewSink();
        var rule = And(Token('a'), Token('b'));
        rule.Parse("ax", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'a', Consumed: 1",
            "      SUCC | Token: found 'a'",
            "      Lexer.Read: 'x', Consumed: 2",
            "      FAIL | Token: found 'x', wanted 'b'",
            "      Lexer.RecordFailure: new deepest failure at char 1",
            "   FAIL | And: symbol #1"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Forced_deepest_failure_update_is_announced_in_trace()
    {
        // The forced slot has its own trace line: "forced deepest failure
        // at char N" instead of "new deepest failure at char N". Same
        // strictly-greater gate: the announcement only fires when the new
        // forced position advances past the previous one.
        //
        // And(a, b.WithError(forced: true)) on "ax" advances past 'a'
        // then the forced b fails at position 1, which is > the initial
        // forced position of 0, so the announcement appears. The trailing
        // mechanical announcement comes from And's own RecordCompositeFailure
        // (no .WithError on the And, so errorMessage=null routes through the
        // mechanical slot, which is still at 0 and advances to 1).
        var sink = NewSink();
        var rule = And(
            Token('a'),
            Token('b').WithError("expected B", forced: true));
        rule.Parse("ax", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'a', Consumed: 1",
            "      SUCC | Token: found 'a'",
            "      Lexer.Read: 'x', Consumed: 2",
            "      FAIL | Token: found 'x', wanted 'b' \"expected B\"",
            "      Lexer.RecordFailure: forced deepest failure at char 1",
            "   FAIL | And: symbol #1",
            "   Lexer.RecordFailure: new deepest failure at char 1"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    [RecursiveEngineOnly]
    public void First_forced_failure_at_position_zero_is_announced_in_trace()
    {
        // The forced slot's first-fill case: a forced .WithError fails at
        // position 0 before the lexer has moved. The position write is a
        // no-op (0 to 0) but the message is genuinely captured, and the
        // "first forced failure at char 0" trace line announces it.
        var sink = NewSink();
        var rule = Token('a').WithError("expected A", forced: true);
        rule.Parse("x", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: 'x', Consumed: 1",
            "   FAIL | Token: found 'x', wanted 'a' \"expected A\"",
            "   Lexer.RecordFailure: first forced failure at char 0"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Transaction_depth_returns_to_zero_after_parse()
    {
        // Regression guard: Dispose must decrement the depth on every
        // exit path, including success and failure. If a commit or
        // rollback short-circuited the decrement, later parses would
        // accumulate incorrect indentation. We run the same parse
        // twice and assert that (a) the first parse produces the
        // exact expected trace output, and (b) the second parse
        // produces the same output as the first. Without (a), both
        // parses could silently produce the same wrong indentation
        // and the test would pass. Without (b), a depth-leak bug
        // that changed the second run would slip through.
        var sink1 = NewSink();
        var sink2 = NewSink();
        var rule = And(Token('a'), Token('b'));
        rule.Parse("ab", new ParseOptions { TraceSink = sink1 });
        rule.Parse("ab", new ParseOptions { TraceSink = sink2 });

        string expected = Lines(
            "      Lexer.Read: 'a', Consumed: 1",
            "      SUCC | Token: found 'a'",
            "      Lexer.Read: 'b', Consumed: 2",
            "      SUCC | Token: found 'b'",
            "   SUCC | And: found 2"
        );
        Assert.That(sink1.ToString(), Is.EqualTo(expected),
            "First parse's trace should match the expected output exactly.");
        Assert.That(sink2.ToString(), Is.EqualTo(sink1.ToString()),
            "Parsing the same input twice should produce identical trace output; " +
            "divergence means transaction depth leaked across parses.");
    }

    [Test]
    public void TraceLevel_Normal_suppresses_output()
    {
        // Diagnostic trace outputs are gated on TraceLevel >=
        // Diagnostic. With TraceLevel.Normal the sink stays empty even
        // though TraceSink is wired up. The grammar below exercises
        // every rule type (Token, OneOf, And, Or, OneOrMore,
        // ZeroOrMore, Optional, Eof) on both success and failure paths,
        // so an ungated trace emission added to any single rule would
        // leak into the sink and fail this test.
        //
        // Walking "ab" through the grammar:
        //   OneOrMore iter 1: Or(a|b) matches 'a' on first branch
        //   OneOrMore iter 2: Or(a|b) fails first branch then
        //                     matches 'b' on second (Or backtrack)
        //   OneOrMore iter 3: both branches fail at EOF (loop ends)
        //   Optional('z'):    fails at EOF, Optional still succeeds
        //   ZeroOrMore('!'):  fails at EOF, ZeroOrMore still succeeds
        //   Eof:              succeeds at EOF
        var rule = And(
            OneOrMore(Or(Token('a'), OneOf("b"))),
            Optional(Token('z')),
            ZeroOrMore(Token('!')),
            Eof()
        );

        var normalSink = NewSink();
        rule.Parse("ab", new ParseOptions { TraceSink = normalSink, TraceLevel = TraceLevel.Normal });
        Assert.That(normalSink.ToString(), Is.Empty);

        // Sanity check: the same grammar must produce trace output at
        // TraceLevel.Diagnostic. Without this assertion, a regression
        // that silently disabled all tracing would pass the empty-sink
        // check above for the wrong reason.
        var diagnosticSink = NewSink();
        rule.Parse("ab", new ParseOptions { TraceSink = diagnosticSink, TraceLevel = TraceLevel.Diagnostic });
        Assert.That(diagnosticSink.ToString(), Is.Not.Empty);
    }

    [Test]
    public void Off_path_does_not_evaluate_interpolated_arguments()
    {
        // Proves the C# compiler actually rewrote $"..." to use our
        // TraceInterpolatedStringHandler's shouldAppend gate, not
        // eagerly-built strings. A side effect placed inside the
        // interpolation hole must NOT run when the handler's
        // constructor returns shouldAppend=false.
        //
        // If this test fails with sideEffectCount == 1, the compiler
        // silently fell back to eager interpolation. That would mean
        // the attributes aren't being recognized (polyfill broken on
        // this target framework? LangVersion regression?) and every
        // trace call is allocating per parse even when the sink
        // is null.
        var lexer = new Lexer("x"); // no TraceSink = tracing off
        int sideEffectCount = 0;

        lexer.Trace(TraceLevel.Diagnostic, "test", TraceOutcome.Info,
            $"value: {Interlocked.Increment(ref sideEffectCount)}");

        Assert.That(sideEffectCount, Is.EqualTo(0),
            "Tracing off: the $\"...\" argument must not be evaluated. " +
            "If this fails, the interpolated string handler rewrite isn't " +
            "happening and tracing is allocating on the hot path.");
    }

    [Test]
    public void On_path_evaluates_interpolated_arguments_exactly_once()
    {
        // Complement to the "off path" test above. When the handler's
        // shouldAppend=true, arguments must be evaluated exactly once
        // (not zero, not twice). Zero would mean AppendFormatted is
        // never called even when tracing is on. Two would mean the
        // compiler generated a spurious extra evaluation.
        var sink = NewSink();
        var lexer = new Lexer("x", traceSink: sink, traceLevel: TraceLevel.Diagnostic);
        int sideEffectCount = 0;

        lexer.Trace(TraceLevel.Diagnostic, "test", TraceOutcome.Info,
            $"value: {Interlocked.Increment(ref sideEffectCount)}");

        Assert.That(sideEffectCount, Is.EqualTo(1),
            "Tracing on: exactly one evaluation of each interpolated arg.");
    }

    [Test]
    public void Rule_TraceSuccess_off_path_does_not_evaluate_interpolated_arguments()
    {
        // Parallels Off_path_does_not_evaluate_interpolated_arguments
        // but exercises the Rule.TraceSuccess / TraceFailure helper
        // path (the 4-arg handler constructor overload) rather than
        // Lexer.Trace directly. If the overload resolution ever breaks
        // or the handler attribute on the Rule helpers stops matching,
        // the side effect inside the interpolation hole will fire and
        // this test catches it.
        var lexer = new Lexer("x");  // no TraceSink = tracing off
        var rule = new TraceProbeRule();
        int sideEffectCount = 0;

        rule.CallTraceSuccess(lexer, ref sideEffectCount);

        Assert.That(sideEffectCount, Is.EqualTo(0),
            "Rule.TraceSuccess with tracing off: the $\"...\" argument " +
            "must not be evaluated. If this fails, the Rule-helper handler " +
            "rewrite isn't happening and tracing allocates even from rules.");
    }

    // Test-only Rule subclass that exposes Rule.TraceSuccess via a
    // public method so the test can exercise it directly. Rule's
    // TraceSuccess is protected, so a derived class is the natural
    // way to reach it from test code.
    private sealed class TraceProbeRule : Rule
    {
        public TraceProbeRule() : base(SyntaxTree.FlattenType.Preserve, emitsLeaf: false) { }

        public void CallTraceSuccess(Lexer lexer, ref int sideEffectCount)
        {
            // Side effect inside the interpolation hole: if the
            // compiler deferred via the handler correctly, this
            // Interlocked.Increment never runs when tracing is off.
            TraceSuccess(lexer, $"value: {Interlocked.Increment(ref sideEffectCount)}");
        }

        protected internal override SyntaxTree.Symbol? TryParseRule(Lexer lexer, int startPosition, SyntaxTree.FlattenType effectiveFlattenType, System.Collections.Generic.List<SyntaxTree.Symbol>? outputSymbols) => null;
    }
}
