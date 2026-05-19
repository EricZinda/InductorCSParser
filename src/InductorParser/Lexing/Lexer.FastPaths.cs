using System;
using InductorParser.Tracing;

namespace InductorParser.Lexing;

// Scanner-skip fast paths. These methods let rules ask "fast-forward
// to the next position where I might match" without going through
// the per-token Read loop. They use string-level IndexOf / IndexOfAny
// for vectorized speed and gate every byte-level landing through
// GraphemeClusterIndex.IsClusterStart so the outer parser only ever
// sees positions it could legally start at.
//
// Split out of Lexer.cs for readability: the core lexer (Read,
// Position, transactions, budget tracking) is one responsibility,
// and "scanner-skip helpers the rules can opt into" is another. Both
// halves of the partial class share the same private fields
// (_input, _position, _endPosition, _graphemeIndex) and trace
// machinery (Trace, TraceLevel).
//
// All callers come from BetweenInclusiveRule.TryCreateScannerSkip,
// the alternative evaluator's ScanUntil opcode, and
// ScanWhile/ScanUntil rules. None of these methods is on the
// per-Token hot path; they fire once per scanner-skip iteration to
// jump across regions of input that can't possibly start an
// inner-rule match.
public sealed partial class Lexer
{
    // AdvanceUntilRuneIn is told "scan forward from _position until you find a place
    // where one of these candidate characters could match." When it returns, _position
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
    internal void AdvanceUntilRuneIn(TokenSet candidates, char[]? bmpCandidates)
    {
        // candidates must be rune-only. Multi-rune
        // entries (like the CRLF cluster) are silently invisible to
        // both the bmpCandidates IndexOfAny path and the slow path's
        // candidates.Contains(runeValue) check, so a caller that
        // forgets to flatten its set via TokenSet.LookaheadFirstRunes
        // would see the scanner-skip step right past the multi-rune
        // entries it was supposed to stop at.
        if (candidates.HasMultiRuneGraphemes)
            throw new InvalidOperationException(
                "Internal: AdvanceUntilRuneIn requires a rune-only candidate set. " +
                "Multi-rune cluster entries are not searched and would be silently " +
                "skipped past. Flatten via TokenSet.LookaheadFirstRunes first.");
        if (IsEof) return;

        // Fast path for the common case: the candidate set is small
        // and every candidate is a single-UTF-16-char codepoint (the
        // 0x0000-0xFFFF range, what Unicode calls the Basic
        // Multilingual Plane or BMP). string.IndexOfAny only takes char[],
        // so this is the path the runtime can vectorize. Candidates
        // outside that range (surrogate pairs for non-BMP codepoints,
        // multi-rune cluster entries) fall through to the slow loop
        // below, which walks one cluster at a time.
        //
        // IndexOfAny searches by UTF-16 code unit and doesn't know
        // about cluster boundaries. Most candidates a user puts in a
        // OneOf are tokens of themselves, but a candidate that can be
        // the second-or-later rune of a multi-rune cluster (LF inside
        // CRLF, ZWJ inside an emoji ZWJ sequence, a combining mark, a
        // variation selector, etc.) can land at a non-token-boundary
        // offset. The post-validation below skips those landings; on
        // inputs without multi-rune clusters the loop runs exactly
        // once.
        if (bmpCandidates is { Length: > 0 })
        {
            // _position is the lexer's official read cursor; the rest of the parser depends
            // on it always sitting at a real cluster boundary. searchStart is a private
            // "where I'm currently looking" cursor. Keeping them separate is the whole trick:
            // we can let the search wander past mid-cluster false hits without ever committing
            // the lexer to one of those bad offsets.
            int searchStart = _position;
            while (true)
            {
                int found = _input.IndexOfAny(bmpCandidates, searchStart, _endPosition - searchStart);
                if (found < 0)
                {
                    // Not found, move to end of string
                    _position = _endPosition;
                    return;
                }

                if (IsAtMidGraphemeCluster(found))
                {
                    // Skip this one char and try again since it is in the middle
                    // of a grapheme cluster. If there is another in this same cluster
                    // it will skip again
                    searchStart = found + 1;
                    if (searchStart >= _endPosition)
                    {
                        _position = _endPosition;
                        return;
                    }
                    continue;
                }
                _position = found;
                return;
            }
        }

        // Slow path: walk one cluster at a time, stopping at the first
        // cluster whose leading rune is a candidate. Handles the non-BMP
        // candidates the IndexOfAny path above can't search for.
        while (_position < _endPosition)
        {
            int len = NextTokenLength(_position);
            // defensive: NextTokenLength returns 0 only past end-of-input,
            // which the loop's bound check excludes. Force >= 1 to advance.
            if (len <= 0) len = 1;

            bool inSet;
            if (TryPeekRune(_input, _position, out int runeValue, out _))
            {
                inSet = candidates.Contains(runeValue);
            }
            else
            {
                // Lone surrogate at _position: not a valid scalar, so
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
                // could still match. A lone surrogate with a combining
                // mark glued on (UAX #29 GB9) is one such cluster. That
                // gate belongs in AdvanceWhileRuneIn, which is a
                // consume scanner and rejects multi-rune clusters.
                inSet = candidates.Contains((int)_input[_position]);
            }
            if (inSet)
                return;

            _position = Math.Min(_position + len, _endPosition);
        }
    }

