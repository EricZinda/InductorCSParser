using System;
using InductorParser.Tracing;

namespace InductorParser.Lexing;

// ScanWhileRule's bulk-consume scanners. AdvanceWhileRuneIn walks one
// rune at a time and checks each against a rune-only TokenSet;
// AdvanceWhileTokenIn walks one grapheme cluster at a time so multi-rune
// entries (CRLF, ZWJ-glued emoji sequences, etc.) can match too.
//
// The look-ahead "skip past stretches that can't start a match"
// scanners (AdvanceUntilRuneIn, AdvanceUntilLiteralCandidateIn) live in
// the StateMachine project. No rule in the recursive evaluator calls
// them.
public sealed partial class Lexer
{
    internal int AdvanceWhileRuneIn(TokenSet set)
    {
        // Set must be rune-only. Multi-rune entries are
        // silently invisible to set.Contains(runeValue) below, so a
        // caller passing a mixed set would walk right past every
        // multi-rune cluster the set was supposed to consume. The
        // multi-rune-aware variant is AdvanceWhileTokenIn, the caller
        // is responsible for dispatching to the right one based on
        // whether the set has multi-rune entries.
        Invariant.That(!set.HasMultiRuneGraphemes,
            "AdvanceWhileRuneIn requires a rune-only set. "
            + "Caller must dispatch to AdvanceWhileTokenIn for sets with multi-rune entries.");

        int count = 0;

        while (_position < _endPosition)
        {
            int tokenLength = NextTokenLength(_position);
            // Sub-lexer edge case: a stray surrogate in the outer input
            // makes the outer cluster one char long, so the sub-lexer's
            // _endPosition can sit between the two halves of a surrogate
            // pair in the underlying string. NextTokenLength reads ahead
            // and reports 2, which would walk past the sub-lexer's
            // bound; stop here.
            if (tokenLength <= 0 || _position + tokenLength > _endPosition)
                break;

            bool inSet;
            if (TryPeekRune(_input, _position, out int runeValue, out int runeLen))
            {
                // A Run-based rule matches only when the whole token is
                // exactly one rune in the set. A multi-rune grapheme
                // whose first rune happens to be in the set isn't part
                // of the run. Under the WithinToken sub-lexer mode every
                // token is one rune, so tokenLength == runeLen still works.
                inSet = tokenLength == runeLen && set.Contains(runeValue);
            }
            else
            {
                // Lone surrogate: a one-char token that isn't a valid
                // Unicode scalar. OneOf matches it against the rune
                // intervals by its UTF-16 code unit under an unnormalized
                // Compile (see TokenSet.ContainsToken's lone-surrogate
                // branch), so the run scanner has to agree to stay
                // equivalent to AtLeast(n, OneOf(set)).
                inSet = tokenLength == 1 && set.Contains((int)_input[_position]);
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

    // Token-aware variant of AdvanceWhileRuneIn. Used when the
    // TokenSet has multi-rune entries: a multi-rune token can be a
    // member of the set, so the loop has to pull a full token per
    // iteration and check it against both halves of the set. Slower
    // per character than AdvanceWhileRuneIn (we pay per-token
    // overhead instead of inline rune decode), but only fires when the
    // grammar actually contains multi-rune set entries. Rune-only sets
    // continue to use AdvanceWhileRuneIn via the rule's dispatch.
    internal int AdvanceWhileTokenIn(TokenSet set)
    {
        int count = 0;
        while (_position < _endPosition)
        {
            int pos = _position;
            int tokenLength = NextTokenLength(pos);
            if (tokenLength <= 0 || pos + tokenLength > _endPosition)
                break;

            // Try the rune fast path first. If the token is a single
            // rune we don't have to hash a span against the multi-rune
            // array. Multi-rune tokens fall through to the grapheme
            // membership check.
            bool inSet;
            if (TryPeekRune(_input, pos, out int runeValue, out int runeLen)
                && tokenLength == runeLen)
            {
                inSet = set.Contains(runeValue);
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
