using System;
using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Runtime defenses against catastrophic backtracking and stack-overflow on
// malicious or pathological input. The library exposes four orthogonal
// abort budgets through ParseOptions; this fixture pins the trip behavior
// of each one and confirms they don't get in the way of normal parses.
[TestFixture]
public class BudgetTests
{
    [Test]
    public void Defaults_let_normal_parses_succeed()
    {
        // No options set: the protective defaults (10M invocations,
        // depth 1000) are in force but neither trips on a plain
        // well-formed parse.
        var rule = OneOrMore(RuneIn(RuneSet.Letters));
        var result = rule.Parse("hello");

        Assert.That(result.Outcome, Is.EqualTo(ParseOutcome.Success));
        Assert.That(result.Tree!.ToString(), Is.EqualTo("hello"));
    }

    [Test]
    public void MaxRuleInvocations_aborts_with_WorkLimitExceeded()
    {
        // OneOrMore(RuneIn) on a long input does roughly two rule
        // invocations per character (OneOrMore once at the outer level
        // plus one RuneIn per inner iteration). The actual trip happens
        // on a periodic budget check, so the count when we abort is
        // somewhere past the configured limit, which is fine: the test
        // is asserting the outcome, not the exact trip point.
        var rule = OneOrMore(RuneIn(RuneSet.Letters));
        var options = new ParseOptions { MaxRuleInvocations = 10 };
        var result = rule.Parse(new string('a', 5000), options);

        Assert.That(result.Outcome, Is.EqualTo(ParseOutcome.WorkLimitExceeded));
        Assert.That(result.ErrorMessage,
            Is.EqualTo("Parse aborted: maximum rule invocations exceeded."));
    }

    [Test]
    public void MaxRuleInvocations_zero_disables_the_work_budget()
    {
        // Same 5000-char workload that trips MaxRuleInvocations = 10 in
        // the test above. With the limit set to 0, the periodic check
        // skips the work-budget comparison entirely and the parse
        // completes. Proves 0 is a real off switch, not just a value
        // small workloads happen to fit under.
        var rule = OneOrMore(RuneIn(RuneSet.Letters));
        var options = new ParseOptions { MaxRuleInvocations = 0 };
        var result = rule.Parse(new string('a', 5000), options);

        Assert.That(result.Outcome, Is.EqualTo(ParseOutcome.Success));
    }

    [Test]
    public void MaxDepth_aborts_with_DepthLimitExceeded_on_recursive_grammar()
    {
        // Recursive grammar via LateBoundRule. Each recursion pushes
        // several rule frames (LateBound forward + Or + And + Char), so
        // a moderate input quickly outgrows a small MaxDepth. Intent
        // mirrors a real "((((...))))" deeply-nested input that would
        // blow the .NET call stack without protection.
        var aRule = new LateBoundRule("aRule");
        aRule.Bind(Or(And(Char('a'), aRule), Char('a')));

        var options = new ParseOptions { MaxDepth = 10 };
        var result = aRule.Parse(new string('a', 100), options);

        Assert.That(result.Outcome, Is.EqualTo(ParseOutcome.DepthLimitExceeded));
        Assert.That(result.ErrorMessage,
            Is.EqualTo("Parse aborted: maximum recursion depth exceeded."));
    }

    [Test]
    public void MaxDepth_zero_disables_the_depth_budget()
    {
        // Same recursive grammar; with MaxDepth disabled the parse
        // completes (the input is short enough not to overflow the real
        // call stack).
        var aRule = new LateBoundRule("aRule");
        aRule.Bind(Or(And(Char('a'), aRule), Char('a')));

        var options = new ParseOptions { MaxDepth = 0, MaxRuleInvocations = 0 };
        var result = aRule.Parse(new string('a', 50), options);

        Assert.That(result.Outcome, Is.EqualTo(ParseOutcome.Success));
    }

    [Test]
    public void Timeout_aborts_with_Timeout_outcome()
    {
        // Tiny timeout plus enough invocations that at least one
        // periodic budget check fires after the deadline. The check
        // interval is 1024, so the input has to be long enough to reach
        // the first periodic check.
        var rule = OneOrMore(RuneIn(RuneSet.Letters));
        var options = new ParseOptions
        {
            Timeout = TimeSpan.FromTicks(1),
            MaxRuleInvocations = 0,
        };
        var result = rule.Parse(new string('a', 5000), options);

        Assert.That(result.Outcome, Is.EqualTo(ParseOutcome.Timeout));
        Assert.That(result.ErrorMessage,
            Is.EqualTo("Parse aborted: timeout exceeded."));
    }

