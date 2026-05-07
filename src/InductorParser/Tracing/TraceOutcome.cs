namespace InductorParser.Tracing;

// Which marker the trace formatter appends to the label, if any. Rules
// emit Success when they matched, Failure when they didn't. Info is for
// non-outcome lines like Lexer.Read or the deepest-failure announcement,
// where "(SUCC)" or "(FAIL)" would misrepresent what the line is saying.
// Skipped is for the Or / BetweenInclusive lookahead shortcut: the
// rule's TryParse never ran because the shortcut proved it couldn't
// match this peek, so a "SKIP" line stands in so a trace reader can see
// why the alternative didn't appear in the trace.
internal enum TraceOutcome
{
    Info,
    Success,
    Failure,
    Skipped,
}
