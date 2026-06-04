using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using InductorParser.SyntaxTree;
using InductorParser.Tracing;

namespace InductorParser.Lexing;

/// <summary>
/// Tokenizes a string one grapheme cluster at a time.
/// </summary>
/// <remarks>
/// <para>
/// Cluster boundaries come from the GraphemeClusterIndex on the input string. One
/// sub-lexer mode, selected by an internal constructor and used only by
/// WithinTokenRule, walks one rune per token instead. That sub-lexer reads a
/// bounded range of the same shared input string and lets the inner rule walk the
/// runes inside one outer token.
/// </para>
/// <para>
/// Malformed UTF-16: stray surrogates (a high surrogate without a paired low,
/// or a low surrogate in any position) are walked one char at a time in both
/// modes and never throw. In rune mode the surrogate-pair check returns 1 for
/// any stray by construction; in grapheme mode the walk delegates to
/// StringInfo, which treats unpaired surrogates as 1-char text elements. Read
/// produces a one-char token over the stray and the read cursor keeps moving.
/// Deciding whether each position represents a valid Unicode character is left
/// to rune-level callers: TryPeekRune returns false on a stray, and rules that
/// decode runes (LiteralRule, TokenSet membership, etc.) handle the false case
/// explicitly.
/// </para>
/// </remarks>
public sealed partial class Lexer
{
    // "Alternative evaluator" (used in several comments below): any parser
    // implementation built on this Lexer other than the recursive evaluator
    // (Rule.TryParse). These reach into internal hooks like BindInput,
    // ResetParseState, SetPositionUnchecked, and the EndPosition /
    // OneRunePerToken / IsGraphemeClusterStart accessors that the recursive
    // evaluator doesn't use.

    // Per-input cache of UAX #29 grapheme cluster boundaries.
    private GraphemeClusterIndex _graphemeIndex;

    // _input, _endPosition, _traceSink, _traceLevel are conceptually readonly
    // but lose the C# `readonly` keyword so an alternative evaluator's pooling
    // extension can re-bind a pooled Lexer to a new input via BindInput()
    // instead of allocating a fresh instance per parse. Constructors still
    // treat them as set-once.
    //
    // _input is the one reference kept to the input string, which is immutable
    // and shared by every Token and ReadOnlySpan<char> the parser hands out.
    // The GC sees this one string object and tracks it. Everything else is
    // stack-resident structs that point back into this string, so the GC never
    // has to track or reclaim them.
    private string _input;

    /// <summary>The input this lexer reads from.</summary>
    /// <remarks>
    /// For a top-level lexer this is what the caller passed to Parse. For a
    /// sub-lexer (the one WithinTokenRule builds over the runes of one outer
    /// token) this is the substring covering just those runes; the sub-lexer's
    /// Position, IsEof, DeepestFailurePosition, and Read / Token offsets are all
    /// expressed in coordinates of this string. Rule code can bound its own
    /// loops on Input.Length safely either way: the lexer's readable range and
    /// Input.Length are always the same string.
    /// </remarks>
    public string Input => _input;

    private int _position;

    /// <summary>The current read cursor as a UTF-16 offset into <see cref="Input"/>.</summary>
    public int Position => _position;

    // Exclusive upper bound on _position. Defaults to _input.Length. Sub-lexer
    // constructors bound this to a lower value so WithinToken can run inner
    // rules over part of the shared string without allocating a Substring.
    // Tokens still carry absolute offsets into _input, so error positions
    // don't need translation.
    private int _endPosition;

    internal int EndPosition => _endPosition;

    /// <summary>True when the read cursor has reached the end of the readable range.</summary>
    public bool IsEof => _position >= _endPosition;

    // Sub-lexer mode: when true, Read advances one rune at a time instead of
    // one grapheme cluster. Set only by the internal constructor
    // used by WithinTokenRule, which creates a sub-lexer over the runes inside
    // one outer token. All public construction paths leave this false.
    private readonly bool _oneRunePerToken;

    internal bool OneRunePerToken => _oneRunePerToken;

    // Also null for sub-lexers (WithinTokenRule) and for pooled Lexer reuse
    // before the pooling extension re-binds it.
    private ParseContext? _context;

