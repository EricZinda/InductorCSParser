namespace InductorParser;

public enum ParseOutcome
{
    Success,
    GrammarMismatch,

    // ParseOptions.Timeout elapsed before the parse finished. Wall-clock,
    // polled from inside the parse loop.
    Timeout,

    // ParseOptions.MaxRuleInvocations was reached. Deterministic work
    // limit. The same input against the same grammar always trips at the
    // same point.
    WorkLimitExceeded,

    // ParseOptions.MaxDepth was reached. Catches deeply nested but
    // well-formed inputs (think 10,000-deep nested parens) before they
    // can blow the call stack.
    DepthLimitExceeded,

    // ParseOptions.Cancellation was triggered before the parse finished.
    // Polled alongside the timeout from inside the parse loop.
    Canceled,
}
