using System;

namespace InductorParser.Lexing;

public sealed partial class Lexer
{
    // Peek, Not, and ScanUntil's stopper / escape-start probes all use
    // this. Bracketing the three pieces of speculative state in one struct
    // means no rule can forget a restore call and let an off-path failure
    // outrank the real one. Unlike Transaction, Probe doesn't touch
    // _transactionDepth, so it's not a trace-indentation level.

    /// <summary>
    /// Like a <see cref="Transaction"/>, but a rollback also discards
    /// the failures and subtree extent the probe recorded, so a failed
    /// lookahead leaves no trace in the parse.
    /// </summary>
    public struct Probe : IDisposable
    {
        private readonly Lexer _lexer;
        private readonly int _savedPosition;
        private readonly FailureStateSnapshot _savedFailureState;
        private readonly int _savedSubtreeExtent;
        private bool _committed;
        private bool _disposed;

        /// <summary>
        /// The lexer position at the moment this probe opened.
        /// </summary>
        /// <remarks>
        /// Rules pass this to <see cref="InductorParser.Lexing.Lexer.RecordFailure(System.Int32,System.String,System.Boolean)">Lexer.RecordFailure</see> as the "pre-read" offset,
        /// the same way <see cref="Transaction.StartPosition">Lexer.Transaction.StartPosition</see> is used.
        /// </remarks>
        public int StartPosition => _savedPosition;

        internal Probe(Lexer lexer)
        {
            _lexer = lexer;
            _savedPosition = lexer._position;
            _savedFailureState = lexer._failureState;
            _savedSubtreeExtent = lexer._subtreeDeepestFailure;
            _committed = false;
            _disposed = false;
        }

        /// <summary>
        /// Keep everything the probe did: the position it advanced to,
        /// the failures it recorded, and the subtree extent it reached.
        /// </summary>
        /// <remarks>
        /// Use this when the speculative read turned out to be real
        /// consumed content.
        /// </remarks>
        public void Commit() => _committed = true;

        /// <summary>
        /// When called without a prior <see cref="Commit">Lexer.Probe.Commit()</see>, restores the
        /// read position, the failure tracker, and the subtree-extent mark
        /// to where they were when the probe opened.
        /// </summary>
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
