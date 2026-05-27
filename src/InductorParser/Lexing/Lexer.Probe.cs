using System;

namespace InductorParser.Lexing;

public sealed partial class Lexer
{
    // A Probe runs a rule as pure lookahead. A Transaction saves only the
    // read position and keeps failures across both commit and rollback,
    // because a rejected Or branch or a stopped count iteration is real
    // evidence about the input. A lookahead probe is different: its whole
    // excursion is off the real parse path, so the deepest-failure mark
    // left by a few tokens it matched before failing has to be discarded.
    // A Probe therefore brackets all three pieces of speculative state
    // (the read position, the three-slot failure tracker, and the
    // subtree-extent high-water mark) and Dispose-without-Commit restores
    // all three. Commit keeps all three (the probe turned out to be real
    // consumed content).
    //
    // Peek, Not, and ScanUntil's stopper / escape-start probes all use
    // this. Bracketing all three pieces of state in one Probe means a
    // rule can't forget a restore call, so an off-path probe failure
    // can't leak past where the probe ran, outrank the real parse
    // failure, and move the error caret.
    //
    // Like Transaction, Probe is a struct nested inside Lexer: it lives
    // inline on the caller's stack frame with no allocation, and the
    // nesting lets its restore logic touch Lexer's private _position,
    // _failureState, and _subtreeDeepestFailure without widening their
    // visibility. Unlike Transaction it doesn't touch _transactionDepth,
    // so it is not a trace-indentation level.
    public struct Probe : IDisposable
    {
        private readonly Lexer _lexer;
        private readonly int _savedPosition;
        private readonly FailureStateSnapshot _savedFailureState;
        private readonly int _savedSubtreeExtent;
        private bool _committed;
        private bool _disposed;

        internal Probe(Lexer lexer)
        {
            _lexer = lexer;
            _savedPosition = lexer._position;
            _savedFailureState = lexer._failureState;
            _savedSubtreeExtent = lexer._subtreeDeepestFailure;
            _committed = false;
            _disposed = false;
        }

        // The lexer position at the moment this probe opened. Rules pass
        // this to RecordFailure as the "pre-read" offset, the same way
        // Transaction.StartPosition is used.
        public int StartPosition => _savedPosition;

        // Keep everything the probe did: the position it advanced to, the
        // failures it recorded, and the subtree extent it reached. Used
        // when the speculative read turned out to be real consumed
        // content, the way ScanUntil's escape-start probe keeps a matched
        // escape start.
        public void Commit() => _committed = true;

        // Dispose without Commit restores all three pieces of state
        // exactly: the read position, the failure tracker, and the
        // subtree-extent mark. The extent is restored to the snapshot, not
        // Math.Max-merged the way Transaction.Dispose merges it: a
        // lookahead excursion's depth is off the real parse path and must
        // not leak into an enclosing composite's anchor.
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_committed) return;
            _lexer._position = _savedPosition;
            _lexer._failureState = _savedFailureState;
            _lexer._subtreeDeepestFailure = _savedSubtreeExtent;
        }
    }
}
