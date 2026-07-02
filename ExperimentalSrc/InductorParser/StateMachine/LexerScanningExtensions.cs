using System;
using InductorParser.Lexing;
using InductorParser.Tracing;

namespace InductorParser.StateMachine;

// Scanner-skip extension methods the StateMachine uses to bulk-advance
// the Lexer past stretches of input that can't start a real match. They
// use string-level IndexOf / IndexOfAny for vectorized speed and gate
// every byte-level landing through Lexer.IsGraphemeClusterStart so the
// outer parser only ever sees positions it could legally start at.
//
// Lives in the StateMachine assembly because the recursive evaluator
// doesn't call these. Built on Lexer's public/internal surface
// (Input, Position, EndPosition, OneRunePerToken, NextTokenLength,
// SetPositionUnchecked, IsGraphemeClusterStart, TryPeekRune, Trace,
// IsTracing), with no private fields touched.
internal static class LexerScanningExtensions
{
    // AdvanceUntilRuneIn is told "scan forward from Position until you find a place
    // where one of these candidate characters could match." When it returns, Position
    // is either at a real cluster boundary where a candidate sits, or at end-of-input
    // if no such place exists. Used by the scanner-skip optimization to leap past long
    // stretches of input that can't possibly start an inner-rule match.
    //
    // The two parameters carry the SAME candidate set in two forms, not
    // two different sets, and not a set plus a subset:
    //
    //   * `candidates` is authoritative: the complete, rune-only set of
    //     first-runes an inner rule could start with. The slow path
    //     tests membership against it directly. Always required.
    //
    //   * `bmpCandidates` is an optional char[] rendering of that exact
    //     same set, pre-built by the caller via TokenSet.TryGetBmpChars
    //     so the fast path can hand it straight to string.IndexOfAny
    //     (which only accepts char[]). It is all-or-nothing: non-null
    //     only when EVERY member of `candidates` is a single BMP
    //     non-surrogate char and the set is small (<= 256 chars). If
    //     even one candidate is non-BMP or a surrogate code unit, the
    //     caller passes null and the whole set goes through the slow
    //     loop. It is never the BMP-only portion of a larger set.
    //
    // So exactly one path runs, over the full `candidates` set either
    // way. A non-null `bmpCandidates` just selects the vectorized one.
    public static void AdvanceUntilRuneIn(this Lexer lexer, TokenSet candidates, char[]? bmpCandidates)
    {
        // candidates must be rune-only. Multi-rune entries (like the
        // CRLF cluster) are silently invisible to both the bmpCandidates
        // IndexOfAny path and the slow path's candidates.Contains(runeValue)
        // check, so a caller that forgets to flatten its set via
        // TokenSet.LookaheadFirstRunes would see the scanner-skip step
        // right past the multi-rune entries it was supposed to stop at.
        Invariant.That(!candidates.HasMultiRuneGraphemes,
            "AdvanceUntilRuneIn requires a rune-only candidate set. "
            + "Multi-rune cluster entries are not searched and would be silently "
            + "skipped past. Caller must flatten via TokenSet.LookaheadFirstRunes first.");
        if (lexer.IsEof) return;

        string input = lexer.Input;
        int endPosition = lexer.EndPosition;

        // Fast path for the common case: every candidate is a single
        // UTF-16 char in the BMP, so string.IndexOfAny can vectorize the
        // search. Candidates outside that range fall to the slow loop
        // below.
        //
        // IndexOfAny searches by UTF-16 code unit and doesn't know about
        // cluster boundaries. A candidate that can be the second-or-later
        // rune of a multi-rune cluster (LF inside CRLF, ZWJ inside an
        // emoji ZWJ sequence, a combining mark, a variation selector,
        // ...) can land at a non-token-boundary offset. The post-validation
        // below skips those landings.
        if (bmpCandidates is { Length: > 0 })
        {
            int searchStart = lexer.Position;
            while (true)
            {
                int found = input.IndexOfAny(bmpCandidates, searchStart, endPosition - searchStart);
                if (found < 0)
                {
                    lexer.SetPositionUnchecked(endPosition);
                    return;
                }

                if (lexer.IsAtMidToken(found))
                {
                    // Skip this one char and try again since it is in the
                    // interior of a token (mid-grapheme-cluster in the default
                    // mode, the trailing surrogate half in one-rune mode). If
                    // there is another candidate in this same token it will
                    // skip again.
                    searchStart = found + 1;
                    if (searchStart >= endPosition)
                    {
                        lexer.SetPositionUnchecked(endPosition);
                        return;
                    }
                    continue;
                }
                lexer.SetPositionUnchecked(found);
                return;
            }
        }

        // Slow path: walk one cluster at a time, stopping at the first
        // cluster whose leading rune is a candidate. Handles the non-BMP
        // candidates the IndexOfAny path above can't search for.
        while (lexer.Position < endPosition)
        {
            int position = lexer.Position;
            int len = lexer.NextTokenLength(position);
            Invariant.That(len > 0, "NextTokenLength returned <= 0 inside the "
                + "AdvanceUntilRuneIn slow-path loop, which the `Position < EndPosition` "
                + "loop condition already excludes. Scanner would advance zero and loop.");

            bool inSet;
            if (Lexer.TryPeekRune(input, position, out int runeValue, out _))
            {
                inSet = candidates.ContainsRune(runeValue);
            }
            else
            {
                // Lone surrogate at position: not a valid scalar, so
                // TryPeekRune can't decode it. Test the cluster's
                // leading char by its UTF-16 code unit, the way OneOf /
                // WithinToken / TokenSet.ContainsToken match a token
                // that begins with one under an unnormalized Compile.
                //
                // No token-length gate here. This is a lookahead
                // scanner: it stops wherever the cluster's leading char
                // is a candidate, exactly as the TryPeekRune branch
                // above stops on a leading rune without checking that
                // the cluster is a single rune. A `len == 1` gate would
                // skip a multi-char cluster a WithinToken alternative
                // could still match.
                inSet = candidates.ContainsRune((int)input[position]);
            }
            if (inSet)
                return;

            lexer.SetPositionUnchecked(Math.Min(position + len, endPosition));
        }
    }

