namespace InductorParser;

/// <summary>
/// Why a parse ended: success, a normal grammar mismatch, or one of the
/// budget / cancellation aborts. Carried on <see cref="ParseResult.Outcome"/>.
/// </summary>
public enum ParseOutcome
{
    /// <summary>
    /// The grammar matched the input (and consumed all of it, unless
    /// <see cref="ParseOptions.AllowTrailingInput"/> was set).
    /// </summary>
    Success,

    /// <summary>
    /// The input didn't match the grammar. The "normal" failure case: no
    /// budget tripped, no cancellation, the grammar just rejected the input.
    /// </summary>
    /// <remarks>
    /// <see cref="ParseResult.ErrorMessage"/> and
    /// <see cref="ParseResult.ErrorCharIndex"/> carry the deepest-failure
    /// details.
    /// </remarks>
    GrammarMismatch,

    /// <summary>
    /// <see cref="ParseOptions.Timeout"/> elapsed before the parse finished.
    /// Wall-clock, polled from inside the parse loop.
    /// </summary>
    Timeout,

    /// <summary>
    /// <see cref="ParseOptions.RuleCountLimit"/> was reached. It counts how
    /// many times rules were invoked, not wall-clock time, so the same input
    /// against the same grammar trips at the same point on every run
    /// regardless of how fast the hardware is.
    /// </summary>
    RuleCountLimitExceeded,

    /// <summary>
    /// <see cref="ParseOptions.MaxDepth"/> was reached. Catches deeply nested
    /// but well-formed input (think 10,000-deep nested parens) before it can
    /// blow the call stack.
    /// </summary>
    DepthLimitExceeded,

    /// <summary>
    /// <see cref="ParseOptions.Cancellation"/> was triggered before the parse
    /// finished. Polled alongside the timeout from inside the parse loop.
    /// </summary>
    Canceled,
}
