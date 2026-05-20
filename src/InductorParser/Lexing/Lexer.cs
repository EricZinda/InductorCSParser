using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using InductorParser.SyntaxTree;
using InductorParser.Tracing;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace InductorParser.Lexing;

// One token per .NET text element (grapheme cluster). Cluster
// boundaries come from the GraphemeClusterIndex on the input string
//
// One sub-lexer mode (selected by an internal constructor and used only
// by WithinTokenRule) walks one rune per token instead of one full
// token. That sub-lexer reads a bounded range of the same shared
// input string and lets the inner rule walk the runes inside one
// token. The mode is one private bool checked once in Read; not a
// virtual dispatch.
public sealed partial class Lexer
{
    // _input is the one reference kept to the input string, which is immutable and shared by
    // every Token and ReadOnlySpan<char> the parser hands out. The GC sees this one string
    // object and tracks it. Everything else is stack-resident structs that point back into
    // this string. The GC never sees the Tokens or ReadOnlySpan<char>s, so they never have
    // to be tracked or reclaimed.
    // _input, _endPosition, _traceSink, _traceLevel are conceptually
    // readonly but lose the C# `readonly` keyword so a pooled Lexer can
    // be re-bound to a new input string through ResetForReuse() instead
    // of allocating a fresh instance per parse. Constructors still treat
    // them as set-once.
    private string _input;
    // Exclusive upper bound on _position. Defaults to _input.Length (a
    // lexer reads to end of input). Sub-lexer constructors bound this to
    // a sub-range of the shared input string so rules like WithinToken
    // can run inner rules over a portion of the same string without
    // allocating a Substring copy. Tokens and positions still use
    // absolute offsets into _input, so outer error-position reporting
    // works without translation.
    private int _endPosition;
    private int _position;
    // Three-slot failure tracking (mechanical / named / forced). See
    // docs/ErrorArchitecture.md.
    private FailureStateSnapshot _failureState;

    // High-water mark of the deepest input position any RecordFailure
    // call has reported within the current transaction's window. Every
    // BeginTransaction saves and zeroes this, and the matching Dispose
    // merges it back into the enclosing window with Math.Max. A composite
    // rule's RecordCompositeFailure reads it once its children have run,
    // so a .WithError it carries anchors at the deepest position its
    // subtree reached rather than at the composite's own start. See
    // docs/ErrorArchitecture.md, "Where each rule records its failure".
    private int _subtreeDeepestFailure;

    // Sub-lexer mode: when true, Read advances one rune at a time
    // instead of one grapheme cluster. Set only by the internal
    // bounded-range constructor used by WithinTokenRule, which
    // creates a sub-lexer over the runes inside one outer token.
    // All public construction paths leave this false (token mode).
    private readonly bool _oneRunePerToken;

    // Per-input cache of UAX #29 grapheme cluster boundaries.
    private GraphemeClusterIndex _graphemeIndex;

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

    // When non-null, this lexer is a sub-lexer that delegates ALL budget
    // bookkeeping (depth increments, invocation counts, periodic checks)
    // to the named parent lexer. The sub-lexer's own _ruleDepth /
    // _ruleInvocations stay at zero and unused; EnterRuleBudgetChecks /
    // ExitRuleBudgetChecks / EnterRuleAtDepthBudgetChecks /
    // TickPeriodicBudget forward straight to the
    // parent so MaxDepth and RuleCountLimit cover the COMBINED depth
    // (outer's running depth at the moment the sub-lexer opened, PLUS
    // the inner's recursion on top of it) and total invocations across
    // outer + inner. Cancellation and the wall-clock Stopwatch are
    // also the parent's, which a non-null _budgetParent already implies
    // (InheritBudgetsFrom copies those references).
    //
    // Null means "this is a top-level lexer that owns its own budget
    // state," which is the Rule.ParseRecursive path. Set by
    // InheritBudgetsFrom and cleared by ResetForReuse.
    private Lexer? _budgetParent;

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

    // Per-parse context the parse driver supplies so every Symbol the
    // engine builds can resolve original-input positions / text. Null
    // for sub-lexers (WithinTokenRule), pooled-Lexer reuse before
    // ResetForReuse re-binds it. Has constructors that don't take a
    // context (callers that build a Lexer directly and don't need
    // Symbol-level position recovery).
    private ParseContext? _context;

    public ParseContext? Context => _context;

    public Lexer(string input)
        : this(input, traceSink: null, traceLevel: TraceLevel.Normal)
    {
    }

    public Lexer(string input, TextWriter? traceSink, TraceLevel traceLevel)
        : this(input, startPosition: 0, endPosition: (input ?? throw new ArgumentNullException(nameof(input))).Length, traceSink, traceLevel, oneRunePerToken: false)
    {
    }

