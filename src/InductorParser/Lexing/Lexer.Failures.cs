using System;
using System.Runtime.CompilerServices;
using InductorParser.Tracing;

namespace InductorParser.Lexing;

public sealed partial class Lexer
{
    // The position of the failure that would be reported if the parse
    // ended now. See docs/ErrorArchitecture.md for how forced, named, and
    // mechanical failures are ranked.
    public int DeepestFailure =>
        _failureState.ForcedMessage != null ? _failureState.ForcedPosition
        : _failureState.NamedMessage != null
            && _failureState.NamedPosition >= _failureState.MechanicalPosition
          ? _failureState.NamedPosition
          : _failureState.MechanicalPosition;

    // The error message that would be reported if the parse ended now:
    // the message carried by whichever failure DeepestFailure picks, or
    // null when that failure carries no message. See
    // docs/ErrorArchitecture.md.
    public string? DeepestFailureMessage =>
        _failureState.ForcedMessage != null ? _failureState.ForcedMessage
        : _failureState.NamedMessage != null
            && _failureState.NamedPosition >= _failureState.MechanicalPosition
          ? _failureState.NamedMessage
          : null;

    // True when the failure DeepestFailure / DeepestFailureMessage would
    // surface is a forced WithError override (recorded via
    // RecordFailure(..., forced: true)). WithinTokenRule reads this to
    // carry an inner sub-lexer's forced flag through to the outer lexer.
    public bool DeepestFailureIsForced => _failureState.ForcedMessage != null;

    // Three-slot failure tracker (mechanical / named / forced). The
    // depth-primary resolution rules and per-slot semantics live in
    // docs/ErrorArchitecture.md.
    //
    // Mutable struct: the Lexer mutates the fields in place from
    // RecordFailure. A Probe value-copies it when it opens and writes
    // the copy back on rollback, which is how a lookahead probe's
    // failures get undone (see the Probe struct). Private because rules
    // never touch the failure tracker directly: they go through
    // RecordFailure / RecordCompositeFailure and BeginProbe.
    private FailureStateSnapshot _failureState;

    // High-water mark of the deepest input position any RecordFailure
    // call has reported within the current transaction's window. Every
    // BeginTransaction saves and zeroes this, and the matching Dispose
    // merges it back into the enclosing window with Math.Max. A composite
    // rule's RecordCompositeFailure reads it once its children have run,
    // so a .WithError it carries anchors at the deepest position its
    // subtree reached rather than at the composite's own start.
    private int _subtreeDeepestFailure;

    private struct FailureStateSnapshot
    {
        public int MechanicalPosition;
        public int NamedPosition;
        public string? NamedMessage;
        public int ForcedPosition;
        public string? ForcedMessage;
    }

    // Record that a rule just failed at the given input position. The
    // caller's responsibility is to pass the position of the offending
    // input: the *start* of the specific read that couldn't match, not
    // the post-read lexer position. The three-slot tracker and the
    // depth-primary resolution rules are described in
    // docs/ErrorArchitecture.md.
    public void RecordFailure(int position, string? errorMessage = null, bool forced = false)
    {
        // Every failure, whatever its slot, advances the subtree-extent
        // high-water mark so an enclosing composite can anchor its own
        // .WithError at the deepest position its subtree reached.
        if (position > _subtreeDeepestFailure)
            _subtreeDeepestFailure = position;
        if (errorMessage == null)
        {
            if (position > _failureState.MechanicalPosition)
            {
                _failureState.MechanicalPosition = position;
                Trace(TraceLevel.Diagnostic, "Lexer.RecordFailure", TraceOutcome.Info,
                      $"new deepest failure at char {position}");
            }
            return;
        }
        if (forced)
        {
            bool moved = position > _failureState.ForcedPosition;
            if (moved || _failureState.ForcedMessage == null)
            {
                _failureState.ForcedPosition = position;
                _failureState.ForcedMessage = errorMessage;
                // Trace only when position strictly increases. A silent
                // slot-fill at the initial position (first writer claims
                // an empty slot without moving it) emits no trace line.
                if (moved)
                {
                    Trace(TraceLevel.Diagnostic, "Lexer.RecordFailure", TraceOutcome.Info,
                          $"forced deepest failure to char {position}");
                }
            }
            return;
        }
        {
            bool moved = position > _failureState.NamedPosition;
            if (moved || _failureState.NamedMessage == null)
            {
                _failureState.NamedPosition = position;
                _failureState.NamedMessage = errorMessage;
                if (moved)
                {
                    Trace(TraceLevel.Diagnostic, "Lexer.RecordFailure", TraceOutcome.Info,
                          $"new deepest failure at char {position}");
                }
            }
        }
    }

    // Record a composite rule's own .WithError failure, anchored at the
    // deeper of floorPosition and the subtree-extent high-water mark.
    // floorPosition is the composite's own floor: usually lexer.Position
    // after the failing child rolled the cursor back, or the composite's
    // start position. Folds in the Math.Max idiom every composite would
    // otherwise repeat by hand. See docs/ErrorArchitecture.md for why a
    // composite anchors this way.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordCompositeFailure(int floorPosition, string? errorMessage, bool forced)
        => RecordFailure(Math.Max(_subtreeDeepestFailure, floorPosition), errorMessage, forced);
}
