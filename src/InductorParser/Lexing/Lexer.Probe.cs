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
    /// <remarks>
    /// <para><b>How nesting works</b></para>
    /// <para>
    /// Transactions and probes can contain either kind of scope. Open them in nested <c>using</c>
    /// blocks so each inner scope closes before its enclosing scope. Each scope saves the lexer
    /// position when it opens. Rolling back restores that position, including undoing reads made
    /// by any nested scopes. Committing an inner scope only prevents its own rollback. An enclosing
    /// scope can still roll back past it. This rule applies to every combination of transactions and probes.
    /// </para>
    /// <para><b>What each scope restores</b></para>
    /// <para>
    /// A <see cref="Transaction">Lexer.Transaction</see> rolls back the lexer position but keeps recorded
    /// failures, including failures from nested scopes, so they can help explain why parsing failed.
    /// A <see cref="Probe">Lexer.Probe</see> rolls back both the position and recorded failures to what they
    /// were when it opened. This also discards failures from nested scopes, even if those scopes committed.
    /// Use a probe for lookahead that shouldn't affect the eventual error message.
    /// Both kinds roll back when their <c>using</c> block ends unless committed.
    /// </para>
    /// Open one with <see cref="InductorParser.Lexing.Lexer.BeginProbe">Lexer.BeginProbe()</see>.
    /// </remarks>
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
