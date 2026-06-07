namespace InductorParser;

/// <summary>
/// External cancellation signal for a running parse. Construct one, hand it to
/// <see cref="ParseOptions.Cancellation"/>, and call <see cref="Cancel"/> from
/// wherever the cancel decision is made (a UI button, an upstream request
/// handler, a test). On the next periodic budget check after Cancel() fires,
/// the parse aborts with <see cref="ParseOutcome.Canceled"/>.
/// </summary>
/// <remarks>
/// This is a custom type instead of System.Threading.CancellationToken
/// because CancellationToken's <c>.CancelAfter(timespan)</c> shortcut
/// schedules the cancel through System.Threading.Timer, which silently does
/// nothing on WebGL where there's no background thread to fire the timer
/// callback. For a wall-clock deadline, use <see cref="ParseOptions.Timeout"/>
/// instead, which polls a Stopwatch synchronously and works on every target.
/// <para>
/// Bridging from an existing CancellationToken (for example, an ASP.NET
/// request token) is one line:
/// <code>
///     var cancellation = new ParseCancellation();
///     upstreamToken.Register(() => cancellation.Cancel());
///     parser.Parse(input, new ParseOptions { Cancellation = cancellation });
/// </code>
/// </para>
/// </remarks>
public sealed class ParseCancellation
{
    // volatile so the JIT can't read the flag once into a register and keep
    // reusing that stale value across polls. Each poll re-reads it from
    // memory, so a Cancel() written by another thread is observed on the next
    // periodic budget check instead of possibly being missed. On
    // single-threaded hosts (WebGL) volatile is a no-op and costs nothing.
    private volatile bool _canceled;

    /// <summary>True once <see cref="Cancel"/> has been called.</summary>
    public bool IsCanceled => _canceled;

    /// <summary>
    /// Signals the running parse to abort at its next periodic budget check.
    /// </summary>
    public void Cancel() => _canceled = true;
}
