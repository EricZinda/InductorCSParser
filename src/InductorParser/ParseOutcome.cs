namespace InductorParser;

/// <summary>
/// Whether a parse ran and how it ended. Accessed from
/// <see cref="ParseResult.Outcome">ParseResult.Outcome</see>.
/// </summary>
public enum ParseOutcome
{
    /// <summary>
    /// No parse result has been assigned. This is the outcome of
    /// <c>default(ParseResult)</c>. <see cref="Rule.Parse(string)">Rule.Parse(string)</see>
    /// never returns it.
    /// </summary>
    NotRun = 0,

    /// <summary>
    /// The grammar matched the input (and consumed all of it, unless
    /// <see cref="ParseOptions.AllowTrailingInput">ParseOptions.AllowTrailingInput</see> was set).
    /// </summary>
    Success,

    /// <summary>
    /// The input didn't match the grammar. The "normal" failure case: no
    /// budget tripped, no cancellation, the grammar just rejected the input.
    /// </summary>
    /// <remarks>
    /// <see cref="ParseResult.ErrorMessage">ParseResult.ErrorMessage</see> describes the deepest failure.
    /// <see cref="ParseResult.ErrorPosition">ParseResult.ErrorPosition</see> gives its location, also available through
    /// <see cref="ParseResult.ErrorCharIndex">ParseResult.ErrorCharIndex</see>,
    /// <see cref="ParseResult.ErrorTokenIndex">ParseResult.ErrorTokenIndex</see>,
    /// <see cref="ParseResult.ErrorLine">ParseResult.ErrorLine</see>,
    /// <see cref="ParseResult.ErrorCharColumn">ParseResult.ErrorCharColumn</see>, and
    /// <see cref="ParseResult.ErrorTokenColumn">ParseResult.ErrorTokenColumn</see>.
    /// </remarks>
    GrammarMismatch,

    /// <summary>
    /// The input couldn't be normalized to the grammar's
    /// <see cref="Rule.NormalizationForm">Rule.NormalizationForm</see> because it isn't well-formed
    /// Unicode (one that contains an unpaired UTF-16 surrogate, or U+FFFE). The grammar never
    /// ran, so this is distinct from <see cref="GrammarMismatch">ParseOutcome.GrammarMismatch</see>: the input
    /// is broken at the encoding level, not merely unrecognized.
    /// </summary>
    /// <remarks>
    /// <see cref="ParseResult.ErrorPosition">ParseResult.ErrorPosition</see> gives the offending character's location,
    /// also available through <see cref="ParseResult.ErrorCharIndex">ParseResult.ErrorCharIndex</see>,
    /// <see cref="ParseResult.ErrorTokenIndex">ParseResult.ErrorTokenIndex</see>,
    /// <see cref="ParseResult.ErrorLine">ParseResult.ErrorLine</see>,
    /// <see cref="ParseResult.ErrorCharColumn">ParseResult.ErrorCharColumn</see>, and
    /// <see cref="ParseResult.ErrorTokenColumn">ParseResult.ErrorTokenColumn</see>.
    /// <see cref="ParseResult.ErrorMessage">ParseResult.ErrorMessage</see> is the
    /// message rendered from <see cref="ParseOptions.MalformedInputTemplate">ParseOptions.MalformedInputTemplate</see>,
    /// so a non-English app can localize it the same way it localizes every
    /// other failure. Only reachable when the grammar was compiled with a
    /// normalization form (the default). <c><see cref="Rule.Compile(System.Text.NormalizationForm?)">Rule.Compile(null)</see></c> skips
    /// normalization and surfaces ill-formed code units as ordinary tokens
    /// instead. The grammar can accept or reject those tokens. Rejecting them produces
    /// <see cref="GrammarMismatch">ParseOutcome.GrammarMismatch</see>.
    /// </remarks>
    MalformedInput,

    /// <summary>
    /// <see cref="ParseOptions.Timeout">ParseOptions.Timeout</see> elapsed before the parse finished.
    /// Wall-clock, polled from inside the parse loop.
    /// </summary>
    Timeout,

    /// <summary>
    /// <see cref="ParseOptions.RuleCountLimit">ParseOptions.RuleCountLimit</see> was reached. It counts how
    /// many times rules were invoked, not wall-clock time, so the same input
    /// against the same grammar trips at the same point on every run
    /// regardless of how fast the hardware is.
    /// </summary>
    RuleCountLimitExceeded,

    /// <summary>
    /// <see cref="ParseOptions.MaxDepth">ParseOptions.MaxDepth</see> was reached. Catches deeply nested
    /// but well-formed input (think 10,000-deep nested parens) before it can
    /// blow the call stack.
    /// </summary>
    DepthLimitExceeded,

    /// <summary>
    /// <see cref="ParseOptions.Cancellation">ParseOptions.Cancellation</see> was triggered before the parse
    /// finished.
    /// </summary>
    Canceled,
}
