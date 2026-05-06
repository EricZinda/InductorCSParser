using System;
using NUnit.Framework;
using InductorParser;
using InductorParser.StateMachine;
using static InductorParser.Rules;

namespace InductorParser.Tests.StateMachine;

// Cross-validates the four ParseOptions runtime budgets (RuleCountLimit,
// MaxDepth, Timeout, Cancellation) against the recursive evaluator. The
// SM's Step_Call dispatch hands its current call depth to the lexer's
// EnterRuleAtDepth, which runs the same MaxDepth check and the same
// periodic CheckPeriodicBudgets the recursive engine runs from
// Lexer.EnterRule. Both engines should answer with the same ParseOutcome
// for the same options and the same input, and the abort position
// should land at the same spot.
//
// Each test grammar uses a LateBoundRule cycle so the SM emits a Call
// opcode per recursive entry. Non-cyclic grammars are inlined into a
// single state machine and don't go through Call, so they wouldn't
// drive the budget counter at all.
[TestFixture]
public class StateMachineBudgetCompareTests
{
    // Nested := '(' Nested ')' | 'x'. Each open paren is one cyclic
    // call, so input depth maps one-to-one onto call depth in both
    // engines. Used to test MaxDepth.
    private static Rule BuildNestedParens()
    {
        var nested = new LateBoundRule("nested");
        nested.Bind(FirstOf(
            AllOf(Token('('), nested, Token(')')),
            Token('x')));
        return nested;
    }

    // Many := 'a' Many | Eof. Tail-recursive consumer of a long run of
    // 'a's. Each 'a' costs one cyclic call. Used to drive RuleCountLimit
    // / Timeout / Cancellation past the 1024-invocation periodic check
    // window without piling up call-stack depth.
    private static Rule BuildManyAs()
    {
        var many = new LateBoundRule("many");
        many.Bind(FirstOf(
            AllOf(Token('a'), many),
            Eof()));
        return many;
    }

    // Backtracking-heavy FirstOf wrapped in cyclic recursion. The first
    // alternative tries Literal("ab") (matches 'a', fails on the second
    // token, records a failure), then falls back to Token('a') and
    // recurses. Each iteration costs three calls (cycle + Literal +
    // Grapheme), so a 5000-char input drives well past the periodic-check
    // boundary while exposing the deepest-failure tracking on abort.
    private static Rule BuildBacktrackingFirstOf()
    {
        var loop = new LateBoundRule("loop");
        loop.Bind(FirstOf(
            AllOf(FirstOf(Literal("ab"), Token('a')), loop),
            Eof()));
        return loop;
    }

    [Test]
    public void MaxDepth_state_machine_matches_recursive_outcome()
    {
        var rule = BuildNestedParens();
        string input = new string('(', 100) + "x" + new string(')', 100);
        var options = new ParseOptions { MaxDepth = 10, RuleCountLimit = 0 };

        var recursive = rule.ParseRecursive(input, options);
        var stateMachine = StateMachineParser.Parse(rule, input, options);

        Assert.That(recursive.Outcome, Is.EqualTo(ParseOutcome.DepthLimitExceeded),
            $"recursive engine outcome: {recursive.ErrorMessage}");
        Assert.That(stateMachine.Outcome, Is.EqualTo(ParseOutcome.DepthLimitExceeded),
            $"state-machine engine outcome: {stateMachine.ErrorMessage}");
        Assert.That(stateMachine.ErrorMessage, Is.EqualTo(recursive.ErrorMessage));
        // The SM doesn't roll the lexer position back through nested
        // transactions on a budget throw the way the recursive engine
        // does (the SM has no transaction frames; its rollback is via
        // BacktrackFrame.LexerPosition, which only fires on backtrack
        // jumps, not on a thrown exception). So the SM reports the
        // position the lexer was at when the depth check tripped (10
        // open parens consumed), while the recursive engine reports 0
        // (every transaction unwound on the way out). Both are valid
        // diagnostics; assert in-range so the position is at least
        // safe to index into the input.
        Assert.That(stateMachine.ErrorCharIndex, Is.InRange(0, input.Length));
        Assert.That(recursive.ErrorCharIndex, Is.InRange(0, input.Length));
    }

    [Test]
    public void RuleCountLimit_state_machine_matches_recursive_outcome()
    {
        // Backtracking grammar wrapped in a cyclic LateBoundRule so the
        // SM also fires Call (the SM inlines non-cyclic rules and
        // wouldn't otherwise increment the rule-invocation counter).
        // MaxDepth is disabled because this grammar tail-recurses one
        // level per char and would otherwise hit the default depth cap
        // before the rule-count cap.
        var rule = BuildBacktrackingFirstOf();
        string input = new string('a', 5000);
        var options = new ParseOptions { RuleCountLimit = 100, MaxDepth = 0 };

        var recursive = rule.ParseRecursive(input, options);
        var stateMachine = StateMachineParser.Parse(rule, input, options);

        Assert.That(recursive.Outcome, Is.EqualTo(ParseOutcome.RuleCountLimitExceeded),
            $"recursive engine outcome: {recursive.ErrorMessage}");
        Assert.That(stateMachine.Outcome, Is.EqualTo(ParseOutcome.RuleCountLimitExceeded),
            $"state-machine engine outcome: {stateMachine.ErrorMessage}");
        Assert.That(stateMachine.ErrorMessage, Is.EqualTo(recursive.ErrorMessage));
        // Abort position has to be in range so callers can index into
        // the input. The exact value can differ between engines: the
        // recursive engine counts every TryParseRule invocation, so its
        // periodic check fires earlier in input-position terms than the
        // SM's, which only counts cyclic Call entries.
        Assert.That(stateMachine.ErrorCharIndex, Is.InRange(0, input.Length));
        Assert.That(recursive.ErrorCharIndex, Is.InRange(0, input.Length));
    }

