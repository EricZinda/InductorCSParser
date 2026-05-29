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
    /// A speculative read scope. Code inside reads input freely. On
    /// <see cref="Dispose"/> the lexer position rolls back to where the
    /// transaction opened unless <see cref="Commit"/> was called first.
    /// </summary>
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
        /// pass this to RecordFailure as the pre-read offset where the
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
        /// advanced it to, and <see cref="Dispose"/> won't roll it back.
        /// </summary>
        /// <remarks>
        /// Commit does nothing to the failure tracker. Failures survive both
        /// commit and rollback. A rejected Or branch or a count rule's stopped
        /// iteration is real evidence about the input and is kept, ranked by
        /// depth like any other failure. The one exception is lookahead, which
        /// is what BeginProbe is for: a Probe restores the failure tracker and
        /// subtree-extent mark as well as the position. See
        /// docs/ErrorArchitecture.md.
        /// </remarks>
        public void Commit()
        {
            if (_settled) return;
            _settled = true;
        }

        /// <summary>
        /// Rolls the lexer position back to where this transaction opened and
        /// marks it settled, so the later <see cref="Dispose"/> is a no-op.
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
        /// If <see cref="Commit"/> or <see cref="Rollback"/> already ran, the
        /// position is left alone. Either way Dispose pops the depth counter
        /// that drives trace indentation and merges this transaction's deepest
        /// failure back into the enclosing one. The pop waits until the scope
        /// exits rather than happening in Commit or Rollback, which allows
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