    // Construct a top-level Lexer with a ParseContext so every Symbol
    // produced during this parse can resolve original-input positions
    // via Symbol.SourceRange and Symbol.SourceText. 
    public Lexer(string input, ParseContext? context, TextWriter? traceSink, TraceLevel traceLevel)
        : this(input, traceSink, traceLevel)
    {
        _context = context;
    }

    // Bounded-range constructor used to build sub-lexers that read a
    // portion of a shared input string. startPosition is the initial
    // read cursor and endPosition is the exclusive upper bound (IsEof
    // fires when _position reaches endPosition). Tokens still carry
    // absolute offsets into the shared string so the outer parse's
    // error-position reporting works uniformly whether positions come
    // from the main lexer or a sub-lexer.
    //
    // oneRunePerToken switches Read to walk one rune at a time instead
    // of one grapheme cluster. WithinTokenRule passes true so the
    // inner rule sees the runes inside the outer token; everywhere
    // else the default (false) keeps grapheme tokenization.
    internal Lexer(string input, int startPosition, int endPosition, TextWriter? traceSink, TraceLevel traceLevel, bool oneRunePerToken)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if ((uint)startPosition > (uint)input.Length)
            throw new ArgumentOutOfRangeException(nameof(startPosition), startPosition, "startPosition must be in [0, input.Length].");
        if (endPosition < startPosition || endPosition > input.Length)
            throw new ArgumentOutOfRangeException(nameof(endPosition), endPosition, "endPosition must be in [startPosition, input.Length].");
        _oneRunePerToken = oneRunePerToken;
        BindInput(input, startPosition, endPosition, traceSink, traceLevel);
    }

    // The string this lexer reads from. For a top-level lexer this is
    // the input the caller passed to Parse. For a sub-lexer (the one
    // WithinTokenRule builds over the runes of one outer token) this is
    // the substring covering just those runes; the sub-lexer's
    // Position, IsEof, DeepestFailure, and Read() / Token offsets are
    // all expressed in coordinates of this string. Rule code can bound
    // its own loops on Input.Length safely either way: the lexer's
    // readable range and Input.Length are always the same string.
    public string Input => _input;
    public int Position => _position;

    // The position of the failure that would be reported if the parse
    // ended now. See docs/ErrorArchitecture.md for how forced, named, and
    // mechanical failures are ranked.
    public int DeepestFailure =>
        _failureState.ForcedMessage != null ? _failureState.ForcedPosition
        : _failureState.NamedMessage != null
            && _failureState.NamedPosition >= _failureState.MechanicalPosition
          ? _failureState.NamedPosition
          : _failureState.MechanicalPosition;

    // Direct write-access to the read cursor for alternative
    // evaluator's backtrack-rollback path. For the recursive evaluator,
    // BeginTransaction is the right mechanism. 
    internal void SetPositionUnchecked(int position) => _position = position;

    // Re-bind a previously-used Lexer to a new input string and reset
    // all per-parse state. Lets other evaluators pool one
    // Lexer instance per thread instead of allocating a fresh class
    // per Parse call. Validates the same input bounds the constructor
    // does so a misuse fails rather than producing a corrupt
    // parse. Pooled lexers are always full-input grapheme-mode (no
    // sub-lexer state to roll over), so the rune-per-token mode can't
    // leak between parses.
    //
    // After ResetForReuse, the caller is expected to invoke
    // ConfigureBudgets to set the budget limits and PreserveAllSymbols
    // for the new parse. The reset clears the budget counters so a
    // pooled lexer can't leak rule-invocation count from the previous
    // parse.
    internal void ResetForReuse(string input, TextWriter? traceSink, TraceLevel traceLevel)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        BindInput(input, startPosition: 0, endPosition: input.Length, traceSink, traceLevel);
        _context = null;
        _failureState = default;
        _subtreeDeepestFailure = 0;
        _transactionDepth = 0;
        _ruleInvocations = 0;
        _ruleDepth = 0;
        _ruleCountLimit = 0;
        _maxDepth = 0;
        _timeout = TimeSpan.Zero;
        _stopwatch = null;
        _cancellation = null;
        _budgetParent = null;
        PreserveAllSymbols = false;
    }

    // Set the per-input fields that both the constructor and
    // ResetForReuse have to assign: input string, position bounds,
    // trace destination, and the grapheme-cluster index for the new
    // input. Doesn't touch _oneRunePerToken (readonly, set-once in the
    // constructor) and doesn't reset per-parse counters / budgets
    // (those are zero-initialized for fresh constructions, and
    // ResetForReuse clears them itself).
    [MemberNotNull(nameof(_input), nameof(_graphemeIndex))]
    private void BindInput(string input, int startPosition, int endPosition, TextWriter? traceSink, TraceLevel traceLevel)
    {
        _input = input;
        _position = startPosition;
        _endPosition = endPosition;
        _traceSink = traceSink;
        _traceLevel = traceLevel;
        _graphemeIndex = GraphemeClusterIndex.For(input);
    }

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
            case TraceOutcome.Skipped: _traceSink!.Write("SKIP | "); break;
        }
        _traceSink!.Write(label);
        if (message.Length > 0)
        {
            _traceSink.Write(": ");
            _traceSink.Write(message);
        }
        _traceSink.WriteLine();
    }

    // How many UTF-16 chars are in the next token at `startOffset`.
    // Returns one rune in WithinToken sub-lexer mode, or one grapheme
    // cluster otherwise. Caller has already verified there's at least
    // one char left in the readable range.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int NextTokenLength(int startOffset)
    {
        if (_oneRunePerToken)
        {
            char c = _input[startOffset];
            if (char.IsHighSurrogate(c)
                && startOffset + 1 < _input.Length
                && char.IsLowSurrogate(_input[startOffset + 1]))
            {
                return 2;
            }
            return 1;
        }
        return _graphemeIndex.LengthAt(startOffset);
    }

    // "How long is the next token at this position?" without
    // advancing. Returns 0 if `position` is at or past the end.
    internal int PeekTokenLength(int position)
    {
        if (position >= _endPosition) return 0;
        return NextTokenLength(position);
    }

    // Peek the next token (one grapheme cluster, or one rune in the
    // WithinToken sub-lexer mode) without advancing. Returns an EOF
    // token when the lexer is at end-of-input. The returned Token's
    // Chars span lives over the original input string and stays valid
    // as long as the lexer's input does.
    //
    // Used by OrRule and BetweenInclusiveRule for the lookahead
    // shortcut: peek one token, ask each child rule whether it could
    // match this token. The shortcut runs at the lexer's current
    // cursor position, which the GraphemeClusterIndex has already
    // walked past (every committed Read calls LengthAt before
    // advancing), so PeekToken is a cache-hit O(1) lookup in the
    // common path.
    //
    // Why not BeginTransaction + Read + rollback? Read emits a trace
    // line, mutates _position, bumps _transactionDepth, and rolls
    // back through a using-block disposer. Correct, but too heavy
    // for the per-Or-entry hot path and would clutter trace
    // output with phantom Read lines. PeekToken is the side-effect-
    // free, AggressiveInlining-friendly alternative.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Token PeekToken()
    {
        if (_position >= _endPosition)
            return new Token(_input, _position, 0, isEof: true);
        int length = NextTokenLength(_position);
        return new Token(_input, _position, length, isEof: false);
    }

    // Peek the rune at `pos` in `input` without advancing any lexer
    // state. Writes the rune value and its UTF-16 length. Returns
    // false if the char at `pos` is a stray surrogate without its
    // paired half (malformed UTF-16 that doesn't represent any real
    // Unicode character).
    //
    // `pos` must be a valid index into `input`
    // (0 <= pos < input.Length). Calling at end-of-input throws
    // IndexOutOfRangeException, by design: the false return is
    // reserved for "stray surrogate" so callers don't have to
    // distinguish "EOF" from "malformed input" off one boolean.
    // Callers gate this call with their own EOF check (most live
    // inside `while (_position < _endPosition)` loops; rules that
    // peek at the lookahead position guard with `pos < input.Length`
    // explicitly).
    //
    // Always one rune at a time. Rules that need to walk rune-by-rune
    // (like the WithinToken sub-lexer) get consistent semantics
    // regardless of how the outer lexer is tokenizing. Callers that
    // want to inspect one token at a time in the lexer's natural unit
    // (one grapheme cluster, or one rune in the WithinToken sub-
    // lexer mode) should call lexer.Read(). Note that Read() advances
    // the lexer, so for peek semantics wrap it in an uncommitted
    // transaction:
    //
    //     using var transaction = lexer.BeginTransaction();
    //     var token = lexer.Read();
    //     // inspect token.Memory, token.Chars, etc.
    //     // DON'T call transaction.Commit(). When the `using`
    //     // block exits, Transaction.Dispose sees Commit wasn't
    //     // called and restores the lexer's position to where
    //     // BeginTransaction was called.
    //
    // That's the idiomatic "peek a token" pattern. A rule doing pure
    // lookahead (Peek, Not) wants BeginProbe instead, which also rolls
    // back the failure tracker and the subtree-extent mark.
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

    // Three-slot failure-tracker state (mechanical / named / forced). See
    // docs/ErrorArchitecture.md.
    // Mutable struct: the Lexer holds one of these as `_failureState` and
    // mutates its fields in place. A Probe value-copies it when it opens
    // and writes the copy back on rollback, which is how a lookahead
    // probe's failures get undone (see the Probe struct). Private because
    // rules never touch the failure tracker directly: they go through
    // RecordFailure / RecordCompositeFailure and BeginProbe.
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
    // access legitimate without widening visibility.
    public Transaction BeginTransaction()
    {
        _transactionDepth++;
        return new Transaction(this, _position);
    }

    // Open a lookahead Probe. Unlike a Transaction, a Probe brackets all
    // three pieces of speculative state (read position, the three-slot
    // failure tracker, and the subtree-extent high-water mark) and
    // restores all three on Dispose unless Commit is called. See the Probe
    // struct for the full explanation. A Probe is not a transaction
    // nesting level: it doesn't touch _transactionDepth, so it adds no
    // trace indentation. The rule opening it already owns one transaction
    // (the one Rule.TryParse opened for it), which supplies the single
    // indentation level.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Probe BeginProbe() => new Probe(this);

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

    // Wire this lexer up as a budget sub-lexer of `source`. After this
    // call EnterRuleBudgetChecks / ExitRuleBudgetChecks /
    // TickPeriodicBudget forward to `source`,
    // so the inner work stacks on the outer's counters and MaxDepth /
    // RuleCountLimit / Timeout / Cancellation cover both sides combined.
    // Used by WithinTokenRule; without it, a recursive inner rule on a
    // cluster with many combining marks would crash the host with an
    // uncatchable StackOverflowException. Nests cleanly: a sub-lexer of
    // a sub-lexer chains through to the root that owns the counters.
    internal void InheritBudgetsFrom(Lexer source)
    {
        _budgetParent = source;
    }

    // Budget bookkeeping called by Rule.TryParse on entry to every rule
    // invocation. Two cheap counters plus a periodic deeper check; the
    // real rule-entry machinery (transactions, output lists, dispatch)
    // lives in Rule.TryParse itself.
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
    internal void EnterRuleBudgetChecks()
    {
        if (_budgetParent != null)
        {
            _budgetParent.EnterRuleBudgetChecks();
            return;
        }
        _ruleDepth++;
        if (_maxDepth > 0 && _ruleDepth > _maxDepth)
            throw new ParseBudgetExceeded(ParseOutcome.DepthLimitExceeded);

        _ruleInvocations++;
        if ((_ruleInvocations & BudgetCheckMask) == 0)
            CheckPeriodicBudgets();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ExitRuleBudgetChecks()
    {
        if (_budgetParent != null)
        {
            _budgetParent.ExitRuleBudgetChecks();
            return;
        }
        _ruleDepth--;
    }

    // Depth-passed variant of EnterRuleBudgetChecks for an alternative
    // evaluator that tracks call depth itself rather than through paired
    // EnterRuleBudgetChecks / ExitRuleBudgetChecks. The recursive engine
    // maintains depth in _ruleDepth. An evaluator that already has its
    // own call-depth counter hands the current depth in here directly,
    // which skips the _ruleDepth bookkeeping it doesn't use and still
    // runs the same RuleCountLimit / Timeout / Cancellation periodic
    // checks the recursive path runs.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EnterRuleAtDepthBudgetChecks(int depth)
    {
        if (_budgetParent != null)
        {
            _budgetParent.EnterRuleAtDepthBudgetChecks(depth);
            return;
        }
        if (_maxDepth > 0 && depth > _maxDepth)
            throw new ParseBudgetExceeded(ParseOutcome.DepthLimitExceeded);

        _ruleInvocations++;
        if ((_ruleInvocations & BudgetCheckMask) == 0)
            CheckPeriodicBudgets();
    }

    // Counter-only budget tick for an alternative evaluator, used on
    // steps that do real work but don't enter a cyclic rule (so they
    // don't go through EnterRuleAtDepthBudgetChecks). Skips the depth check. An
    // evaluator that calls this enforces MaxDepth itself on its
    // rule-entry path. The periodic RuleCountLimit / Timeout /
    // Cancellation check fires at the same 1024 boundary the recursive
    // engine uses, so budget aborts share the same trip mechanism.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void TickPeriodicBudget()
    {
        if (_budgetParent != null)
        {
            _budgetParent.TickPeriodicBudget();
            return;
        }
        _ruleInvocations++;
        if ((_ruleInvocations & BudgetCheckMask) == 0)
            CheckPeriodicBudgets();
    }

    // Off the hot path on purpose: only invoked once every
    // BudgetCheckInterval rule invocations, so making it a separate
    // non-inlined method keeps EnterRuleBudgetChecks small enough for the JIT to
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