    // The stronger scanner fast path for
    // ZeroOrMore(Or(literal-choice, AnyToken.Delete)). A plain first-rune
    // skip still stops at every "s" for a case-insensitive "Sherlock"
    // search and then invokes the full parser to reject it. Here we keep
    // consuming the deleted fallback ourselves until the whole literal
    // could match at the current lexer position. The outer loop then calls
    // the real rule, preserving the same tree and capture behavior as the
    // unoptimized parse.
    //
    // Three payload shapes:
    //   * literalPositions != null && literals.Length == 1: single-literal
    //     IndexOf with a per-position cache that amortizes across iterations.
    //   * bmpFirstRunes is non-null: multi-literal IndexOfAny on the shared
    //     first-rune set.
    //   * Neither: slow per-rune walk with firstRunes.Contains gating.
    //
    // All three use byte-level substring search (IndexOf / IndexOfAny) and
    // don't know about cluster boundaries. IsAtMidToken filters mid-cluster
    // landings so AnyLiteralMatchesAt is only invoked at offsets the outer
    // parser could legally start at.
    public static void AdvanceUntilLiteralCandidateIn(
        this Lexer lexer,
        TokenSet firstRunes,
        char[]? bmpFirstRunes,
        LiteralScannerCandidate[] literals,
        int[]? literalPositions)
    {
        if (lexer.IsEof) return;

        string input = lexer.Input;
        int endPosition = lexer.EndPosition;

        if (literalPositions != null && literals.Length == 1)
        {
            while (lexer.Position < endPosition)
            {
                int found = FindNextLiteralCandidate(literals, literalPositions, input, lexer.Position, endPosition);
                if (found < 0)
                {
                    lexer.SetPositionUnchecked(endPosition);
                    return;
                }

                lexer.SetPositionUnchecked(found);
                if (lexer.IsAtMidToken(lexer.Position))
                {
                    // Mid-token landing. Step past the offending char;
                    // next iteration's substring search will either find
                    // another candidate or run out.
                    lexer.SetPositionUnchecked(Math.Min(lexer.Position + 1, endPosition));
                    continue;
                }
                if (AnyLiteralMatchesAt(literals, input, lexer.Position, endPosition))
                    return;

                int len = lexer.NextTokenLength(lexer.Position);
                Invariant.That(len > 0, "NextTokenLength returned <= 0 inside the "
                    + "AdvanceUntilLiteralCandidateIn single-literal loop, which the "
                    + "`Position < EndPosition` loop condition already excludes. Scanner would advance zero and loop.");
                lexer.SetPositionUnchecked(Math.Min(lexer.Position + len, endPosition));
            }
            return;
        }

        if (bmpFirstRunes is { Length: > 0 })
        {
            // Literal alternates still benefit from staying inside the
            // scanner: the real parser is only invoked when a complete
            // literal candidate matches. Use IndexOfAny for the shared
            // first-rune set, then check only plausible literals at the
            // candidate position.
            while (lexer.Position < endPosition)
            {
                int found = input.IndexOfAny(bmpFirstRunes, lexer.Position, endPosition - lexer.Position);
                if (found < 0)
                {
                    lexer.SetPositionUnchecked(endPosition);
                    return;
                }

                lexer.SetPositionUnchecked(found);
                if (lexer.IsAtMidToken(lexer.Position))
                {
                    lexer.SetPositionUnchecked(Math.Min(lexer.Position + 1, endPosition));
                    continue;
                }
                if (AnyLiteralMatchesAt(literals, input, lexer.Position, endPosition))
                    return;

                int len = lexer.NextTokenLength(lexer.Position);
                Invariant.That(len > 0, "NextTokenLength returned <= 0 inside the "
                    + "AdvanceUntilLiteralCandidateIn IndexOfAny loop, which the "
                    + "`Position < EndPosition` loop condition already excludes. Scanner would advance zero and loop.");
                lexer.SetPositionUnchecked(Math.Min(lexer.Position + len, endPosition));
            }
            return;
        }

        while (lexer.Position < endPosition)
        {
            if (Lexer.TryPeekRune(input, lexer.Position, out int runeValue, out _)
                && firstRunes.ContainsRune(runeValue)
                && AnyLiteralMatchesAt(literals, input, lexer.Position, endPosition))
            {
                return;
            }

            // Fall-through path: advance in the lexer's natural token
            // units so we never manufacture a start position inside a
            // grapheme cluster. A full literal match is only useful at
            // positions where the outer parser could legally start.
            int len = lexer.NextTokenLength(lexer.Position);
            Invariant.That(len > 0, "NextTokenLength returned <= 0 inside the "
                + "AdvanceUntilLiteralCandidateIn fall-through loop, which the "
                + "`Position < EndPosition` loop condition already excludes. Scanner would advance zero and loop.");
            lexer.SetPositionUnchecked(Math.Min(lexer.Position + len, endPosition));
        }
    }

