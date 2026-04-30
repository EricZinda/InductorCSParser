using System.Linq;
using NUnit.Framework;
using InductorParser;
using InductorParser.StateMachine;
using InductorParser.Tracing;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;

namespace InductorParser.Tests.StateMachine;

// Trace-format tests for the state-machine evaluator. The SM only
// emits rule-level trace at Call / ReturnSuccess / ReturnFailure
// boundaries, which the lowerer only generates for cyclic rules
// (LateBound targets) and for ScanUntil's escape-end / rule-stopper
// subprograms. Inlined rules (the common case) produce no SM-side
// rule traces. Lexer-level traces (Lexer.Read, RecordFailure) still
// fire under the SM because both engines share one Lexer.
//
// These tests use a recursive grammar built via LateBoundRule so the
// SM's lowerer emits an actual Call opcode whose Return* the trace
// emit hooks can fire on. Assertions are structural (label appears
// with the right outcome, level-gating respected) rather than
// byte-for-byte against the recursive engine, because the SM's
// trace stream omits the rule-specific body text the recursive
// engine builds (e.g. "count= 3", "found 3") and skips the inlined
// rules entirely.
[TestFixture]
public class StateMachineTracingTests
{
    // Build a tiny self-referential grammar: parens = ( "(" parens ")" )*
    // Returns the LateBound and the inner ZeroOrMore (the rule the
    // lowerer actually treats as cyclic — LateBound is transparent
    // and gets unwrapped during cycle detection). The inner rule
    // carries the .As("parensInner") name so the trace label is
    // predictable.
    private static (LateBoundRule LateBound, Rule InnerCyclic) BuildParensGrammar()
    {
        var parens = new LateBoundRule("parens");
        var inner = ZeroOrMore(AllOf(Grapheme('('), parens, Grapheme(')'))).As("parensInner");
        parens.Bind(inner);
        return (parens, inner);
    }

    [Test]
    public void Cyclic_rule_emits_success_trace_line_with_rule_label()
    {
        var (parens, inner) = BuildParensGrammar();
        var rooted = AllOf(parens, Eof());
        var sink = NewSink();

        var result = StateMachineParser.Parse(rooted, "(())", new ParseOptions { TraceSink = sink });
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        // The cyclic rule fires once per nesting level plus once at
        // the root call (3 entries for "(())"). Every Return* the
        // Stepper executes for it should emit one SUCC line carrying
        // the rule's TraceLabel ("parensInner:ZeroOrMore").
        string output = sink.ToString();
        string expectedLabel = inner.TraceLabel;
        int successCount = output.Split('\n')
            .Count(line => line.Contains("SUCC | " + expectedLabel));
        Assert.That(successCount, Is.GreaterThanOrEqualTo(1),
            $"Expected at least one 'SUCC | {expectedLabel}' line in trace output:\n{output}");
    }

    [Test]
    public void Cyclic_rule_emits_failure_trace_line_when_inner_attempt_fails()
    {
        // Parens grammar's inner ZeroOrMore can't fail (zero matches
        // is success), so use an AllOf-shaped recursive rule whose
        // body must consume at least one '[' character. With input
        // that doesn't open a bracket, the cyclic rule fails on its
        // first attempt and the SM should emit a FAIL trace line
        // labeled with the rule's TraceLabel.
        var nested = new LateBoundRule("nested");
        var inner = AllOf(Grapheme('['), Optional(nested), Grapheme(']')).As("nestedInner");
        nested.Bind(inner);
        var rooted = AllOf(nested, Eof());

        var sink = NewSink();
        var result = StateMachineParser.Parse(rooted, "abc", new ParseOptions { TraceSink = sink });
        Assert.That(result.Success, Is.False);

        string output = sink.ToString();
        string expectedLabel = inner.TraceLabel;
        int failureCount = output.Split('\n')
            .Count(line => line.Contains("FAIL | " + expectedLabel));
        Assert.That(failureCount, Is.GreaterThanOrEqualTo(1),
            $"Expected at least one 'FAIL | {expectedLabel}' line in trace output:\n{output}");
    }

    [Test]
    public void TraceLevel_Normal_suppresses_state_machine_trace_output()
    {
        // The SM's Call / Return trace emit is gated by IsTracing(Diagnostic).
        // A parse run with TraceLevel.Normal must produce an empty sink
        // even on a cyclic grammar that would normally emit several
        // rule-level trace lines.
        var (parens, _) = BuildParensGrammar();
        var rooted = AllOf(parens, Eof());

        var sink = NewSink();
        var result = StateMachineParser.Parse(
            rooted, "(())",
            new ParseOptions { TraceSink = sink, TraceLevel = TraceLevel.Normal });
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        Assert.That(sink.ToString(), Is.Empty,
            "TraceLevel.Normal must suppress SM-side rule trace lines.");
    }

    [Test]
    public void Null_TraceSink_runs_cyclic_grammar_without_throwing()
    {
        // Smoke test: with no sink, the SM's Call / Return path must
        // not dereference null when looking up the source rule or
        // deciding whether to emit. The IsTracing guard inside
        // EmitCallReturnTrace returns early on a null sink, so this
        // should parse cleanly.
        var (parens, _) = BuildParensGrammar();
        var rooted = AllOf(parens, Eof());

        var result = StateMachineParser.Parse(rooted, "((()))", new ParseOptions());
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Trace_outcomes_appear_in_call_nesting_order()
    {
        // For a nested input like "(())" the cyclic rule fires three
        // times (root call + one recursive call per nesting level).
        // Every successful exit produces one SUCC line. The total
        // count therefore tracks input nesting structure: balanced
        // parens match cleanly so every exit is a success, no FAIL
        // for the cyclic rule itself appears in the trace.
        var (parens, inner) = BuildParensGrammar();
        var rooted = AllOf(parens, Eof());
        var sink = NewSink();

        var result = StateMachineParser.Parse(rooted, "(())", new ParseOptions { TraceSink = sink });
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        string output = sink.ToString();
        string expectedLabel = inner.TraceLabel;

        var lines = output.Split('\n').Where(line => line.Contains(expectedLabel)).ToArray();
        Assert.That(lines.Length, Is.GreaterThanOrEqualTo(3),
            $"Expected at least three trace lines for {expectedLabel} on '(())':\n{output}");
        Assert.That(lines.All(line => line.Contains("SUCC")), Is.True,
            $"Every cyclic-rule trace line for a balanced input should be SUCC:\n{output}");
    }
}
