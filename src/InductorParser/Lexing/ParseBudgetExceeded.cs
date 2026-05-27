using System;

namespace InductorParser.Lexing;

// Exception thrown from the periodic budget check on
// the lexer when RuleCountLimit, MaxDepth, the Timeout, or the
// Cancellation trips. The throw unwinds through the rule stack,
// rolling back every active lexer transaction via the existing `using`
// scaffolding, and lands at the catch in Rule.Parse, which converts it
// to a failed ParseResult with the matching outcome.
//
// One throw per pathological parse, not one per rule invocation, so the
// IL2CPP exception cost is irrelevant. The design avoids exception
// filters (`catch ... when (...)`) which IL2CPP doesn't support.
internal sealed class ParseBudgetExceeded : Exception
{
    public ParseOutcome Outcome { get; }

    // The "how far did the parser get" position, captured at throw time
    // by Lexer.ThrowBudgetExceeded so it survives the unwind of any
    // active lookahead Probe. The Probe's Dispose runs on the exception
    // path and restores the failure tracker to its pre-probe value, so
    // the catch handler in Rule.ParseRecursive can't reliably read
    // lexer.DeepestFailurePosition after the unwind. Capturing the value at
    // throw time freezes it before any restoration runs.
    public int DeepestPositionAtAbort { get; }

    public ParseBudgetExceeded(ParseOutcome outcome, int deepestPositionAtAbort)
        : base($"Parse aborted: {outcome}.")
    {
        Outcome = outcome;
        DeepestPositionAtAbort = deepestPositionAtAbort;
    }
}
