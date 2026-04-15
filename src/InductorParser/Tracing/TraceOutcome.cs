namespace InductorParser.Tracing;

// Which marker the trace formatter appends to the label, if any. Rules
// emit Success when they matched, Failure when they didn't. Info is for
// non-outcome lines like Lexer.Read or the deepest-failure announcement,
// where "(SUCC)" or "(FAIL)" would misrepresent what the line is saying.
internal enum TraceOutcome
{
    Info,
    Success,
    Failure,
}