    [Test]
    public void Zero_Timeout_disables_the_timeout()
    {
        // Same 5000-char workload that trips Timeout = 1 tick in the
        // test above. Setting Timeout to Zero means "no deadline,"
        // matching MaxRuleInvocations = 0 and MaxDepth = 0. The
        // Stopwatch isn't even allocated.
        var rule = OneOrMore(RuneIn(RuneSet.Letters));
        var options = new ParseOptions
        {
            Timeout = TimeSpan.Zero,
            MaxRuleInvocations = 0,
        };
        var result = rule.Parse(new string('a', 5000), options);

        Assert.That(result.Outcome, Is.EqualTo(ParseOutcome.Success));
    }

    [Test]
    public void Cancellation_already_canceled_aborts_with_Canceled()
    {
        // Pre-canceled signal, parse long enough to hit a periodic check.
        // Confirms ParseCancellation.IsCanceled is polled inside the parse
        // loop the same way the timeout is.
        var cancellation = new ParseCancellation();
        cancellation.Cancel();

        var rule = OneOrMore(RuneIn(RuneSet.Letters));
        var options = new ParseOptions
        {
            Cancellation = cancellation,
            MaxRuleInvocations = 0,
        };
        var result = rule.Parse(new string('a', 5000), options);

        Assert.That(result.Outcome, Is.EqualTo(ParseOutcome.Canceled));
        Assert.That(result.ErrorMessage,
            Is.EqualTo("Parse aborted: cancellation requested."));
    }

    [Test]
    public void Null_Cancellation_does_not_abort_normal_parse()
    {
        // The default Cancellation (null) must not look canceled to the
        // periodic check. Pinned because if we ever forget the null guard,
        // every parse would NRE on the IsCanceled poll.
        var rule = OneOrMore(RuneIn(RuneSet.Letters));
        var options = new ParseOptions { Cancellation = null };
        var result = rule.Parse(new string('a', 5000), options);

        Assert.That(result.Outcome, Is.EqualTo(ParseOutcome.Success));
    }

    [Test]
    public void Uncanceled_ParseCancellation_does_not_abort_normal_parse()
    {
        // A live but never-canceled ParseCancellation must let the parse
        // run to completion. Separates "the caller passed a cancellation
        // signal" from "the caller wants to cancel."
        var cancellation = new ParseCancellation();

        var rule = OneOrMore(RuneIn(RuneSet.Letters));
        var options = new ParseOptions { Cancellation = cancellation };
        var result = rule.Parse(new string('a', 5000), options);

        Assert.That(result.Outcome, Is.EqualTo(ParseOutcome.Success));
    }

    [Test]
    public void Pathological_nested_repetition_aborts_under_default_work_limit()
    {
        // The doc-cited shape: OneOrMore(OneOrMore(A)) with A able to
        // match in multiple ways. Greedy PEG semantics mean this doesn't
        // produce true catastrophic backtracking the way a regex would,
        // but the broader contract holds: any grammar/input combination
        // that runs the rule machinery past the configured budget aborts
        // cleanly with WorkLimitExceeded instead of hanging. 
        var rule = OneOrMore(OneOrMore(RuneIn(RuneSet.Letters)));
        var options = new ParseOptions { MaxRuleInvocations = 5_000 };
        var result = rule.Parse(new string('a', 100_000), options);

        Assert.That(result.Outcome, Is.EqualTo(ParseOutcome.WorkLimitExceeded));
    }

    [Test]
    public void Aborted_result_carries_a_position_for_diagnostics()
    {
        // ErrorCharIndex on a budget abort carries the lexer position at
        // unwind, which gives callers a coarse "how far did the parser
        // get" signal. We don't assert an exact value (it depends on the
        // periodic-check trip point) but we do confirm it's in range so
        // a caller can use it without bounds-checking against the input.
        var rule = OneOrMore(RuneIn(RuneSet.Letters));
        var input = new string('a', 5000);
        var options = new ParseOptions { MaxRuleInvocations = 10 };
        var result = rule.Parse(input, options);

        Assert.That(result.Outcome, Is.EqualTo(ParseOutcome.WorkLimitExceeded));
        Assert.That(result.ErrorCharIndex, Is.InRange(0, input.Length));
    }
}
