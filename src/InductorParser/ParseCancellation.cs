namespace InductorParser;

// External cancellation signal for a running parse. The caller constructs
// one, hands it to ParseOptions, holds onto its reference, and calls
// Cancel() from wherever the cancel decision is made (a UI button, an
// upstream request handler, a test). The parser polls IsCanceled inside
// its periodic budget check. On the next check after Cancel() fires, the
// parse aborts with ParseOutcome.Canceled.
//
// This is a custom type instead of the standard System.Threading.CancellationToken because
// CancellationToken supports a .CancelAfter(timespan) shortcut on its
// source that schedules the cancel through System.Threading.Timer, which
// silently does nothing on WebGL because there's no background thread to
// fire the timer callback. Code that compiles, passes desktop tests, and
// looks correct in review then ships and never times out in production.
// This type has no time-based API at all (only manual Cancel()), so it
// bypasses the bug. For a wall-clock deadline, use
// ParseOptions.Timeout instead, which uses synchronous Stopwatch polling
// and works on every target.
//
// Bridging from an existing CancellationToken (for example, an ASP.NET
// request token) is one line:
//
//     var cancellation = new ParseCancellation();
//     upstreamToken.Register(() => cancellation.Cancel());
//     parser.Parse(input, new ParseOptions { Cancellation = cancellation });
//
// Reference type so a caller can hold the same instance the lexer is
// polling and flip it from anywhere. The flag is `volatile` so a Cancel()
// call from another thread becomes visible on the parsing thread's next
// poll without a memory barrier on the read side. On single-threaded
// hosts (WebGL) volatile is a no-op and costs nothing.
public sealed class ParseCancellation
{
    private volatile bool _canceled;

    public bool IsCanceled => _canceled;

    public void Cancel() => _canceled = true;
}
