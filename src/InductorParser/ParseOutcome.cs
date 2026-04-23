namespace InductorParser;

public enum ParseOutcome
{
    Success,

    // The input didn't match the grammar. This is the "normal" failure
    // case: no budget tripped, no cancellation, the grammar just
    // rejected the input. ParseResult.ErrorMessage and ErrorCharIndex
    // carry the deepest-failure details.
    GrammarMismatch,

    // ParseOptions.Timeout elapsed before the parse finished. Wall-clock,
    // polled from inside the parse loop.
    Timeout,

    // ParseOptions.RuleCountLimit was reached. RuleCountLimit counts
    // how many times rules were invoked during the parse, not
    // wall-clock time, so the same input against the same grammar trips
    // at exactly the same point on every run regardless of how fast
    // the hardware is.
    RuleCountLimitExceeded,

    // ParseOptions.MaxDepth was reached. Catches deeply nested but
    // well-formed inputs (think 10,000-deep nested parens) before they
    // can blow the call stack.
    DepthLimitExceeded,

    // ParseOptions.Cancellation was triggered before the parse finished.
    // Polled alongside the timeout from inside the parse loop.
    Canceled,
}
