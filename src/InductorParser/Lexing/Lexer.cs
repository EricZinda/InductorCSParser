using System;
using System.IO;
using System.Runtime.CompilerServices;
using InductorParser.Tracing;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace InductorParser.Lexing;

public abstract class Lexer
{
    // _input is the one reference kept to the input string, which is immutable and shared by
    // every Token and ReadOnlySpan<char> the parser hands out. The GC sees this one string
    // object and tracks it. Everything else is stack-resident structs that point back into
    // this string. The GC never sees the Tokens or ReadOnlySpan<char>s, so they never have
    // to be tracked or reclaimed.
    private readonly string _input;
    // Exclusive upper bound on _position. Defaults to _input.Length (a
    // lexer reads to end of input). Sub-lexer constructors bound this to
    // a sub-range of the shared input string so rules like WithinGrapheme
    // can run inner rules over a portion of the same string without
    // allocating a Substring copy. Tokens and positions still use
    // absolute offsets into _input, so outer error-position reporting
    // works without translation.
    private readonly int _endPosition;
    private int _position;
    private int _deepestFailure;
    private string? _deepestFailureMessage;

    // Trace destination and verbosity. Null _traceSink means tracing is off.
    // When set, every rule, Lexer.Read, and deepest-failure update writes
    // one line per event.
    private readonly TextWriter? _traceSink;
    private readonly TraceLevel _traceLevel;

    private int _transactionDepth;

    // Budget tracking. Set by ConfigureBudgets right after construction.
    // The three limit fields (_ruleCountLimit, _maxDepth, _timeout)
    // are zero-disabled: a zero value means "no limit".
    // The counter fields (_ruleInvocations, _ruleDepth) accumulate as the parse runs.
    private long _ruleCountLimit;
    private int _maxDepth;
    private TimeSpan _timeout;

    private long _ruleInvocations;
    private int _ruleDepth;
    private Stopwatch? _stopwatch;
    private ParseCancellation? _cancellation;

    // Debug knob wired in from ParseOptions. Rules that would normally
    // apply parse-time tree-shape optimizations (e.g.
    // Delete-node filtering) consult this flag and skip the optimization
    // when it's set, producing a tree whose shape matches the grammar
    // one-to-one. See ParseOptions.PreserveFlattenWrappers.
    internal bool PreserveFlattenWrappers { get; private set; }

    // Periodic budget check fires every BudgetCheckInterval rule
    // invocations rather than every one. Power of two so the check is a
    // single bitwise AND in the hot path. 1024 keeps the per-call
    // overhead invisible on well-formed input (a million-invocation parse
    // does ~1000 wall-clock polls) while still tripping promptly enough
    // that the Stopwatch and ParseCancellation checks feel responsive.
    private const int BudgetCheckInterval = 1024;
    private const int BudgetCheckMask = BudgetCheckInterval - 1;

    protected Lexer(string input)
        : this(input, traceSink: null, traceLevel: TraceLevel.Normal)
    {
    }

    protected Lexer(string input, TextWriter? traceSink, TraceLevel traceLevel)
        : this(input, startPosition: 0, endPosition: (input ?? throw new ArgumentNullException(nameof(input))).Length, traceSink, traceLevel)
    {
    }

