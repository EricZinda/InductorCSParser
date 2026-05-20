# Cancellation aborted inside a lookahead Peek reports ErrorCharIndex 0 instead of the deepest progress

`BudgetTests.cs` has three sibling tests that share the same shape: a left-recursive `Or` wrapped in `Peek(...)` parsed against 1000 'a's, with one of the three budget knobs (RuleCountLimit, Timeout, Cancellation) set to abort the parse in flight. All three expect `ErrorCharIndex == 146` (the deepest position the recursion reached before the abort). RuleCountLimit and Timeout pass. Cancellation fails: actual `ErrorCharIndex == 0`.

The bug reproduces on a clean checkout of the `StateMachine` branch (verified by stashing all in-flight changes, rebuilding, and running the test in isolation). No Unicode or grammar changes are involved.

## Where the symptom comes from

`Lexer.CheckPeriodicBudgets()` ([src/InductorParser/Lexing/Lexer.cs](../src/InductorParser/Lexing/Lexer.cs#L743)) runs every 1024 rule invocations and tests the three budget slots in order: rule-count, timeout, cancellation. When any of the three trips, `ThrowBudgetExceeded` ([src/InductorParser/Lexing/Lexer.cs](../src/InductorParser/Lexing/Lexer.cs#L767)) freezes `Math.Max(DeepestFailure, _position)` onto the exception so the `Probe.Dispose` unwind path can't collapse it back to a shallow value.

The first periodic check runs at `_ruleInvocations == 0`, before any rules have actually been entered (`0 & 1023 == 0`). The Cancellation test pre-cancels its token before calling `Parse`, so the very first check sees `IsCanceled == true` and throws at invocation zero. At that instant `DeepestFailure` and `_position` are both still zero, so the frozen "deepest at abort" reads as 0 and the test's `ErrorCharIndex == 146` assertion fails.

The RuleCountLimit and Timeout siblings sidestep this by construction: `RuleCountLimit == 100` requires `_ruleInvocations > 100` before tripping, so the first check at invocation 0 is a no-op and the next check fires at invocation 1024 after the recursion has reached depth 146. `Timeout == 1 tick` needs the stopwatch to advance, which only happens once at least one rule has run.

So the three sibling tests don't really exercise the same code path. Pre-cancelling the token short-circuits the periodic check immediately, and the test's expectation that Cancellation behaves like Timeout / RuleCountLimit is built on an asymmetry the implementation doesn't enforce.

## Verify the Bug (already failing)

The failing test is `BudgetTests.Aborted_result_keeps_deepest_progress_when_Cancellation_trips_inside_a_lookahead_probe` at [src/InductorParser.Tests/Core/BudgetTests.cs:465](../src/InductorParser.Tests/Core/BudgetTests.cs#L465). It already exists, fails today on `StateMachine`, and runs in 15ms.

```
dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj \
    --filter "FullyQualifiedName~Aborted_result_keeps_deepest_progress_when_Cancellation_trips_inside_a_lookahead_probe" \
    --no-build
```

Today's output:
```
  Assert.That(result.ErrorCharIndex, Is.EqualTo(146))
  Expected: 146
  But was:  0
```

## Fix (pick one of two)

There are two reasonable directions. The right one depends on which behavior the suite wants to lock in. Either is a one-file change:

1. **Make the test accurate about pre-cancellation.** Pre-cancelled tokens trip at invocation 0, so the deepest progress really is 0. Either change the assertion to `Is.EqualTo(0)` for the pre-cancelled case, or rework the test to cancel mid-parse the way the sibling tests delay their trips. The cleanest mid-parse trip is to wire a `LateBoundRule` whose side effect calls `cancellation.Cancel()` once a chosen depth is reached, then run the recursion against that. RuleCountLimit's "100 invocations then abort" shape is the same idea in a different knob.

2. **Defer the first periodic check.** Skip the budget probe at invocation 0 (treat the periodic schedule as 1024, 2048, ... rather than 0, 1024, 2048, ...). Then a pre-cancelled token would behave like RuleCountLimit / Timeout: the first 1024 invocations run and reach depth 146, then the check fires. That keeps the three tests genuinely parallel. The change is one bitwise edit in the `EnterRule` periodic-check call at [src/InductorParser/Lexing/Lexer.cs:680](../src/InductorParser/Lexing/Lexer.cs#L680) (and the two siblings at 715 and 735). Trade-off: every parse pays one extra rule invocation before the first budget probe, which is invisible on the hot path but is a semantics change the docs ought to mention.

Option 1 is the lower-risk choice: it preserves "budget abort fires as early as possible" and only tightens what the test claims. Option 2 is the more interesting choice: it makes pre-cancellation behave like the other two budget aborts, which is what the test name implies.

## Verify the Fix

Re-run:
```
dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj --no-build
```

Expected: `Aborted_result_keeps_deepest_progress_when_Cancellation_trips_inside_a_lookahead_probe` passes, and the two sibling budget-abort tests (RuleCountLimit, Timeout) still pass with `ErrorCharIndex == 146`.