    // True when `position` is NOT a valid token-start offset, so the
    // byte-level scanner fast paths in AdvanceUntilRuneIn /
    // AdvanceUntilLiteralCandidateIn must skip the IndexOf / IndexOfAny
    // landing there. Those searches run over UTF-16 code units and can
    // land in the interior of a token; the outer parser only ever starts
    // at token boundaries, so this gate filters the interior hits out.
    //
    // What "interior" means depends on the lexer's token unit:
    //
    //   * Grapheme mode (the default, every top-level parse): a token is
    //     one UAX #29 grapheme cluster. The interior offsets are the
    //     second-or-later runes of a cluster (LF inside CRLF under GB3, a
    //     combining mark, ZWJ inside an emoji ZWJ sequence, a variation
    //     selector, a Hangul jamo continuation, ...). Resolution goes
    //     through Lexer.IsGraphemeClusterStart, the same walk
    //     NextTokenLength uses, so any rule StringInfo respects (including
    //     backward-context rules like GB9c Indic Conjunct Break) is
    //     handled automatically.
    //
    //   * One-rune-per-token mode (the WithinTokenRule sub-lexer): a token
    //     is one rune, so a combining mark or any other cluster
    //     continuation IS its own token and a valid start. The only
    //     interior offset is the trailing half of a surrogate pair. Using
    //     the grapheme-cluster index here would wrongly skip every
    //     mid-cluster rune the sub-lexer can legitimately start at, and
    //     the IndexOfAny fast path would diverge from the per-token slow
    //     path (which walks NextTokenLength and stops at those runes).
    internal static bool IsAtMidToken(this Lexer lexer, int position)
    {
        string input = lexer.Input;
        if (lexer.OneRunePerToken)
            return char.IsLowSurrogate(input[position])
                && position > 0
                && char.IsHighSurrogate(input[position - 1]);
        return !lexer.IsGraphemeClusterStart(position);
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
        int firstRune;
        Lexer.TryPeekRune(input, position, out firstRune, out _);
        for (int index = 0; index < literals.Length; index++)
            if (literals[index].CanStartWith(firstRune)
                && literals[index].MatchesAt(input, position, endPosition))
            {
                return true;
            }
        return false;
    }
}