    // Bounded-range constructor used to build sub-lexers that read a
    // portion of a shared input string. startPosition is the initial
    // read cursor and endPosition is the exclusive upper bound (IsEof
    // fires when _position reaches endPosition). Tokens still carry
    // absolute offsets into the shared string so the outer parse's
    // error-position reporting works uniformly whether positions come
    // from the main lexer or a sub-lexer.
    protected Lexer(string input, int startPosition, int endPosition, TextWriter? traceSink, TraceLevel traceLevel)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        if ((uint)startPosition > (uint)_input.Length)
            throw new ArgumentOutOfRangeException(nameof(startPosition), startPosition, "startPosition must be in [0, input.Length].");
        if (endPosition < startPosition || endPosition > _input.Length)
            throw new ArgumentOutOfRangeException(nameof(endPosition), endPosition, "endPosition must be in [startPosition, input.Length].");
        _position = startPosition;
        _endPosition = endPosition;
        _traceSink = traceSink;
        _traceLevel = traceLevel;
    }

    public string Input => _input;
    public int Position => _position;
    public int DeepestFailure => _deepestFailure;

    // The error message associated with the deepest failure seen so far
    // if one was set.
    public string? DeepestFailureMessage => _deepestFailureMessage;

    public bool IsEof => _position >= _endPosition;

    internal int TransactionDepth => _transactionDepth;

    internal bool IsTracing(TraceLevel level) =>
        _traceSink != null && _traceLevel >= level;

    // The `message` parameter is a TraceInterpolatedStringHandler, 
    // which means callers can write `lexer.Trace(level, label, outcome, $"...")` 
    // and the C# compiler will skip building the string when the sink is off or the level
    // is gated out. No "if" needed at the caller. See
    // TraceInterpolatedStringHandler for how the compiler rewrite
    // actually works.
    //

    // Cost when tracing is off (canonical reference for trace perf):
    // Note that lexer.Trace(...) still gets called even when tracing
    // is off. The TraceInterpolatedStringHandler argument only gates the
    // expensive string-building work, not the method invocation
    // itself. Trace stays cheap because:
    //
    //   1. The first thing inside Trace is a null check, so Trace
    //      returns immediately without doing any indent/write work.
    //      The Rule.TraceSuccess / TraceFailure helpers do the same
    //      thing one layer up.
    //   2. Trace carries [MethodImpl(MethodImplOptions.AggressiveInlining)],
    //      so in Release builds the JIT folds the body into the caller.
    //      What looks like a method call in the IL becomes a handful
    //      of inline machine instructions.
    //
    // Net cost when tracing is off: about the same as the hand written
    // alternative:
    //
    //     if (lexer.IsTracing(level))
    //         lexer.WriteTraceLine(label, outcome, $"...");
    //
    // In Debug builds where AggressiveInlining
    // is sometimes ignored, you do pay one real call frame per trace site.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Trace(
        TraceLevel level,
        string label,
        TraceOutcome outcome,
        [InterpolatedStringHandlerArgument("", nameof(level))]
        TraceInterpolatedStringHandler message)
    {
        string? formatted = message.GetFormattedOrNull();
        if (formatted == null) return;
        WriteTraceLine(label, outcome, formatted);
    }

    // Write one trace line
    // Fields are written incrementally rather than pre-concatenated to
    // avoid allocating an intermediate string for every line.
    internal void WriteTraceLine(string label, TraceOutcome outcome, string message)
    {
        for (int i = 0; i < _transactionDepth * 3; i++)
            _traceSink!.Write(' ');
        switch (outcome)
        {
            case TraceOutcome.Success: _traceSink!.Write("SUCC | "); break;
            case TraceOutcome.Failure: _traceSink!.Write("FAIL | "); break;
        }
        _traceSink!.Write(label);
        if (message.Length > 0)
        {
            _traceSink.Write(": ");
            _traceSink.Write(message);
        }
        _traceSink.WriteLine();
    }

    // Subclasses decide what one token means: one rune, one grapheme cluster,
    // etc. Called only when there is at least one char left in input.
    protected abstract int NextTokenLength(int startOffset);

    // Peek the rune at `pos` in `input` without advancing any lexer
    // state. Writes the rune value and its UTF-16 length. Returns
    // false if the char at `pos` is a stray surrogate without its
    // paired half (malformed UTF-16 that doesn't represent any real
    // Unicode character).
    //
    // Always one rune at a time, regardless of which Lexer subclass
    // is in use. Rules that scan rune-by-rune need consistent
    // semantics even when the outer parse was configured with
    // GraphemeLexer. Callers that want to inspect one token at a
    // time in the lexer's natural unit (one grapheme under
    // GraphemeLexer, one rune under RuneLexer) should call
    // lexer.Read(). Note that Read() advances
    // the lexer, so for peek semantics wrap it in an uncommitted
    // transaction:
    //
    //     using var transaction = lexer.BeginTransaction();
    //     var token = lexer.Read();
    //     // inspect token.Memory, token.Chars, etc.
    //     // do NOT call transaction.Commit(). When the `using`
    //     // block exits, Transaction.Dispose sees Commit wasn't
    //     // called and restores the lexer's position to where
    //     // BeginTransaction was called.
    //
    // That's the idiomatic "peek a token" pattern. Rules like Peek
    // and Not use exactly this shape.
    //
    // Aggressive-inlined so the caller sees the same machine
    // code the fully inline decoder would. Pulled out so the
    // surrogate-pair logic lives in exactly one place instead of
    // being re-implemented in every rule that peeks.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryPeekRune(string input, int pos, out int runeValue, out int runeLen)
    {
        char c0 = input[pos];
        if (char.IsHighSurrogate(c0)
            && pos + 1 < input.Length
            && char.IsLowSurrogate(input[pos + 1]))
        {
            runeValue = char.ConvertToUtf32(c0, input[pos + 1]);
            runeLen = 2;
            return true;
        }
        if (char.IsSurrogate(c0))
        {
            // Stray surrogate without its paired half. Not a valid
            // Unicode character.
            runeValue = -1;
            runeLen = 0;
            return false;
        }
        runeValue = c0;
        runeLen = 1;
        return true;
    }

    // Advance one token and return it. The "one token" shape is decided
    // by the subclass (see NextTokenLength). Cheap: Token is a stack-only
    // ref struct carrying offset+length into the input string, no
    // allocation, no substring copying. See Token for why it's safe to
    // return one by value.
    public Token Read()
    {
        if (IsEof)
        {
            Trace(TraceLevel.Diagnostic, "Lexer.Read", TraceOutcome.Info, $"'<EOF>', Consumed: {_position}");
            return new Token(_input, _position, 0, isEof: true);
        }
        int len = NextTokenLength(_position);
        if (len <= 0) len = 1; // defensive: never advance zero on a non-EOF read
        Token t = new Token(_input, _position, len, isEof: false);
        _position += len;
        Trace(TraceLevel.Diagnostic, "Lexer.Read", TraceOutcome.Info, $"'{_input.Substring(t.Offset, t.Length)}', Consumed: {_position}");
        return t;
    }

    // Record that a rule just failed at the given input position. The
    // callers' responsibility is to pass the position of the offending
    // input: the *start* of the specific read that couldn't match, not the
    // post-read lexer position. That way `input[ErrorCharIndex]` gives the
    // actual wrong character on user-facing error reports.
    //
    // "Deepest failure wins" across competing records:
    //   1. If the caller's position is strictly past the current deepest,
    //      move the deepest marker there and take this caller's message
    //      (which may be null).
    //   2. If the caller's position equals the current deepest AND has a
    //      non-null message AND nobody has claimed the message slot yet,
    //      they claim it.
    //
    // Rule 2 is what lets a composite like OneOrMore(...).WithError(...)
    // contribute its message even though its inner leaf already
    // recorded the same depth with a null message. The restriction to
    // equal-depth avoids shallow rules stealing the message slot from
    // unrelated deeper failures.
    public void RecordFailure(int position, string? errorMessage = null)
    {
        if (position > _deepestFailure)
        {
            _deepestFailure = position;
            _deepestFailureMessage = errorMessage;
            Trace(TraceLevel.Diagnostic, "Lexer.RecordFailure", TraceOutcome.Info, $"new deepest failure at char {position}");
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
    // commit doesn't "promote" its reads to permanent. It only says
    // "I personally wouldn't roll back here." Any ancestor is free
    // to roll further back. That is the PEG semantic: only the
    // outermost successful match is final, and a failure anywhere
    // above it undoes everything below.
    //
    // Transaction is a struct (not a class) because every rule
    // invocation opens one, and allocating a new GC object each time
    // would dominate parse time. As a struct it lives inline in the
    // caller's stack frame. Constructing one is two field writes,
    // disposing one is a flag read plus possibly one field write.
    //
    // Transaction is nested inside Lexer on purpose: the rollback logic
    // touches Lexer's private _position field, and nesting keeps that
    // access legitimate without widening visibility.
    public Transaction BeginTransaction()
    {
        _transactionDepth++;
        return new Transaction(this, _position);
    }

    // Wire the per-parse runtime budgets onto the lexer. Called by
    // Rule.Parse right after constructing the lexer and before the first
    // rule fires. The Stopwatch is only allocated when a positive Timeout
    // is set. TimeSpan.Zero means "disabled" and skips both the
    // allocation and the per-check comparison.
    internal void ConfigureBudgets(ParseOptions options)
    {
        _ruleCountLimit = options.RuleCountLimit;
        _maxDepth = options.MaxDepth;
        _timeout = options.Timeout;
        _cancellation = options.Cancellation;
        _stopwatch = options.Timeout > TimeSpan.Zero ? Stopwatch.StartNew() : null;
        PreserveFlattenWrappers = options.PreserveFlattenWrappers;
    }

    // Called by Rule.TryParse on entry to every rule invocation. Two
    // cheap counters plus a periodic deeper check.
    //
    // Depth is checked every call because (a) the comparison is one
    // integer op and (b) catching it late means a stack overflow already
    // crashed the host process, which is exactly what MaxDepth is here
    // to prevent.
    //
    // The rule-count limit, timeout, and cancellation flag are checked
    // every BudgetCheckInterval invocations (1024). Per-call cost is a
    // single bitwise-AND, so the amortized overhead is invisible on
    // well-formed input. Tripping at interval boundaries instead of
    // exactly when the limit is reached means the parse may run up to
    // 1023 invocations past RuleCountLimit before the abort fires,
    // but that overshoot is predictable: the trip always happens at
    // the first interval boundary past the limit, and the invocation
    // count is a pure function of grammar + input. Run the same parse
    // twice on the same input and both runs abort at the exact same
    // invocation count, so budget tests won't be flaky.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EnterRule()
    {
        _ruleDepth++;
        if (_maxDepth > 0 && _ruleDepth > _maxDepth)
            throw new ParseBudgetExceeded(ParseOutcome.DepthLimitExceeded);

        _ruleInvocations++;
        if ((_ruleInvocations & BudgetCheckMask) == 0)
            CheckPeriodicBudgets();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ExitRule()
    {
        _ruleDepth--;
    }

    // Off the hot path on purpose: only invoked once every
    // BudgetCheckInterval rule invocations, so making it a separate
    // non-inlined method keeps EnterRule small enough for the JIT to
    // inline cleanly.
    private void CheckPeriodicBudgets()
    {
        if (_ruleCountLimit > 0 && _ruleInvocations > _ruleCountLimit)
            throw new ParseBudgetExceeded(ParseOutcome.RuleCountLimitExceeded);
        if (_stopwatch != null && _stopwatch.Elapsed >= _timeout)
            throw new ParseBudgetExceeded(ParseOutcome.Timeout);
        if (_cancellation != null && _cancellation.IsCanceled)
            throw new ParseBudgetExceeded(ParseOutcome.Canceled);
    }

    public struct Transaction : IDisposable
    {
        private readonly Lexer _lexer;
        private readonly int _savedPosition;
        private bool _settled;
        private bool _depthPopped;

        internal Transaction(Lexer lexer, int savedPosition)
        {
            _lexer = lexer;
            _savedPosition = savedPosition;
            _settled = false;
            _depthPopped = false;
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

        // Dispose runs on every exit path (commit, rollback, normal return,
        // exception). It does two things, each guarded by its own flag so
        // the combination of explicit Commit()/Rollback() followed by
        // implicit Dispose stays balanced:
        //   * Restore the lexer position if the transaction wasn't settled.
        //   * Pop the transaction-depth counter exactly once so trace
        //     indentation mirrors the transaction nesting. The depth pop
        //     has to happen regardless of commit vs. rollback, because the
        //     rule that opened this transaction is unwinding either way.
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
                _depthPopped = true;
            }
        }
    }
}
