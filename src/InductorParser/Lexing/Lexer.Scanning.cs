using System;
using InductorParser.Tracing;

namespace InductorParser.Lexing;

// Bulk-consume scanners exposed to ScanWhileRule and to user-defined
// Rule subclasses. AdvanceWhileRuneIn walks one rune at a time and
// checks each against a rune-only TokenSet; AdvanceWhileTokenIn walks
// one grapheme cluster at a time so multi-rune entries (CRLF, ZWJ-glued
// emoji sequences, etc.) can match too.
public sealed partial class Lexer
{
    /// <summary>
    /// Consume tokens while each one is a single rune in <paramref name="set"/>.
    /// Returns the number of tokens consumed.
    /// </summary>
    /// <remarks>
    /// <paramref name="set"/> must be rune-only. For sets that contain
    /// multi-rune entries (CRLF, ZWJ emoji, etc.), use
    /// <see cref="AdvanceWhileTokenIn"/>. Branch on
    /// <see cref="TokenSet.HasMultiRuneGraphemes"/> to pick.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="set"/> contains a multi-rune grapheme entry.
    /// </exception>
    public int AdvanceWhileRuneIn(TokenSet set)
    {
        if (set.HasMultiRuneGraphemes)
            throw new ArgumentException(
                "AdvanceWhileRuneIn requires a rune-only set. " +
                "Use AdvanceWhileTokenIn for sets with multi-rune entries, " +
                "or branch on TokenSet.HasMultiRuneGraphemes to pick.",
                nameof(set));

        int count = 0;

        while (_position < _endPosition)
        {
            int tokenLength = NextTokenLength(_position);
            // NextTokenLength is bounded by _input.Length; sub-lexers
            // hold a substring copy, so _input.Length == _endPosition
            // and this stays in bounds. Asserted, not assumed.
            Invariant.That(tokenLength >= 1 && _position + tokenLength <= _endPosition,
                $"NextTokenLength returned {tokenLength} at _position={_position} with _endPosition={_endPosition}.");
            // A single rune is at most 2 chars (surrogate pair); longer
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
                // case where StringInfo fuses a stray surrogate with a
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

    /// <summary>
    /// Consume tokens while each one is in <paramref name="set"/>,
    /// matching either rune or multi-rune grapheme entries. Returns
    /// the number of tokens consumed.
    /// </summary>
    /// <remarks>
    /// For rune-only sets, <see cref="AdvanceWhileRuneIn"/> is faster
    /// (it can skip the multi-rune membership check). Branch on
    /// <see cref="TokenSet.HasMultiRuneGraphemes"/> to pick.
    /// </remarks>
    public int AdvanceWhileTokenIn(TokenSet set)
    {
        int count = 0;
        while (_position < _endPosition)
        {
            int pos = _position;
            int tokenLength = NextTokenLength(pos);
            // Same bound invariant as AdvanceWhileRuneIn.
            Invariant.That(tokenLength >= 1 && pos + tokenLength <= _endPosition,
                $"NextTokenLength returned {tokenLength} at pos={pos} with _endPosition={_endPosition}.");

            // Try the rune fast path first. If the token is a single
            // rune we don't have to hash a span against the multi-rune
            // array. Multi-rune tokens fall through to the grapheme
            // membership check.
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
                inSet = set.HasMultiRuneGraphemes
                    && set.ContainsToken(_input.AsSpan(pos, tokenLength));
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
