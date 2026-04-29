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
    // _input, _endPosition, _traceSink, _traceLevel are conceptually
    // readonly but lose the C# `readonly` keyword so the state-machine
    // evaluator's per-thread Lexer pool can call ResetForReuse() to
    // re-bind a previously-used Lexer instance to a new input string.
    // Constructors still treat them as set-once.
    private string _input;
    // Exclusive upper bound on _position. Defaults to _input.Length (a
    // lexer reads to end of input). Sub-lexer constructors bound this to
    // a sub-range of the shared input string so rules like WithinGrapheme
    // can run inner rules over a portion of the same string without
    // allocating a Substring copy. Tokens and positions still use
    // absolute offsets into _input, so outer error-position reporting
    // works without translation.
    private int _endPosition;
    private int _position;
    private int _deepestFailure;
    private string? _deepestFailureMessage;

    // Trace destination and verbosity. Null _traceSink means tracing is off.
    // When set, every rule, Lexer.Read, and deepest-failure update writes
    // one line per event.
    private TextWriter? _traceSink;
    private TraceLevel _traceLevel;

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
    // one-to-one. See ParseOptions.PreserveAllSymbols.
    internal bool PreserveAllSymbols { get; private set; }

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

    // Direct write-access to the read cursor for the state-machine
    // evaluator's backtrack-rollback path. Outside that path,
    // BeginTransaction is the right mechanism. The state machine
    // already tracks its own backtrack frames and restores positions
    // explicitly on failure, so it doesn't need the Transaction
    // wrapper's commit / rollback machinery.
    internal void SetPositionUnchecked(int position) => _position = position;

    // Re-bind a previously-used Lexer to a new input string and reset
    // all per-parse state. Lets the state-machine evaluator pool
    // RuneLexer / GraphemeLexer instances per thread instead of
    // allocating a fresh class per Parse call. Validates the same
    // input bounds the constructor does so a misuse fails loud
    // rather than producing a corrupt parse.
    //
    // After ResetForReuse, the caller is expected to invoke
    // ConfigureBudgets to set the budget limits and PreserveAllSymbols
    // for the new parse. The reset clears the budget counters so a
    // pooled lexer can't leak rule-invocation count from the previous
    // parse.
    internal void ResetForReuse(string input, TextWriter? traceSink, TraceLevel traceLevel)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        _input = input;
        _position = 0;
        _endPosition = input.Length;
        _traceSink = traceSink;
        _traceLevel = traceLevel;
        _deepestFailure = 0;
        _deepestFailureMessage = null;
        _transactionDepth = 0;
        _ruleInvocations = 0;
        _ruleDepth = 0;
        _ruleCountLimit = 0;
        _maxDepth = 0;
        _timeout = TimeSpan.Zero;
        _stopwatch = null;
        _cancellation = null;
        PreserveAllSymbols = false;
    }

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
    // time in the lexer's natural unit (one StringInfo text element under
    // GraphemeLexer, one scalar-value token under RuneLexer) should call
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

    internal void AdvanceUntilRuneIn(RuneSet candidates, char[]? bmpCandidates)
    {
        if (IsEof) return;

        if (bmpCandidates is { Length: > 0 } && this is RuneLexer)
        {
            int found = _input.IndexOfAny(bmpCandidates, _position, _endPosition - _position);
            _position = found >= 0 ? found : _endPosition;
            return;
        }

        while (_position < _endPosition)
        {
            if (TryPeekRune(_input, _position, out int runeValue, out _) && candidates.Contains(runeValue))
                return;

            int len = NextTokenLength(_position);
            if (len <= 0) len = 1;
            _position = Math.Min(_position + len, _endPosition);
        }
    }

    internal int AdvanceWhileSingleRuneIn(RuneSet set)
    {
        int count = 0;

        // RuneLexer is the hot path for regex-like scans: each token is a
        // Unicode scalar, so once the RuneSet says "yes" we can advance by
        // the decoded rune length directly. Malformed surrogate halves stop
        // the run, which is the same observable result OneOfRule gets from
        // Token.RuneValue == -1.
        if (this is RuneLexer)
        {
            while (_position < _endPosition)
            {
                int pos = _position;
                if (!TryPeekRune(_input, pos, out int runeValue, out int runeLen)
                    || pos + runeLen > _endPosition
                    || !set.Contains(runeValue))
                {
                    break;
                }

                _position = pos + runeLen;
                count++;
                Trace(TraceLevel.Diagnostic, "Lexer.AdvanceWhileSingleRuneIn", TraceOutcome.Info,
                    $"'{_input.Substring(pos, runeLen)}', Consumed: {_position}");
            }
            return count;
        }

        // GraphemeLexer must preserve OneOf semantics: a character-class
        // rule matches only when the whole grapheme token is exactly one
        // rune in the set. A multi-rune grapheme whose first rune happens
        // to be in the set is not part of the run.
        while (_position < _endPosition)
        {
            int pos = _position;
            int tokenLength = NextTokenLength(pos);
            if (tokenLength <= 0)
                break;
            if (!TryPeekRune(_input, pos, out int runeValue, out int runeLen)
                || pos + tokenLength > _endPosition
                || tokenLength != runeLen
                || !set.Contains(runeValue))
            {
                break;
            }

            _position = pos + tokenLength;
            count++;
            Trace(TraceLevel.Diagnostic, "Lexer.AdvanceWhileSingleRuneIn", TraceOutcome.Info,
                $"'{_input.Substring(pos, tokenLength)}', Consumed: {_position}");
        }
        return count;
    }

    internal void AdvanceUntilLiteralCandidateIn(
        RuneSet firstRunes,
        char[]? bmpFirstRunes,
        LiteralScannerCandidate[] literals,
        int[]? literalPositions)
    {
        if (IsEof) return;

        // This is the stronger scanner fast path for
        // ZeroOrMore(Or(literal-choice, AnyToken.Delete)). A plain
        // first-rune skip still stops at every "s" for a case-insensitive
        // "Sherlock" search and then invokes the full parser to reject it.
        // Here we keep consuming the deleted fallback ourselves until the
        // whole literal could match at the current lexer position. The
        // outer loop will then call the real rule, preserving the same tree
        // and capture behavior as the unoptimized parse.
        //
        // Single-literal only. The runtime's optimized substring search
        // jumps straight to the next full-literal candidate instead of
        // stopping at every matching first character. Multi-literal
        // alternates use the IndexOfAny path below: an experiment that
        // enabled the cached path for them measured 23x slower on the
        // rebar Sherlock haystack because the BCL's IndexOfAny is SIMD-
        // tuned for "any of these chars" while N separate IndexOf calls
        // are not. See src/Benchmarks/Rebar/results/multi-literal-cache-rebar-2026-04-28.csv.
        if (this is RuneLexer && literalPositions != null && literals.Length == 1)
        {
            while (_position < _endPosition)
            {
                int found = FindNextLiteralCandidate(literals, literalPositions, _input, _position, _endPosition);
                if (found < 0)
                {
                    _position = _endPosition;
                    return;
                }

                _position = found;
                if (AnyLiteralMatchesAt(literals, _input, _position, _endPosition))
                    return;

                int len = NextTokenLength(_position);
                if (len <= 0) len = 1;
                _position = Math.Min(_position + len, _endPosition);
            }
            return;
        }

        if (bmpFirstRunes is { Length: > 0 } && this is RuneLexer)
        {
            // Literal alternates still benefit from staying inside the
            // scanner: the real parser is only invoked when a complete
            // literal candidate matches. Use IndexOfAny for the shared
            // first-rune set, then check only plausible literals at the
            // candidate position.
            while (_position < _endPosition)
            {
                int found = _input.IndexOfAny(bmpFirstRunes, _position, _endPosition - _position);
                if (found < 0)
                {
                    _position = _endPosition;
                    return;
                }

                _position = found;
                if (AnyLiteralMatchesAt(literals, _input, _position, _endPosition))
                    return;

                int len = NextTokenLength(_position);
                if (len <= 0) len = 1;
                _position = Math.Min(_position + len, _endPosition);
            }
            return;
        }

        while (_position < _endPosition)
        {
            if (TryPeekRune(_input, _position, out int runeValue, out _)
                && firstRunes.Contains(runeValue)
                && AnyLiteralMatchesAt(literals, _input, _position, _endPosition))
            {
                return;
            }

            // GraphemeLexer comes through this path. Advance in the lexer's
            // natural token units so we never manufacture a start position
            // inside a grapheme cluster. A full literal match is only useful
            // at positions where the outer parser could legally start.
            int len = NextTokenLength(_position);
            if (len <= 0) len = 1;
            _position = Math.Min(_position + len, _endPosition);
        }
    }

    private static int FindNextLiteralCandidate(
        LiteralScannerCandidate[] literals,
        int[] literalPositions,
        string input,
        int position,
        int endPosition)
    {
        int best = -1;
        for (int index = 0; index < literals.Length; index++)
        {
            int found = literalPositions[index];
            if (found < position)
            {
                // Cache each literal's next substring hit. Literal-alternate
                // scanners call Advance once per real match; without this,
                // five alternatives would rescan the whole remaining input
                // five times after every match. Cached future hits survive
                // until the lexer moves past them.
                found = literals[index].IndexIn(input, position, endPosition);
                literalPositions[index] = found;
            }
            if (found >= 0 && (best < 0 || found < best))
                best = found;
        }
        return best;
    }

    private static bool AnyLiteralMatchesAt(
        LiteralScannerCandidate[] literals,
        string input,
        int position,
        int endPosition)
    {
        int firstRune = -1;
        TryPeekRune(input, position, out firstRune, out _);
        for (int index = 0; index < literals.Length; index++)
            if (literals[index].CanStartWith(firstRune)
                && literals[index].MatchesAt(input, position, endPosition))
            {
                return true;
            }
        return false;
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
        PreserveAllSymbols = options.PreserveAllSymbols;
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

internal readonly struct LiteralScannerCandidate
{
    private readonly int _firstRune;

    public LiteralScannerCandidate(string text, bool ignoreAsciiCase)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        IgnoreAsciiCase = ignoreAsciiCase;
        Lexer.TryPeekRune(text, 0, out _firstRune, out _);
    }

    public string Text { get; }
    public bool IgnoreAsciiCase { get; }

    public int IndexIn(string input, int position, int endPosition)
    {
        int count = endPosition - position;
        if (count < Text.Length)
            return -1;

        // Use the BCL's optimized substring search to hop across whole
        // spans of non-candidates. OrdinalIgnoreCase is broader than this
        // parser's ASCII-only ignore-case rule for some Unicode text, so
        // callers still confirm with MatchesAt before stopping. Broader
        // prefilter candidates are safe: they may cause extra parser work,
        // but they never skip a real ASCII-ignore-case match.
        return input.IndexOf(
            Text,
            position,
            count,
            IgnoreAsciiCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public bool CanStartWith(int runeValue)
    {
        if (runeValue == _firstRune)
            return true;

        // LiteralIgnoreAsciiCase is intentionally ASCII-only. Keep the
        // scanner prefilter under the exact same rule: only A-Z/a-z fold
        // together, and every other rune has to match by exact code point. This
        // prevents the optimization from accepting Unicode case-folding
        // candidates that the real rule would reject.
        return IgnoreAsciiCase
            && _firstRune >= 0
            && _firstRune <= char.MaxValue
            && runeValue >= 0
            && runeValue <= char.MaxValue
            && IsAsciiLetter((char)_firstRune)
            && IsAsciiLetter((char)runeValue)
            && (_firstRune | 0x20) == (runeValue | 0x20);
    }

    public bool MatchesAt(string input, int position, int endPosition)
    {
        if (position + Text.Length > endPosition)
            return false;

        ReadOnlySpan<char> actual = input.AsSpan(position, Text.Length);
        ReadOnlySpan<char> expected = Text.AsSpan();
        if (!IgnoreAsciiCase)
            return actual.SequenceEqual(expected);

        for (int index = 0; index < expected.Length; index++)
        {
            char ca = actual[index];
            char cb = expected[index];
            if (ca == cb) continue;
            if (IsAsciiLetter(ca) && IsAsciiLetter(cb) && (ca | 0x20) == (cb | 0x20)) continue;
            return false;
        }
        return true;
    }

    private static bool IsAsciiLetter(char c) =>
        (uint)((c | 0x20) - 'a') <= ('z' - 'a');
}
