using System.Threading;
using NUnit.Framework;
using InductorParser.Lexing;
using InductorParser.Tracing;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;

namespace InductorParser.Tests;

// Cross-cutting trace-format tests. Per-rule trace output (Token, OneOf,
// Eof, AllOf, FirstOf, OneOrMore, ZeroOrMore, Optional) is in
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
        var rule = OneOrMore(OneOf(RuneSet.Ascii.Letters));
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
    public void Rule_name_appears_in_trace_label()
    {
        // .As("settingName") sets the user label. Rule.TraceLabel joins
        // it with the rule's class name via ":", producing
        // "settingName:OneOrMore" as the full trace label.
        var sink = NewSink();
        var settingName = OneOrMore(OneOf(RuneSet.Ascii.Letters))
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
            "   FAIL | Token: found 'x', wanted 'a' \"expected an A\""
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Lexer_Read_emits_one_line_per_token()
    {
        var sink = NewSink();
        var rule = AllOf(Token('a'), Token('b'), Token('c'));
        rule.Parse("abc", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'a', Consumed: 1",
            "      SUCC | Token: found 'a'",
            "      Lexer.Read: 'b', Consumed: 2",
            "      SUCC | Token: found 'b'",
            "      Lexer.Read: 'c', Consumed: 3",
            "      SUCC | Token: found 'c'",
            "   SUCC | AllOf: found 3"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Deepest_failure_update_is_announced_in_trace()
    {
        // The deepest-failure trace fires only when the new position is
        // strictly greater than the previous deepest. AllOf(a, b) on "ax"
        // advances past 'a' then fails at position 1, which is > 0, so
        // the announcement appears.
        var sink = NewSink();
        var rule = AllOf(Token('a'), Token('b'));
        rule.Parse("ax", new ParseOptions { TraceSink = sink });

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
        var rule = AllOf(Token('a'), Token('b'));
        rule.Parse("ab", new ParseOptions { TraceSink = sink1 });
        rule.Parse("ab", new ParseOptions { TraceSink = sink2 });

        string expected = Lines(
            "      Lexer.Read: 'a', Consumed: 1",
            "      SUCC | Token: found 'a'",
            "      Lexer.Read: 'b', Consumed: 2",
            "      SUCC | Token: found 'b'",
            "   SUCC | AllOf: found 2"
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
        // Diagnostic trace emissions are gated on TraceLevel >=
        // Diagnostic. With TraceLevel.Normal the sink stays empty even
        // though TraceSink is wired up.
        var sink = NewSink();
        var rule = AllOf(Token('a'), Token('b'));
        rule.Parse("ab", new ParseOptions { TraceSink = sink, TraceLevel = TraceLevel.Normal });

        Assert.That(sink.ToString(), Is.Empty);
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
        var lexer = new GraphemeLexer("x"); // no TraceSink = tracing off
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
        var lexer = new GraphemeLexer("x", sink, TraceLevel.Diagnostic);
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
        var lexer = new GraphemeLexer("x");  // no TraceSink = tracing off
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
        public TraceProbeRule() : base(SyntaxTree.FlattenType.Preserve) { }

        public void CallTraceSuccess(Lexer lexer, ref int sideEffectCount)
        {
            // Side effect inside the interpolation hole: if the
            // compiler deferred via the handler correctly, this
            // Interlocked.Increment never runs when tracing is off.
            TraceSuccess(lexer, $"value: {Interlocked.Increment(ref sideEffectCount)}");
        }

        internal override SyntaxTree.Symbol? TryParseRule(Lexer lexer, SyntaxTree.FlattenType effectiveFlattenType, System.Collections.Generic.List<SyntaxTree.Symbol>? outputSymbols) => null;
    }
}
