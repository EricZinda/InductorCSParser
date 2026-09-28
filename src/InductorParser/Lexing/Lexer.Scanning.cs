using System;
using InductorParser.Tracing;

namespace InductorParser.Lexing;

// Bulk-consume scanner exposed to ScanWhileRule and to user-defined Rule
// subclasses. The public AdvanceWhileIn picks between two internal loops:
// AdvanceWhileRuneIn walks one rune at a time and checks each against a
// rune-only TokenSet, and AdvanceWhileTokenIn walks one grapheme cluster
// at a time so multi-rune entries (CRLF, ZWJ-glued emoji sequences, etc.)
// can match too.
public sealed partial class Lexer
{
    /// <summary>
    /// Consume tokens while each one is in <paramref name="set"/>, and return the number of
    /// tokens consumed. Stops at the first token that isn't in the set, or at the end of the
    /// input, and leaves the cursor there. This is the bulk scanner behind
    /// <see cref="InductorParser.Rules.ScanWhile(InductorParser.TokenSet,System.Int32)">Rules.ScanWhile</see>,
    /// and a user-defined Rule that consumes a sequence of tokens can call it instead of looping over
    /// <see cref="Read">Lexer.Read()</see>.
    /// </summary>
    /// <remarks>
    /// Works for any set. A set with multi-rune entries (CRLF, a ZWJ emoji sequence) matches whole
    /// grapheme clusters against those entries, and a rune-only set takes a faster path that skips
    /// the multi-rune membership check. The parse budget is ticked once per token, so
    /// <see cref="ParseOptions.Timeout">ParseOptions.Timeout</see> and the other limits can fire
    /// mid-scan.
    /// </remarks>
    public int AdvanceWhileIn(TokenSet set) =>
        set.HasMultiRuneGraphemes ? AdvanceWhileTokenIn(set) : AdvanceWhileRuneIn(set);

    // The rune-only loop behind AdvanceWhileIn. Consumes tokens while each
    // one is a single rune in `set` and returns the count. The set must be
    // rune-only: AdvanceWhileIn routes sets with multi-rune entries to
    // AdvanceWhileTokenIn, and nothing else calls this.
    internal int AdvanceWhileRuneIn(TokenSet set)
    {
        Invariant.That(!set.HasMultiRuneGraphemes,
            $"AdvanceWhileRuneIn was handed a set with multi-rune entries: {set}");

        int count = 0;

        while (_position < _endPosition)
        {
            // One iteration is work the budget should see. The whole
            // scan is one rule invocation from the engine's view, so
            // without this tick Timeout / Cancellation / RuleCountLimit
            // can't fire until the scan returns. The budget's own
            // 1024-mask amortizes the actual checks.
            _budget.TickPeriodic();

            int tokenLength = NextTokenLength(_position);
            // NextTokenLength is bounded by _input.Length. Sub-lexers
            // hold a substring copy, so _input.Length == _endPosition
            // and this stays in bounds. Asserted, not assumed.
            Invariant.That(tokenLength >= 1 && _position + tokenLength <= _endPosition,
                $"NextTokenLength returned {tokenLength} at _position={_position} with _endPosition={_endPosition}.");
            // A single rune is at most 2 chars (surrogate pair). Longer
            // means a multi-rune cluster, which a rune-only run can't match.
            if (tokenLength > 2)
                break;

            bool inSet;
            if (TryPeekRune(_input, _position, out int runeValue, out int runeLen))
            {
                // tokenLength == runeLen rejects 2-char multi-rune
                // clusters (CRLF, base + combining mark): TryPeekRune
                // reports just the first rune, so runeLen=1 but
                // tokenLength=2.
                inSet = tokenLength == runeLen && set.ContainsRune(runeValue);
            }
            else
            {
                // Lone surrogate: a 1-char token whose UTF-16 code unit
                // can still be a set member if the set's intervals cover
                // it. tokenLength == 1 excludes the normal-grapheme-mode
                // case where the segmenter fuses a stray surrogate with a
                // following extender (combining mark, ZWJ) into one cluster.
                inSet = tokenLength == 1 && set.ContainsRune((int)_input[_position]);
            }
            if (!inSet)
                break;

            int consumedFrom = _position;
            _position += tokenLength;
            count++;
            Trace(TraceLevel.Diagnostic, "Lexer.AdvanceWhileRuneIn", TraceOutcome.Info,
                $"'{_input.Substring(consumedFrom, tokenLength)}', Consumed: {_position}");
        }
        return count;
    }

    // The grapheme-cluster loop behind AdvanceWhileIn. Consumes tokens while
    // each one is in `set`, matching either rune or multi-rune grapheme
    // entries, and returns the count. Works for any set, but AdvanceWhileIn
    // sends rune-only sets to AdvanceWhileRuneIn, which skips the multi-rune
    // membership check.
    internal int AdvanceWhileTokenIn(TokenSet set)
    {
        int count = 0;
        while (_position < _endPosition)
        {
            // See AdvanceWhileRuneIn for why every iteration ticks.
            _budget.TickPeriodic();

            int pos = _position;
            int tokenLength = NextTokenLength(pos);
            // Same bound invariant as AdvanceWhileRuneIn.
            Invariant.That(tokenLength >= 1 && pos + tokenLength <= _endPosition,
                $"NextTokenLength returned {tokenLength} at pos={pos} with _endPosition={_endPosition}.");

            // Try the rune fast path first. If the token is a single
            // rune we don't have to hash a span against the multi-rune
            // array. Multi-rune tokens and lone surrogates fall through
            // to the grapheme membership check.
            // tokenLength == runeLen rejects 2-char multi-rune
            // clusters (CRLF, base + combining mark): TryPeekRune
            // reports just the first rune, so runeLen=1 but
            // tokenLength=2.
            bool inSet;
            if (TryPeekRune(_input, pos, out int runeValue, out int runeLen)
                && tokenLength == runeLen)
            {
                inSet = set.ContainsRune(runeValue);
            }
            else
            {
                // ContainsToken handles both a lone surrogate (matched
                // by code unit) and a multi-rune cluster.
                inSet = set.ContainsToken(_input.AsSpan(pos, tokenLength));
            }
            if (!inSet) break;

            _position = pos + tokenLength;
            count++;
            Trace(TraceLevel.Diagnostic, "Lexer.AdvanceWhileTokenIn", TraceOutcome.Info,
                $"'{_input.Substring(pos, tokenLength)}', Consumed: {_position}");
        }
        return count;
    }
}
