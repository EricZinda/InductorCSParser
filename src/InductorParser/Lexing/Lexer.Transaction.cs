using System;

namespace InductorParser.Lexing;

public sealed partial class Lexer
{
    // Current transaction nesting depth. BeginTransaction increments,
    // Transaction.Dispose decrements. Read by Lexer.Tracing's
    // WriteTraceLine for indent width.
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
    // to roll further back. That's the PEG semantic: only the
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
    // access legitimate without widening visibility. The factory method
    // BeginTransaction lives in Lexer.cs next to Read.
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

        internal Transaction(Lexer lexer, int savedPosition)
        {
            _lexer = lexer;
            _savedPosition = savedPosition;
            _savedSubtreeExtent = lexer._subtreeDeepestFailure;
            lexer._subtreeDeepestFailure = 0;
            _settled = false;
            _depthPopped = false;
        }

        // The lexer position at the moment this transaction opened. Rules
        // pass this to RecordFailure as the "pre-read" offset where the
        // offending input starts. Saves a separate local that would
        // duplicate this state.
        public int StartPosition => _savedPosition;

        // Commit succeeds the transaction: the lexer keeps the position
        // its children advanced it to, and Dispose won't roll it back.
        //
        // Commit does nothing to the failure tracker. Failures
        // survive both commit and rollback. A rejected Or branch or a
        // count rule's stopped iteration is real evidence about the
        // input and is kept, ranked by depth like any other failure. The
        // one exception is lookahead, and that's exactly what BeginProbe
        // is for: a Probe restores the failure tracker and subtree-extent
        // mark as well as the position. See docs/ErrorArchitecture.md.
        public void Commit()
        {
            if (_settled) return;
            _settled = true;
        }

        public void Rollback()
        {
            if (_settled) return;
            _lexer._position = _savedPosition;
            _settled = true;
        }

        // Dispose runs on every exit path (commit, rollback, normal return,
        // exception). It does three things, each behind its own flag check so
        // the combination of explicit Commit()/Rollback() followed by
        // implicit Dispose stays balanced:
        //   * Restore the lexer position if the transaction wasn't settled.
        //   * Pop the transaction-depth counter exactly once so trace
        //     indentation mirrors the transaction nesting. The depth pop
        //     has to happen regardless of commit vs. rollback, because the
        //     rule that opened this transaction is unwinding either way.
        //   * Merge this transaction's subtree-extent window back into the
        //     enclosing one with Math.Max. The deepest failure this rule's
        //     subtree reached is part of the parent's subtree too, so the
        //     parent's window has to absorb it. Done in the same once-only
        //     block as the depth pop, for the same "unwinding either way"
        //     reason.
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
