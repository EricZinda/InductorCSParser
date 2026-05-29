using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace InductorParser.Lexing;

// Tracks the four parse budgets (rule-count limit, depth limit, wall-clock
// timeout, cancellation) for one parse. Owns the counters that accumulate
// as the parse runs and the limits set by ParseOptions.
//
// The Lexer holds one of these and exposes it as Lexer.Budget. Its
// methods mark rule entry/exit and tick the periodic checks.
//
// Holds a back-reference to its Lexer so it can read Lexer.DeepestFailurePosition
// and Lexer.Position when freezing the deepest position onto the
// ParseBudgetExceeded it throws. The read happens only on the unhappy
// path. The happy-path EnterRule / TickPeriodic never touch the Lexer.
//
// A budget can be a sub-budget: instead of holding its own limits and
// counters, it forwards all bookkeeping to a parent budget (see
// InheritFrom). WithinTokenRule's inner sub-lexer uses one so its
// recursion counts against the outer parse's combined limits.
internal sealed class ParseBudget
{
    // Configure sets these limits and they stay constant for the rest of
    // the parse. On a sub-budget they're left at their defaults (the
    // parent's are used instead).
    private long _ruleCountLimit;
    private int _maxDepth;
    private TimeSpan _timeout;
    private Stopwatch? _stopwatch;
    private ParseCancellation? _cancellation;

    // These counters accumulate as the parse runs. They stay zero on a
    // sub-budget (the parent's are used instead).
    private long _ruleInvocations;
    private int _ruleDepth;

    // When non-null, this budget is a sub-budget delegating to the named
    // parent. Its own counters stay at zero. The parent's are what the
    // limit checks read.
    private ParseBudget? _parent;

    // The Lexer this budget belongs to. ThrowBudgetExceeded reads its
    // position when building the exception, so each budget reports its
    // own lexer's position even when it delegates bookkeeping to a parent.
    private readonly Lexer _lexer;

    // Periodic check fires every BudgetCheckInterval rule invocations.
    // 1024 keeps the per-call cost at one AND + one compare on the hot
    // path while still being often enough that wall-clock and
    // cancellation checks feel responsive. Power-of-two so the modulo
    // collapses to a bitmask.
    internal const int BudgetCheckInterval = 1024;
    internal const int BudgetCheckMask = BudgetCheckInterval - 1;

    public ParseBudget(Lexer lexer)
    {
        _lexer = lexer ?? throw new ArgumentNullException(nameof(lexer));
    }

    // Initialize with the user-supplied limits. Called after Lexer
    // construction (or after ResetForReuse for a pooled Lexer). Drops
    // any prior parent reference: a root parse's budget is its own,
    // not delegated.
    public void Configure(ParseOptions options)
    {
        _ruleCountLimit = options.RuleCountLimit;
        _maxDepth = options.MaxDepth;
        _timeout = options.Timeout;
        _cancellation = options.Cancellation;
        _stopwatch = options.Timeout > TimeSpan.Zero ? Stopwatch.StartNew() : null;
        _parent = null;
    }

    // Sub-budget hookup. After this call, every EnterRule / ExitRule /
    // TickPeriodic on `this` forwards to `parent`. WithinTokenRule
    // calls this so the inner sub-lexer's recursion counts against the
    // outer parse's combined depth / rule-count limit.
    public void InheritFrom(ParseBudget parent)
    {
        _parent = parent ?? throw new ArgumentNullException(nameof(parent));
    }

    // Clear all per-parse state. Called by Lexer.ResetForReuse so a
    // pooled Lexer can't leak counters / limits / cancellation from
    // the previous parse.
    public void Reset()
    {
        _ruleInvocations = 0;
        _ruleDepth = 0;
        _ruleCountLimit = 0;
        _maxDepth = 0;
        _timeout = TimeSpan.Zero;
        _stopwatch = null;
        _cancellation = null;
        _parent = null;
    }

    // Called at the start of each rule invocation by the recursive
    // engine. Increments depth, checks MaxDepth, then increments the
    // invocation counter and runs the periodic checks every 1024
    // calls.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnterRule()
    {
        if (_parent != null)
        {
            _parent.EnterRule();
            return;
        }
        _ruleDepth++;
        if (_maxDepth > 0 && _ruleDepth > _maxDepth)
            ThrowBudgetExceeded(ParseOutcome.DepthLimitExceeded);

        _ruleInvocations++;
        if ((_ruleInvocations & BudgetCheckMask) == 0)
            CheckPeriodicBudgets();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ExitRule()
    {
        if (_parent != null)
        {
            _parent.ExitRule();
            return;
        }
        _ruleDepth--;
    }

    // Depth-passed variant of EnterRule for an alternative evaluator
    // that tracks call depth itself rather than through paired EnterRule
    // / ExitRule. The recursive engine maintains depth in _ruleDepth via
    // EnterRule. 
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnterRuleAtDepth(int depth)
    {
        if (_parent != null)
        {
            _parent.EnterRuleAtDepth(depth);
            return;
        }
        if (_maxDepth > 0 && depth > _maxDepth)
            ThrowBudgetExceeded(ParseOutcome.DepthLimitExceeded);

        _ruleInvocations++;
        if ((_ruleInvocations & BudgetCheckMask) == 0)
            CheckPeriodicBudgets();
    }

    // Counter-only tick for an alternative evaluator, used on steps
    // that do real work but don't enter a cyclic rule (so they don't
    // go through EnterRuleAtDepth). Skips the depth check. An evaluator
    // that calls this must enforce MaxDepth itself on its rule-entry path.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void TickPeriodic()
    {
        if (_parent != null)
        {
            _parent.TickPeriodic();
            return;
        }
        _ruleInvocations++;
        if ((_ruleInvocations & BudgetCheckMask) == 0)
            CheckPeriodicBudgets();
    }

    // Off the hot path on purpose: only invoked once every
    // BudgetCheckInterval rule invocations, so making it a separate
    // non-inlined method keeps EnterRule small enough for the JIT to
    // inline cleanly.
    private void CheckPeriodicBudgets()
    {
        if (_ruleCountLimit > 0 && _ruleInvocations > _ruleCountLimit)
            ThrowBudgetExceeded(ParseOutcome.RuleCountLimitExceeded);
        if (_stopwatch != null && _stopwatch.Elapsed >= _timeout)
            ThrowBudgetExceeded(ParseOutcome.Timeout);
        if (_cancellation != null && _cancellation.IsCanceled)
            ThrowBudgetExceeded(ParseOutcome.Canceled);
    }

    // Throw a budget abort with the deepest-failure position frozen at
    // throw time. Without this freeze, an active lookahead Probe's
    // Dispose would run on the exception path and restore the failure
    // tracker to its pre-probe value, collapsing the catch handler's
    // "how far did the parse get" reading to a shallow position. By
    // computing the position here, before the throw, and stashing it on
    // the exception, the catch handler in Rule.ParseRecursive gets a
    // value that survives the unwind regardless of how many probes were
    // open.
    //
    // NoInlining factors the throw out of EnterRule / EnterRuleAtDepth.
    // A throw expands to a fair amount of IL, and the JIT weighs caller
    // size when deciding what to inline, so keeping it in its own method
    // leaves the hot callers small enough to inline. The throw fires at
    // most once per parse, so the extra call costs nothing. Same pattern
    // as the BCL's ThrowHelper methods.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowBudgetExceeded(ParseOutcome outcome)
    {
        int deepestAtAbort = Math.Max(_lexer.DeepestFailurePosition, _lexer.Position);
        throw new ParseBudgetExceeded(outcome, deepestAtAbort);
    }
}