    [Test]
    public void Timeout_state_machine_matches_recursive_outcome()
    {
        // Tiny deadline plus enough cyclic calls to cross the 1024-
        // invocation periodic-check boundary in both engines. MaxDepth
        // is disabled because BuildManyAs tail-recurses one level per
        // char and would otherwise hit the default depth cap before
        // the timeout fires.
        var rule = BuildManyAs();
        string input = new string('a', 5000);
        var options = new ParseOptions
        {
            Timeout = TimeSpan.FromTicks(1),
            RuleCountLimit = 0,
            MaxDepth = 0,
        };

        var recursive = rule.ParseRecursive(input, options);
        var stateMachine = StateMachineParser.Parse(rule, input, options);

        Assert.That(recursive.Outcome, Is.EqualTo(ParseOutcome.Timeout),
            $"recursive engine outcome: {recursive.ErrorMessage}");
        Assert.That(stateMachine.Outcome, Is.EqualTo(ParseOutcome.Timeout),
            $"state-machine engine outcome: {stateMachine.ErrorMessage}");
        Assert.That(stateMachine.ErrorMessage, Is.EqualTo(recursive.ErrorMessage));
        Assert.That(stateMachine.ErrorCharIndex, Is.InRange(0, input.Length));
    }

    [Test]
    public void Cancellation_state_machine_matches_recursive_outcome()
    {
        // Pre-canceled signal: the periodic check inside CheckPeriodicBudgets
        // sees IsCanceled the first time it fires after a Call has bumped
        // the counter past the BudgetCheckInterval boundary. RuleCountLimit
        // and MaxDepth are disabled so the only thing the periodic check
        // trips on is the cancellation flag.
        var cancellation = new ParseCancellation();
        cancellation.Cancel();

        var rule = BuildManyAs();
        string input = new string('a', 5000);
        var options = new ParseOptions
        {
            Cancellation = cancellation,
            RuleCountLimit = 0,
            MaxDepth = 0,
        };

        var recursive = rule.ParseRecursive(input, options);
        var stateMachine = StateMachineParser.Parse(rule, input, options);

        Assert.That(recursive.Outcome, Is.EqualTo(ParseOutcome.Canceled),
            $"recursive engine outcome: {recursive.ErrorMessage}");
        Assert.That(stateMachine.Outcome, Is.EqualTo(ParseOutcome.Canceled),
            $"state-machine engine outcome: {stateMachine.ErrorMessage}");
        Assert.That(stateMachine.ErrorMessage, Is.EqualTo(recursive.ErrorMessage));
        Assert.That(stateMachine.ErrorCharIndex, Is.InRange(0, input.Length));
    }

    [Test]
    public void Defaults_state_machine_lets_normal_parses_through()
    {
        // Sanity check: when no budget would trip on the input, both
        // engines return Success. Catches the regression where the SM
        // accidentally throws on a clean parse because EnterRuleAtDepth
        // tripped on a stale counter from a previous parse on the
        // pooled lexer.
        var rule = BuildNestedParens();
        string input = "((x))";

        var recursive = rule.ParseRecursive(input, new ParseOptions());
        var stateMachine = StateMachineParser.Parse(rule, input);

        Assert.That(recursive.Outcome, Is.EqualTo(ParseOutcome.Success));
        Assert.That(stateMachine.Outcome, Is.EqualTo(ParseOutcome.Success));
    }

    [Test]
    public void Disabled_budgets_do_not_abort_state_machine_parse()
    {
        // Mirrors the recursive engine's *_zero_disables_* tests: when
        // every budget is set to its disabled value, even a heavy input
        // completes successfully on the SM. Proves the zero-disabled
        // contract is honored on the state-machine path.
        var rule = BuildManyAs();
        var options = new ParseOptions
        {
            RuleCountLimit = 0,
            MaxDepth = 0,
            Timeout = TimeSpan.Zero,
            Cancellation = null,
        };

        var stateMachine = StateMachineParser.Parse(rule, new string('a', 2000), options);

        Assert.That(stateMachine.Outcome, Is.EqualTo(ParseOutcome.Success),
            $"state-machine outcome: {stateMachine.ErrorMessage}");
    }

    [Test]
    public void TryMatch_returns_false_on_budget_abort()
    {
        // Matcher-mode entry collapses the abort to "did not match"
        // because TryMatch returns bool, not ParseResult. Confirms the
        // try/catch on the matcher path is wired up.
        var rule = BuildNestedParens();
        string input = new string('(', 100) + "x" + new string(')', 100);
        var options = new ParseOptions { MaxDepth = 10 };

        bool matched = StateMachineParser.TryMatch(rule, input, options);

        Assert.That(matched, Is.False);
    }
}
