using System;

namespace InductorParser.Lexing;

public sealed partial class Lexer
{
    // Current transaction nesting depth. BeginTransaction increments,
    // Transaction.Dispose decrements. Drives the indent width of trace output.
    private int _transactionDepth;

    // Transactions exist so rules can speculatively read input and then
    // decide they didn't match: an alternative that reads three tokens
    // and then fails has to leave the lexer as if it had never touched
    // it, so the next alternative sees the same input.
    //
    // Every non-commit exit path (normal return, early return, a
    // thrown exception, a tripped budget) funnels through Dispose
    // via the `using` pattern. So failure always rolls back without
    // each rule site having to remember to do it. The one way to
    // commit is to actually call Commit, which you only do on the
    // success path just before returning.
    //
    // Transactions compose correctly under nested rules. When an
    // inner rule commits and then an outer rule fails, the outer's
    // Dispose rolls the position back to the outer's saved point,
    // which is earlier than the inner's saved point. The inner's
    // commit doesn't "promote" its reads to permanent. It only says
    // "I personally wouldn't roll back here." Any ancestor is free
    // to roll further back. That's the semantic: only the
    // outermost successful match is final, and a failure anywhere
    // above it undoes everything below.
    //
    // Transaction is a struct (not a class) because every rule
    // invocation opens one, and allocating a new GC object each time
    // would dominate parse time. As a struct it lives inline in the
    // caller's stack frame. Constructing one is a handful of field
    // writes, disposing one is a flag read plus a couple of field
    // writes.
    //
    // Transaction is nested inside Lexer on purpose: the rollback logic
    // touches Lexer's private _position field, and nesting keeps that
    // access legitimate without widening visibility.
    /// <summary>
    /// Allows a rule to call <see cref="Lexer.Read">Lexer.Read()</see> as much as it wants, then call
    /// <see cref="Commit">Lexer.Transaction.Commit()</see> to keep the position or
    /// <see cref="Rollback">Lexer.Transaction.Rollback()</see> to reset it to where the transaction opened. On
    /// <see cref="Dispose">Lexer.Transaction.Dispose()</see> the lexer position rolls back to where the
    /// transaction opened unless <see cref="Commit">Lexer.Transaction.Commit()</see> was called first.
    /// </summary>
    /// <remarks>
    /// <para><b>How nesting works</b></para>
    /// <para>
    /// Transactions and probes can contain each other in any combination. Open them in nested <c>using</c>
    /// blocks so each inner scope closes before its enclosing scope. Each scope saves the lexer
    /// position when it opens. Rolling back restores that position, including undoing reads made
    /// by any nested scopes. Committing an inner scope only prevents its own rollback. An enclosing
    /// scope can still roll back past it.
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
    /// <para>
    /// A rule writer rarely opens one by hand: the parser wraps every
    /// <see cref="InductorParser.Rule.TryParseRule(InductorParser.Lexing.Lexer,System.Int32,InductorParser.SyntaxTree.FlattenType,System.Collections.Generic.List{InductorParser.SyntaxTree.Symbol})">Rule.TryParseRule</see>
    /// call in one, so a rule opens its own only for a scope inside its own match.
    /// Open one with <see cref="Lexer.BeginTransaction">Lexer.BeginTransaction()</see>.
    /// </para>
    /// </remarks>
    public struct Transaction : IDisposable
    {
        private readonly Lexer _lexer;
        private readonly int _savedPosition;
        // The enclosing transaction's subtree-extent high-water mark,
        // saved here while this transaction zeroes the lexer's so the
        // window covers only this rule's subtree. Dispose merges the two
        // back together.
        private readonly int _savedSubtreeExtent;
        private bool _settled;
        private bool _depthPopped;

        /// <summary>
        /// The lexer position at the moment this transaction opened. Rules
        /// pass this to <see cref="InductorParser.Lexing.Lexer.RecordFailure(System.Int32,System.String,System.Boolean)">Lexer.RecordFailure</see> as the pre-read offset where the
        /// offending input starts.
        /// </summary>
        public int StartPosition => _savedPosition;

        internal Transaction(Lexer lexer, int savedPosition)
        {
            _lexer = lexer;
            _savedPosition = savedPosition;
            _savedSubtreeExtent = lexer._subtreeDeepestFailure;
            lexer._subtreeDeepestFailure = 0;
            _settled = false;
            _depthPopped = false;
        }

        /// <summary>
        /// Succeeds the transaction: the lexer keeps the position its children
        /// advanced it to, and <see cref="Dispose">Lexer.Transaction.Dispose()</see> won't roll it back.
        /// </summary>
        /// <remarks>
        /// <see cref="Commit">Lexer.Transaction.Commit</see> does nothing to the failure tracker. Failures survive both
        /// commit and rollback. A rejected <see cref="Rules.Or">Rules.Or</see> branch or a failed attempt to match
        /// another repetition in <see cref="Rules.OneOrMore(Rule)">Rules.OneOrMore(Rule)</see>
        /// is real evidence about the input and is kept, ranked by
        /// depth like any other failure. The one exception is lookahead, which
        /// is what <see cref="InductorParser.Lexing.Lexer.BeginProbe">Lexer.BeginProbe()</see> is for: a <see cref="Probe">Lexer.Probe</see> restores the failure tracker and
        /// subtree-extent mark as well as the position. See
        /// <a href="../docs/ErrorArchitecture.md">Error Reporting Architecture</a>.
        /// </remarks>
        public void Commit()
        {
            if (_settled) return;
            _settled = true;
        }

        /// <summary>
        /// Rolls the lexer position back to where this transaction opened and
        /// marks it settled, so the later <see cref="Dispose">Lexer.Transaction.Dispose()</see> is a no-op.
        /// </summary>
        public void Rollback()
        {
            if (_settled) return;
            _lexer._position = _savedPosition;
            _settled = true;
        }

        /// <summary>
        /// Runs on every exit path (commit, rollback, normal return,
        /// exception) and rolls the position back if the transaction was never
        /// settled.
        /// </summary>
        /// <remarks>
        /// If <see cref="Commit">Lexer.Transaction.Commit()</see> or <see cref="Rollback">Lexer.Transaction.Rollback()</see> already ran, the
        /// position is left alone. Either way <see cref="Dispose">Lexer.Transaction.Dispose</see> pops the depth counter
        /// that drives trace indentation and merges this transaction's deepest
        /// failure back into the enclosing one. The pop waits until the scope
        /// exits rather than happening in <see cref="Commit">Lexer.Transaction.Commit</see> or <see cref="Rollback">Lexer.Transaction.Rollback</see>, which allows
        /// traces to emit at the right level from within the using block after
        /// a commit or rollback. Flag checks keep it balanced so each part runs
        /// once even when an explicit call came first.
        /// </remarks>
        public void Dispose()
        {
            if (!_settled)
            {
                _lexer._position = _savedPosition;
                _settled = true;
            }
            if (!_depthPopped)
            {
                _lexer._transactionDepth--;
                if (_lexer._subtreeDeepestFailure < _savedSubtreeExtent)
                    _lexer._subtreeDeepestFailure = _savedSubtreeExtent;
                _depthPopped = true;
            }
        }
    }
}