    /// <summary>
    /// The per-parse <see cref="ParseContext"/> supplied at construction so
    /// every Symbol the engine builds can resolve back to original-input
    /// positions and text. Null when no context was supplied.
    /// </summary>
    public ParseContext? Context => _context;

    // Per-parse budget tracking (rule count, depth, wall-clock timeout,
    // cancellation). Lexer wires the throw callback at construction so the
    // throw path can freeze lexer-side position state onto the exception. See
    // Lexing/ParseBudget.cs for the details. Sub-lexers (WithinTokenRule)
    // delegate to the outer parse's budget via Budget.InheritFrom(outer.Budget).
    private readonly ParseBudget _budget;

    internal ParseBudget Budget => _budget;

    /// <summary>
    /// Make this lexer's parse budget delegate every rule-entry, rule-exit,
    /// and periodic tick to <paramref name="parent"/>'s budget, so work done
    /// on this lexer counts on top of the parent's current depth and limits.
    /// A user-defined Rule that runs an inner rule against a sub-lexer (the
    /// way the built-in WithinToken does) calls this so the inner recursion
    /// can't spend a fresh MaxDepth on top of the outer parse's depth, and so
    /// a Timeout or Cancellation observed on either lexer trips both.
    /// </summary>
    public void InheritBudgetFrom(Lexer parent) => _budget.InheritFrom(parent._budget);

    /// <summary>
    /// Tick the parse budget once. A user-defined Rule subclass whose
    /// TryParseRule scans many tokens in a single invocation should call
    /// this once per iteration of its inner loop, the same way the
    /// built-in ScanWhile and ScanUntil rules do. Without an inner tick
    /// the budget's periodic check only runs on rule entry, so Timeout,
    /// Cancellation, and RuleCountLimit can't observe the per-iteration
    /// work.
    /// </summary>
    /// <remarks>
    /// The check is amortized to one read every 1024 ticks. The per-call
    /// cost is one int increment and one mask compare.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void TickBudget() => _budget.TickPeriodic();

    // Debug knob wired in from ParseOptions. Rules that would normally apply
    // parse-time tree-shape optimizations (e.g. Delete-node filtering) consult
    // this flag and skip the optimization when it's set, producing a tree
    // whose shape matches the grammar one-to-one. See ParseOptions.PreserveAllSymbols.
    internal bool PreserveAllSymbols { get; set; }

    /// <summary>Create a lexer over <paramref name="input"/>.</summary>
    /// <param name="input">The string to tokenize.</param>
    /// <param name="context">Per-parse <see cref="ParseContext"/>, or null when Symbol-level position recovery isn't needed.</param>
    /// <param name="traceSink">Destination for trace output, or null to disable tracing.</param>
    /// <param name="traceLevel">Verbosity when tracing is enabled.</param>
    public Lexer(
        string input,
        ParseContext? context = null,
        TextWriter? traceSink = null,
        TraceLevel traceLevel = TraceLevel.Normal)
        : this(input, startPosition: 0, endPosition: (input ?? throw new ArgumentNullException(nameof(input))).Length, traceSink, traceLevel, oneRunePerToken: false)
    {
        _context = context;
    }

    /// <summary>
    /// Create a lexer over the whole of <paramref name="input"/>, choosing
    /// whether Read walks one rune at a time (<paramref name="oneRunePerToken"/>
    /// true) instead of one grapheme cluster. A user-defined Rule that needs
    /// to run an inner rule against the runes inside a single token (the way
    /// the built-in WithinToken does) builds a sub-lexer this way over the
    /// token's text, then calls <see cref="InheritBudgetFrom"/> to fold the
    /// inner work into the outer parse's budget.
    /// </summary>
    public Lexer(string input, bool oneRunePerToken)
        : this(input, startPosition: 0,
               endPosition: (input ?? throw new ArgumentNullException(nameof(input))).Length,
               traceSink: null, traceLevel: TraceLevel.Normal, oneRunePerToken: oneRunePerToken)
    {
    }

