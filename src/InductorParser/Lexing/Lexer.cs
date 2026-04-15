using System;

namespace InductorParser.Lexing;

public abstract class Lexer
{
    // This is the one reference kept to the input string, which is immutable and shared by all Tokens and
    // Spans. The GC sees this one string object and tracks it; everything else is stack-resident
    // structs that point back into this string. The GC never sees the Tokens or Spans, 
    // so they never have to be tracked or reclaimed. 
    private readonly string _input;
    private int _position;
    private int _deepestFailure;
    private string? _deepestFailureMessage;

    protected Lexer(string input)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
    }

    public string Input => _input;
    public int Position => _position;
    public int DeepestFailure => _deepestFailure;

    // The error message associated with the deepest failure seen so far, if
    // the rule that failed at that position had one set via .WithError(...).
    // Null when no rule at the deepest position set a custom message, or
    // when nothing has failed yet.
    public string? DeepestFailureMessage => _deepestFailureMessage;

    public bool IsEof => _position >= _input.Length;

    // Subclasses decide what one token means: one rune, one grapheme cluster,
    // etc. Called only when there is at least one char left in input.
    protected abstract int NextTokenLength(int startOffset);

    // Token is a `readonly ref struct` (defined in Token.cs). Returning
    // it copies the fields (a string reference, two ints, a bool, a
    // span) into the caller's storage rather than allocating on the
    // heap. For a struct this small the JIT usually returns it in
    // registers and skips even the stack copy. Either way, no GC
    // traffic.
    //
    // Each modifier on Token is pulling its weight:
    //   * `struct` keeps it off the heap. Value type semantics,
    //     returned by copying fields.
    //   * `readonly` means the fields never change after construction,
    //     so the compiler can skip defensive copies at call sites.
    //   * `ref` is the language-enforced lifetime guarantee: a ref
    //     struct is stack-only by rule, which is what makes it safe
    //     for Token to carry a Span as a field without risking the
    //     span outliving its source string.
    public Token Read()
    {
        if (IsEof) return new Token(_input, _position, 0, isEof: true);
        int len = NextTokenLength(_position);
        if (len <= 0) len = 1; // defensive: never advance zero on a non-EOF read
        Token t = new Token(_input, _position, len, isEof: false);
        _position += len;
        return t;
    }

    // Record that a rule just failed at the given input position. The
    // callers' responsibility is to pass the position of the offending
    // input — the start of the specific read that couldn't match — not the
    // post-read lexer position. That way `input[ErrorCharIndex]` gives the
    // actual wrong character on user-facing error reports.
    //
    // Primitive rules save pre-read via transaction.StartPosition (single-
    // read case) or a per-iteration local (multi-read lockstep). Composite
    // rules pass lexer.Position, which after a child's rollback equals
    // where that child started trying.
    //
    // "Deepest failure wins" across competing records:
    //   1. If the caller's position is strictly past the current deepest,
    //      move the deepest marker there and take this caller's message
    //      (which may be null).
    //   2. If the caller's position equals the current deepest AND has a
    //      non-null message AND nobody has claimed the message slot yet,
    //      they claim it.
    //
    // Rule (2) is what lets a composite like OneOrMore(...).WithError(...)
    // contribute its message even though its inner primitive already
    // recorded the same depth with a null message. The restriction to
    // equal-depth avoids shallow rules stealing the message slot from
    // unrelated deeper failures.
    public void RecordFailure(int position, string? errorMessage = null)
    {
        if (position > _deepestFailure)
        {
            _deepestFailure = position;
            _deepestFailureMessage = errorMessage;
            return;
        }
        if (position == _deepestFailure
            && errorMessage != null
            && _deepestFailureMessage == null)
        {
            _deepestFailureMessage = errorMessage;
        }
    }

    // Transactions exist so rules can speculatively read input and then
    // decide they didn't match: an alternative that
    // reads three tokens and then fails has to leave the lexer as if it
    // had never touched it, so the next alternative sees the same input.
    //
    // Every non-commit exit path (normal return, early return, a
    // thrown exception, a tripped budget) funnels through Dispose
    // via the `using` pattern. So failure always rolls back without
    // each rule site having to remember to do it. The one way to
    // commit is to actually call Commit, which
    // you only do on the success path just before returning.
    //
    // Transactions compose correctly under nested rules. When an
    // inner rule commits and then an outer rule fails, the outer's
    // Dispose rolls the position back to the outer's saved point,
    // which is earlier than the inner's saved point. The inner's
    // commit doesn't "promote" its reads to permanent; it only says
    // "I personally wouldn't roll back here." Any ancestor is free
    // to roll further back. That is the PEG semantic: only the
    // outermost successful match is final, and a failure anywhere
    // above it undoes everything below.
    //
    // Transaction is a struct (not a class) because every rule
    // invocation opens one, and allocating a new GC object each time
    // would dominate parse time. As a struct it lives inline in the
    // caller's stack frame; constructing one is two field writes,
    // disposing one is a flag read plus possibly one field write.
    //
    // Transaction is nested inside Lexer on purpose: the rollback logic
    // touches Lexer's private _position field, and nesting keeps that
    // access legitimate without widening visibility.
    public Transaction BeginTransaction() => new Transaction(this, _position);

    public struct Transaction : IDisposable
    {
        private readonly Lexer _lexer;
        private readonly int _savedPosition;
        private bool _settled;

        internal Transaction(Lexer lexer, int savedPosition)
        {
            _lexer = lexer;
            _savedPosition = savedPosition;
            _settled = false;
        }

        // The lexer position at the moment this transaction opened. Rules
        // pass this to RecordFailure as the "pre-read" offset where the
        // offending input starts. Saves a separate local that would
        // duplicate this state.
        public int StartPosition => _savedPosition;

        public void Commit() => _settled = true;

        public void Rollback()
        {
            if (_settled) return;
            _lexer._position = _savedPosition;
            _settled = true;
        }

        public void Dispose()
        {
            if (_settled) return;
            _lexer._position = _savedPosition;
            _settled = true;
        }
    }
}