    // True when `position` is in the middle of a multi-rune grapheme
    // cluster (UAX #29). The byte-level scanner fast paths in
    // AdvanceUntilRuneIn / AdvanceUntilLiteralCandidateIn use IndexOf /
    // IndexOfAny over UTF-16 code units, which can land on a candidate
    // char that's the second-or-later rune of a cluster (LF inside
    // CRLF under GB3, a combining mark, ZWJ inside an emoji ZWJ
    // sequence, a variation selector, a Hangul jamo continuation, ...
    // anything that GB3-GB13 keeps glued to the previous rune). The
    // outer parser is grapheme-scoped, so it would never start at such
    // an offset; this gate skips them.
    //
    // Resolution goes through GraphemeClusterIndex, which walks the
    // input once and caches every cluster boundary. That's the same
    // walk Lexer.NextTokenLength uses, so any UAX #29 rule
    // StringInfo respects (including backward-context rules like
    // GB9c Indic Conjunct Break) is handled correctly by construction.
    private bool IsAtMidGraphemeCluster(int position) =>
        !_graphemeIndex.IsClusterStart(position);

    internal int AdvanceWhileRuneIn(TokenSet set)
    {
        // Set must be rune-only. Multi-rune entries are
        // silently invisible to set.Contains(runeValue) below, so a
        // caller passing a mixed set would walk right past every
        // multi-rune cluster the set was supposed to consume. The
        // multi-rune-aware variant is AdvanceWhileTokenIn — the caller
        // is responsible for dispatching to the right one based on
        // whether the set has multi-rune entries.
        if (set.HasMultiRuneGraphemes)
            throw new InvalidOperationException(
                "Internal: AdvanceWhileRuneIn requires a rune-only set. " +
                "Use AdvanceWhileTokenIn for sets with multi-rune entries.");

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

    internal void AdvanceUntilLiteralCandidateIn(
        TokenSet firstRunes,
        char[]? bmpFirstRunes,
        LiteralScannerCandidate[] literals,
        int[]? literalPositions)
    {
        if (IsEof) return;

        // The stronger scanner fast path for
        // ZeroOrMore(Or(literal-choice, AnyToken.Delete)). A plain
        // first-rune skip still stops at every "s" for a case-insensitive
        // "Sherlock" search and then invokes the full parser to reject
        // it. Here we keep consuming the deleted fallback ourselves
        // until the whole literal could match at the current lexer
        // position. The outer loop then calls the real rule, preserving
        // the same tree and capture behavior as the unoptimized parse.
        //
        // Single-literal only. The runtime's optimized substring search
        // jumps straight to the next full-literal candidate instead of
        // stopping at every matching first character. Multi-literal
        // alternates use the IndexOfAny path below instead: the cached
        // path is much slower for them, because the BCL's IndexOfAny is
        // SIMD-tuned for "any of these chars" while N separate IndexOf
        // calls are not.
        //
        // Both fast paths use byte-level substring search (IndexOf /
        // IndexOfAny) and don't know about cluster boundaries. The one
        // realistic mid-cluster landing is the LF of a CRLF cluster
        // when the literal starts with '\n' (or '\n' is in the first-
        // rune set). The IsLfInsideCrlf gate skips those landings so
        // AnyLiteralMatchesAt is only invoked at offsets the outer
        // parser could legally start at.
        if (literalPositions != null && literals.Length == 1)
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
                if (IsAtMidGraphemeCluster(_position))
                {
                    // Mid-cluster landing. Step past the offending
                    // char; next iteration's substring search will
                    // either find another candidate or run out.
                    _position = Math.Min(_position + 1, _endPosition);
                    continue;
                }
                if (AnyLiteralMatchesAt(literals, _input, _position, _endPosition))
                    return;

                int len = NextTokenLength(_position);
                // defensive: NextTokenLength returns 0 only past end-of-input,
                // which the loop's bound check excludes. Force >= 1 to advance.
                if (len <= 0) len = 1;
                _position = Math.Min(_position + len, _endPosition);
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
            while (_position < _endPosition)
            {
                int found = _input.IndexOfAny(bmpFirstRunes, _position, _endPosition - _position);
                if (found < 0)
                {
                    _position = _endPosition;
                    return;
                }

                _position = found;
                if (IsAtMidGraphemeCluster(_position))
                {
                    _position = Math.Min(_position + 1, _endPosition);
                    continue;
                }
                if (AnyLiteralMatchesAt(literals, _input, _position, _endPosition))
                    return;

                int len = NextTokenLength(_position);
                // defensive: NextTokenLength returns 0 only past end-of-input,
                // which the loop's bound check excludes. Force >= 1 to advance.
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

            // Fall-through path: advance in the lexer's natural token
            // units so we never manufacture a start position inside a
            // grapheme cluster. A full literal match is only useful at
            // positions where the outer parser could legally start.
            int len = NextTokenLength(_position);
            // defensive: NextTokenLength returns 0 only past end-of-input,
            // which the loop's bound check excludes. Force >= 1 to advance.
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
        // scanner prefilter under the exact same rule: only A-Z/a-z match
        // case-insensitively, and every other rune has to match by exact code
        // point. This prevents the optimization from accepting full-Unicode
        // case-insensitive candidates that the real rule would reject.
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