    // Constructor used to build sub-lexers that read only a portion
    // of a shared input string. startPosition is the initial read cursor and
    // endPosition is the exclusive upper bound (IsEof fires when _position
    // reaches endPosition). Tokens still carry absolute offsets into the
    // shared string so the outer parse's error-position reporting works
    // uniformly whether positions come from the main lexer or a sub-lexer.
    //
    // oneRunePerToken switches Read to walk one rune at a time instead of one
    // grapheme cluster. WithinTokenRule passes true so its inner rule can see the
    // runes inside the outer token. Everywhere else the default (false) uses
    // grapheme tokenization.
    internal Lexer(string input, int startPosition, int endPosition, TextWriter? traceSink, TraceLevel traceLevel, bool oneRunePerToken)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        // uint cast: negatives wrap to huge values, so one compare catches < 0 and > Length.
        if ((uint)startPosition > (uint)input.Length)
            throw new ArgumentOutOfRangeException(nameof(startPosition), startPosition, "startPosition must be in [0, input.Length].");
        if (endPosition < startPosition || endPosition > input.Length)
            throw new ArgumentOutOfRangeException(nameof(endPosition), endPosition, "endPosition must be in [startPosition, input.Length].");
        _oneRunePerToken = oneRunePerToken;
        _budget = new ParseBudget(this);
        BindInput(input, startPosition, endPosition, traceSink, traceLevel);
    }

    // Set the per-input fields that both the constructor and an alternative
    // evaluator's pooling extension have to assign: input string, position
    // bounds, trace destination, and the grapheme-cluster index for the new
    // input. Doesn't touch _oneRunePerToken (readonly, set-once in the
    // constructor) and doesn't reset per-parse counters or budgets (those are
    // zero-initialized for fresh constructions, and the pooling extension
    // clears them itself).
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

    // Per-parse state reset for an alternative evaluator's pooling extension.
    // Zeros the failure tracker, transaction depth, and other per-parse-only
    // fields so a pooled Lexer can't leak state into the next parse. The
    // budget has its own ParseBudget.Reset. This method covers everything
    // else.
    internal void ResetParseState()
    {
        _context = null;
        _failureState = default;
        _subtreeDeepestFailure = 0;
        _transactionDepth = 0;
        PreserveAllSymbols = false;
    }

    // Exposes the cached grapheme-cluster boundaries for a string, used only by alternative evaluators.
    internal bool IsGraphemeClusterStart(int position) => _graphemeIndex.IsClusterStart(position);

    /// <summary>
    /// Move the read cursor to <paramref name="position"/>. Two invariants are
    /// enforced so a user-defined scanning Rule that walks the cursor by hand
    /// (the way the built-in ScanUntil does, jumping past a token whose length
    /// it already peeked) can't put the lexer into a state a later Read would
    /// choke on:
    /// <list type="bullet">
    /// <item>It must lie in the readable range [0, <see cref="EndPosition"/>],
    /// or this throws <see cref="System.ArgumentOutOfRangeException"/>.</item>
    /// <item>It must sit on a token boundary: a grapheme-cluster boundary in
    /// normal mode, or between runes (never inside a surrogate pair) in the
    /// WithinToken sub-lexer's rune mode. A mid-token offset throws
    /// <see cref="System.ArgumentException"/> here, rather than surfacing later
    /// as an opaque failure when the next Read tries to size a token at it.</item>
    /// </list>
    /// Positions taken from a prior <see cref="Read"/> or computed as
    /// <c>cursor + PeekTokenLength(cursor)</c> always satisfy both.
    /// </summary>
    /// <remarks>
    /// This moves only the read cursor. It is safe with respect to the two
    /// pieces of parse state a rule author might worry about:
    /// <list type="bullet">
    /// <item><b>Error tracking</b> is unaffected. RecordFailure /
    /// RecordCompositeFailure record at the explicit position the rule passes,
    /// not at the live cursor, and the deepest-failure mark is a max-only
    /// high-water value. A transaction restores the cursor (and that mark) by
    /// value on rollback, so a jump that's later rolled back leaves no trace in
    /// the failure state. Because the cursor only ever holds an in-range
    /// token-boundary offset (the two checks above), a failure a rule records
    /// at <see cref="Position"/> afterward is always a real source offset the
    /// error machinery can map back.</item>
    /// <item><b>Tracing</b> is unaffected but not automatic. Trace output is
    /// diagnostic logging keyed on transaction depth and validated token
    /// offsets, so a cursor move can't corrupt it, but unlike <see cref="Read"/>
    /// SetPosition emits no trace line. A rule that advances the cursor over a span by
    /// hand should emit its own summary via the Rule.TraceSuccess /
    /// TraceFailure helpers (as the built-in ScanWhile / ScanUntil do), or that
    /// span won't appear in the trace.</item>
    /// </list>
    /// </remarks>
    public void SetPosition(int position)
    {
        // uint cast: negatives wrap to huge values, so one compare catches
        // both < 0 and > EndPosition.
        if ((uint)position > (uint)_endPosition)
            throw new ArgumentOutOfRangeException(nameof(position), position,
                $"position must be in [0, {_endPosition}] (the lexer's readable range).");
        if (!IsTokenBoundary(position))
            throw new ArgumentException(
                $"position {position} is not a token boundary. The read cursor can only sit "
                + (_oneRunePerToken
                    ? "between runes, not inside a surrogate pair. "
                    : "on a grapheme-cluster boundary. ")
                + "Compute it from a prior Read or from cursor + PeekTokenLength(cursor).",
                nameof(position));
        _position = position;
    }

    // True when `position` is a place the lexer could legitimately stop reading:
    // the start, the end, or a token boundary in the current tokenization mode.
    // Mirrors how Read sizes tokens (one grapheme cluster, or one rune in the
    // sub-lexer mode), so any boundary this accepts is one Read can resume from.
    private bool IsTokenBoundary(int position)
    {
        if (position <= 0 || position >= _endPosition) return true;
        if (_oneRunePerToken)
            // Mid-pair only when a low surrogate at `position` follows its high
            // surrogate. A stray (unpaired) surrogate is its own one-char token,
            // so a position on one is a valid boundary.
            return !(char.IsHighSurrogate(_input[position - 1]) && char.IsLowSurrogate(_input[position]));
        return _graphemeIndex.IsClusterStart(position);
    }

    // Direct write-access to the read cursor with no bounds or transaction
    // bookkeeping, for an alternative evaluator's backtrack-rollback path
    // where the position came from a prior checkpoint and is known good.
    // User-defined rules use the bounds-checked SetPosition above instead.
    internal void SetPositionUnchecked(int position) => _position = position;

    // Apply per-parse ParseOptions to the lexer. Called by Rule.Parse right
    // after constructing the lexer and before the first rule fires. Forwards
    // budget limits to ParseBudget and pulls out PreserveAllSymbols, a debug
    // flag that doesn't live on the budget.
    internal void ConfigureOptions(ParseOptions options)
    {
        _budget.Configure(options);
        PreserveAllSymbols = options.PreserveAllSymbols;
    }

    // How many UTF-16 chars are in the next token at `startOffset`. Returns
    // one rune in WithinToken sub-lexer mode, or one grapheme cluster
    // otherwise. Caller has verified there's at least one char left in the
    // readable range.
    //
    // Invariants: always returns >= 1 and never throws. In rune mode, returns
    // 2 only for a well-formed surrogate pair (high then low); stray surrogates
    // in any position return 1. See the class doc for the full malformed-UTF-16
    // rules.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int NextTokenLength(int startOffset)
    {
        Invariant.That(startOffset < _endPosition, $"NextTokenLength called with startOffset={startOffset} at or past endPosition={_endPosition}.");
        if (_oneRunePerToken)
            return RuneHelpers.IsSurrogatePairAt(_input, startOffset) ? 2 : 1;
        return _graphemeIndex.LengthAt(startOffset);
    }

    // "How long is the next token at this position?" without advancing.
    // Returns 0 if `position` is at or past the end. Throws when the
    // position is negative or sits inside a token, matching the boundary
    // rules SetPosition enforces. See the class doc for the full
    // malformed-UTF-16 rules.
    public int PeekTokenLength(int position)
    {
        if (position < 0)
            throw new ArgumentOutOfRangeException(nameof(position), position,
                "position must be non-negative.");
        if (position >= _endPosition) return 0;
        if (!IsTokenBoundary(position))
            throw new ArgumentException(
                $"position {position} is not a token boundary. PeekTokenLength can only measure "
                + (_oneRunePerToken
                    ? "from between runes, not inside a surrogate pair."
                    : "from a grapheme-cluster boundary."),
                nameof(position));
        return NextTokenLength(position);
    }

    // Decode the rune at `pos` in `input` without advancing any lexer state.
    // Writes the rune value and its UTF-16 length. Returns false on a stray
    // surrogate (see the class doc for the malformed-UTF-16 rules).
    //
    // `pos` must be a valid index (0 <= pos < input.Length). The false return
    // is reserved for "stray surrogate" so callers don't have to distinguish
    // EOF from malformed input off one bool. Callers check EOF themselves.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryPeekRune(string input, int pos, out int runeValue, out int runeLen)
    {
        if (input == null)
            throw new ArgumentNullException(nameof(input));
        if ((uint)pos >= (uint)input.Length)
            throw new ArgumentOutOfRangeException(nameof(pos), pos,
                $"pos must be in [0, input.Length={input.Length}).");

        if (RuneHelpers.IsSurrogatePairAt(input, pos))
        {
            runeValue = char.ConvertToUtf32(input[pos], input[pos + 1]);
            runeLen = 2;
            return true;
        }
        char c0 = input[pos];
        if (char.IsSurrogate(c0))
        {
            // Stray surrogate.
            runeValue = -1;
            runeLen = 0;
            return false;
        }
        runeValue = c0;
        runeLen = 1;
        return true;
    }

    /// <summary>
    /// Advance one token and return it. The token shape is one grapheme
    /// cluster in normal mode, or one rune in the WithinToken sub-lexer mode.
    /// </summary>
    /// <remarks>
    /// Cheap: <see cref="Token"/> is a stack-only ref struct carrying offset
    /// and length into the input string, with no allocation and no substring
    /// copying. On malformed UTF-16 (stray surrogates), Read advances by one
    /// char and returns a one-char token rather than throwing. See the class
    /// doc for the full rules.
    /// </remarks>
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
        // The token text is spliced raw here; TraceInterpolatedStringHandler
        // routes every interpolation hole through DisplayEscape, so control /
        // line-separator chars (CRLF, Token('\n').Preserve(), and so on) can't
        // break this line apart. Keeping the escape in the handler instead of
        // here means no trace caller has to remember it.
        Trace(TraceLevel.Diagnostic, "Lexer.Read", TraceOutcome.Info, $"'{_input.Substring(t.Offset, t.Length)}', Consumed: {_position}");
        return t;
    }

    /// <summary>
    /// Open a <see cref="Transaction"/> over the current position. Every
    /// rule opens one on entry (via Rule.TryParse): commit keeps the cursor
    /// where children advanced it, rollback restores the cursor to where
    /// the rule was entered.
    /// </summary>
    /// <remarks>
    /// Rollback restores the cursor but keeps any failures recorded during
    /// the rule: a rejected Or branch or a stopped Count iteration is real
    /// evidence about the input and the error reporter uses it. For pure
    /// lookahead that has to discard the failures too, use
    /// <see cref="Probe"/>. See <see cref="Transaction"/> for the full
    /// semantics.
    /// </remarks>
    public Transaction BeginTransaction()
    {
        _transactionDepth++;
        return new Transaction(this, _position);
    }

    /// <summary>
    /// Open a lookahead <see cref="Probe"/>. Used by rules that do pure
    /// lookahead (Peek, Not, ScanUntil's stopper and escape-start probes):
    /// excursions whose failures are off the real parse path and can't
    /// influence the error report.
    /// </summary>
    /// <remarks>
    /// Where <see cref="Transaction"/> rollback restores only the cursor
    /// and keeps recorded failures, Probe rollback restores the cursor AND
    /// the failure tracker AND the subtree-extent mark, so a probed
    /// excursion that fails leaves no trace in the error reporter. Probes
    /// open inside an existing transaction (Rule.TryParse already opened
    /// one), so a Probe doesn't add a trace-indentation level. See
    /// <see cref="Probe"/> for the full semantics.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Probe BeginProbe() => new Probe(this);
}
