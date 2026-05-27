using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using InductorParser.SyntaxTree;
using InductorParser.Tracing;

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

    // Sub-lexer mode: when true, Read advances one rune at a time
    // instead of one grapheme cluster. Set only by the internal
    // bounded-range constructor used by WithinTokenRule, which
    // creates a sub-lexer over the runes inside one outer token.
    // All public construction paths leave this false (token mode).
    private readonly bool _oneRunePerToken;

    // Per-input cache of UAX #29 grapheme cluster boundaries.
    private GraphemeClusterIndex _graphemeIndex;

    // _failureState, _subtreeDeepestFailure live in Lexer.Failures.cs.
    // _transactionDepth lives in Lexer.Transaction.cs. _traceSink / _traceLevel
    // live in Lexer.Tracing.cs.

    // Per-parse budget tracking (rule count, depth, wall-clock timeout,
    // cancellation). Owned by ParseBudget so the budget concept lives in
    // its own type instead of being woven into the lexer's tokenization
    // state. Lexer wires the throw callback at construction so the throw
    // path can still freeze lexer-side position state onto the exception.
    // See Lexing/ParseBudget.cs for the details. Sub-lexers
    // (WithinTokenRule) delegate to the outer parse's budget via
    // Budget.InheritFrom(outer.Budget).
    private readonly ParseBudget _budget;

    internal ParseBudget Budget => _budget;

    // Debug knob wired in from ParseOptions. Rules that would normally
    // apply parse-time tree-shape optimizations (e.g.
    // Delete-node filtering) consult this flag and skip the optimization
    // when it's set, producing a tree whose shape matches the grammar
    // one-to-one. See ParseOptions.PreserveAllSymbols.
    internal bool PreserveAllSymbols { get; set; }

    // Internal accessors so an alternative evaluator (the StateMachine in
    // ExperimentalSrc/InductorParser/StateMachine/) can implement its own
    // scanner-skip and pooling extension methods without the lexer
    // growing private state into its concerns. The recursive evaluator
    // doesn't call any of these.
    internal int EndPosition => _endPosition;
    internal bool OneRunePerToken => _oneRunePerToken;
    internal bool IsGraphemeClusterStart(int position) => _graphemeIndex.IsClusterStart(position);

    // Per-parse state reset for the pooling extension in the StateMachine
    // project. Zeros the failure tracker, transaction depth, and other
    // per-parse-only fields so a pooled Lexer can't leak state into the
    // next parse. The budget has its own ParseBudget.Reset; this method
    // covers everything else.
    internal void ResetParseState()
    {
        _context = null;
        _failureState = default;
        _subtreeDeepestFailure = 0;
        _transactionDepth = 0;
        PreserveAllSymbols = false;
    }

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
        _budget = new ParseBudget(this);
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

    // Direct write-access to the read cursor for alternative
    // evaluator's backtrack-rollback path. For the recursive evaluator,
    // BeginTransaction is the right mechanism. 
    internal void SetPositionUnchecked(int position) => _position = position;

    // Set the per-input fields that both the constructor and
    // the StateMachine's ResetForReuse extension have to assign: input string, position bounds,
    // trace destination, and the grapheme-cluster index for the new
    // input. Doesn't touch _oneRunePerToken (readonly, set-once in the
    // constructor) and doesn't reset per-parse counters / budgets
    // (those are zero-initialized for fresh constructions, and
    // ResetForReuse clears them itself).
    [MemberNotNull(nameof(_input), nameof(_graphemeIndex))]
    internal void BindInput(string input, int startPosition, int endPosition, TextWriter? traceSink, TraceLevel traceLevel)
    {
        _input = input;
        _position = startPosition;
        _endPosition = endPosition;
        _traceSink = traceSink;
        _traceLevel = traceLevel;
        _graphemeIndex = GraphemeClusterIndex.For(input);
    }

    public bool IsEof => _position >= _endPosition;

    // Tracing methods (IsTracing, Trace, WriteTraceLine) live in Lexer.Tracing.cs.

    // How many UTF-16 chars are in the next token at `startOffset`.
    // Returns one rune in WithinToken sub-lexer mode, or one grapheme
    // cluster otherwise. Caller has already verified there's at least
    // one char left in the readable range.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int NextTokenLength(int startOffset)
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
        Invariant.That(len > 0, $"NextTokenLength returned <= 0 on a non-EOF read at position {_position} (endPosition {_endPosition}). Read would advance zero and loop.");
        Token t = new Token(_input, _position, len, isEof: false);
        _position += len;
        Trace(TraceLevel.Diagnostic, "Lexer.Read", TraceOutcome.Info, $"'{_input.Substring(t.Offset, t.Length)}', Consumed: {_position}");
        return t;
    }

    // Open a Transaction over the current position. Every rule does this
    // on entry: it captures _position so a failure can roll back, bumps
    // _transactionDepth for trace indentation, and saves the
    // subtree-extent window. See the Transaction struct in Lexer.Transaction.cs
    // for the full semantics.
    public Transaction BeginTransaction()
    {
        _transactionDepth++;
        return new Transaction(this, _position);
    }

    // Open a lookahead Probe. Unlike a Transaction, a Probe brackets all
    // three pieces of speculative state (read position, the three-slot
    // failure tracker, and the subtree-extent high-water mark) and
    // restores all three on Dispose unless Commit is called. See the
    // Probe struct in Lexer.Probe.cs for the full explanation. A Probe is
    // not a transaction nesting level: it doesn't touch _transactionDepth,
    // so it adds no trace indentation. The rule opening it already owns
    // one transaction (the one Rule.TryParse opened for it), which
    // supplies the single indentation level.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Probe BeginProbe() => new Probe(this);

    // Wire the per-parse runtime budgets onto the lexer. Called by
    // Rule.Parse right after constructing the lexer and before the first
    // rule fires. Forwards the limits onto the lexer's ParseBudget and
    // picks up PreserveAllSymbols which is a per-parse debug flag that
    // doesn't live on the budget.
    internal void ConfigureBudgets(ParseOptions options)
    {
        _budget.Configure(options);
        PreserveAllSymbols = options.PreserveAllSymbols;
    }

}
