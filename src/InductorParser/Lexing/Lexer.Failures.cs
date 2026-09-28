using System;
using System.Runtime.CompilerServices;
using InductorParser.Tracing;

namespace InductorParser.Lexing;

public sealed partial class Lexer
{
    // Three-slot failure tracker (mechanical / named / forced). The
    // depth-primary resolution rules and per-slot semantics live in
    // docs/ErrorArchitecture.md.
    private struct FailureStateSnapshot
    {
        public int MechanicalPosition;
        public int NamedPosition;
        public string? NamedMessage;
        public int ForcedPosition;
        public string? ForcedMessage;
    }

    // Rules don't touch _failureState directly: they go
    // through RecordFailure, RecordCompositeFailure and BeginProbe.
    private FailureStateSnapshot _failureState;

    // High-water mark of the deepest input position any RecordFailure call
    // has reported within the current transaction's window. Every
    // BeginTransaction saves and zeroes this, and the matching Dispose
    // merges it back into the enclosing window with Math.Max. A composite
    // rule's RecordCompositeFailure reads it once its children have run,
    // so its .WithError anchors at the deepest position its
    // subtree reached rather than at the composite's own start.
    private int _subtreeDeepestFailure;

    /// <summary>
    /// The position of the failure that would be reported if the parse ended now, as a char
    /// index (a UTF-16 code unit offset) into <see cref="Input">Lexer.Input</see>.
    /// </summary>
    /// <remarks>
    /// See <a href="../docs/ErrorArchitecture.md">Error Reporting Architecture</a> for how forced, named, and mechanical
    /// failures are ranked.
    /// </remarks>
    public int DeepestFailurePosition
    {
        get
        {
            if (_failureState.ForcedMessage != null)
                return _failureState.ForcedPosition;
            if (_failureState.NamedMessage != null
                && _failureState.NamedPosition >= _failureState.MechanicalPosition)
                return _failureState.NamedPosition;
            return _failureState.MechanicalPosition;
        }
    }

    /// <summary>
    /// The error message that would be reported if the parse ended now,
    /// or null if the deepest failure has no message.
    /// </summary>
    /// <remarks>
    /// See <a href="../docs/ErrorArchitecture.md">Error Reporting Architecture</a>.
    /// </remarks>
    public string? DeepestFailureMessage
    {
        get
        {
            if (_failureState.ForcedMessage != null)
                return _failureState.ForcedMessage;
            if (_failureState.NamedMessage != null
                && _failureState.NamedPosition >= _failureState.MechanicalPosition)
                return _failureState.NamedMessage;
            return null;
        }
    }

    /// <summary>
    /// True when the failure <see cref="DeepestFailureMessage">Lexer.DeepestFailureMessage</see> would
    /// surface is a forced <see cref="Rule.WithError">Rule.WithError(string, bool)</see> override.
    /// </summary>
    public bool DeepestFailureIsForced => _failureState.ForcedMessage != null;

    /// <summary>
    /// Record that a rule just failed at the given input position.
    /// </summary>
    /// <remarks>
    /// Precedence: forced beats everything, otherwise the deepest position
    /// wins. At the same position named beats mechanical and ties go to
    /// the first writer. See <a href="../docs/ErrorArchitecture.md">Error Reporting Architecture</a>.
    /// </remarks>
    /// <param name="position">The start of the specific read that failed, not the post-read lexer position.</param>
    /// <param name="errorMessage">The named message to attach,
    /// or null for a mechanical failure.</param>
    /// <param name="forced">True for a forced <see cref="Rule.WithError">Rule.WithError(string, bool)</see> override
    /// that beats every non-forced failure regardless of depth.</param>
    public void RecordFailure(int position, string? errorMessage = null, bool forced = false)
    {
        Invariant.That(!forced || errorMessage != null,
            $"RecordFailure(forced: true) requires a non-null errorMessage. Got null at position {position}.");
        // Every failure, whatever its precedence, advances the subtree-extent
        // high-water mark so an enclosing composite can anchor its own
        // .WithError at the deepest position its subtree reached.
        if (position > _subtreeDeepestFailure)
            _subtreeDeepestFailure = position;
        if (forced)
        {
            if (position > _failureState.ForcedPosition)
            {
                // Strictly deeper than any previous forced failure: take
                // the slot and announce the advance.
                _failureState.ForcedPosition = position;
                _failureState.ForcedMessage = errorMessage;
                Trace(TraceLevel.Diagnostic, "Lexer.RecordFailure", TraceOutcome.Info,
                      $"forced deepest failure at char {position}");
            }
            else if (_failureState.ForcedMessage == null)
            {
                // First forced failure. ForcedMessage being null means
                // ForcedPosition is still its int default 0, and the
                // outer condition guarantees position <= 0, so position
                // is also 0. The write to ForcedPosition is a no-op
                // (0 to 0). The meaningful change is filling in the
                // message.
                _failureState.ForcedPosition = position;
                _failureState.ForcedMessage = errorMessage;
                Trace(TraceLevel.Diagnostic, "Lexer.RecordFailure", TraceOutcome.Info,
                      $"first forced failure at char {position}");
            }
            // else: a previous forced failure already sits at this depth
            // or deeper. First writer wins ties, so leave it.
        }
        else if (errorMessage != null)
        {
            if (position > _failureState.NamedPosition)
            {
                _failureState.NamedPosition = position;
                _failureState.NamedMessage = errorMessage;
                Trace(TraceLevel.Diagnostic, "Lexer.RecordFailure", TraceOutcome.Info,
                      $"new deepest failure at char {position}");
            }
            else if (_failureState.NamedMessage == null)
            {
                // First named failure. Same structural reasoning as the
                // forced branch: position == NamedPosition == 0, so the
                // position write is a no-op and only the message is
                // genuinely filled.
                _failureState.NamedPosition = position;
                _failureState.NamedMessage = errorMessage;
                Trace(TraceLevel.Diagnostic, "Lexer.RecordFailure", TraceOutcome.Info,
                      $"first named failure at char {position}");
            }
            // else: a previous named failure already sits at this depth
            // or deeper. First writer wins ties, so leave it.
        }
        else if (position > _failureState.MechanicalPosition)
        {
            _failureState.MechanicalPosition = position;
            Trace(TraceLevel.Diagnostic, "Lexer.RecordFailure", TraceOutcome.Info,
                  $"new deepest failure at char {position}");
        }
    }

    /// <summary>
    /// Record a composite rule's <see cref="InductorParser.Rule.WithError(System.String,System.Boolean)">Rule.WithError</see> failure at whichever is
    /// further into the input: <paramref name="floorPosition"/> or the
    /// deepest position any child of the composite reached.
    /// </summary>
    /// <remarks>
    /// See <a href="../docs/ErrorArchitecture.md">Error Reporting Architecture</a> for why a composite records this way.
    /// </remarks>
    /// <param name="floorPosition">Where the composite would record on its
    /// own, usually its start position or the current lexer position.</param>
    /// <param name="errorMessage">The named or forced message to attach,
    /// or null for a mechanical failure.</param>
    /// <param name="forced">True for a forced <see cref="Rule.WithError">Rule.WithError(string, bool)</see> override.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordCompositeFailure(int floorPosition, string? errorMessage, bool forced)
        => RecordFailure(Math.Max(_subtreeDeepestFailure, floorPosition), errorMessage, forced);
}
