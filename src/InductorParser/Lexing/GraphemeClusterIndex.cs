using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace InductorParser.Lexing;

// A per-input cache of UAX #29 grapheme cluster boundaries, populated
// lazily as positions are queried. The single source of truth for
// "where does one user-visible character end and the next begin" in
// this parser. Replaces direct per-call invocations of
// StringInfo.GetNextTextElement, which allocates a substring on each
// call just to read the cluster length.
//
// Runtime caveat: cluster detection delegates to
// System.Globalization.StringInfo, which is UAX #29 rev. 35 compliant
// on .NET 5 and later but uses pre-UAX29 custom logic on .NET
// Framework, .NET Core 3.x, and the Mono runtimes Unity ships. On
// those older runtimes some real grapheme clusters split incorrectly
// (Thai "kam", multi-codepoint emoji like the woman-shrugging
// sequence). Replacing this with a bundled UAX #29 implementation
// would make the behavior uniform across runtimes. Until then, this
// class uses the StringInfo the runtime ships with. Grammars that
// operate on ASCII-only or single-UTF-16-char content (the Setting
// example, most config-file grammars) are unaffected.
//
// Sharing model: GraphemeClusterIndex.For(string) returns a cached
// instance keyed on the input string via a ConditionalWeakTable, so
// the Lexer and post-parse callers (SourcePositionConverter) reuse the
// same cache automatically. The CWT keeps the index alive only while
// the string is alive, so a finished parse drops both together.
//
// The cache is populated by walking a TextElementEnumerator one
// MoveNext at a time, recording each cluster start in a bool[] sized
// to the input. bool[] gives O(1) IsClusterStart lookups; LengthAt
// scans forward by at most one cluster's worth of bool reads after
// the enumerator has been advanced past the position. After the
// enumerator is exhausted (the input has been fully walked once),
// every query becomes pure bool-array work.
//
// Thread safety: the cache is keyed on the input string instance, so
// multiple parses of the same string (interned literals, cached
// config text, identical request bodies in a web server) share one
// index instance. The walk that fills _isStart is locked because
// TextElementEnumerator's MoveNext isn't thread-safe and the bool[]
// updates would race in lockstep. The lock is per-instance (one
// per input string), so concurrent parses of DIFFERENT inputs don't
// contend. The two fast-path reads (the _exhausted check and the
// _walkedTo check at the top of EnsureWalkedTo) skip the lock once
// the walk has reached the requested position, so the steady-state
// cost on a long parse is a pair of volatile reads per Read.
internal sealed class GraphemeClusterIndex
{
    private static readonly ConditionalWeakTable<string, GraphemeClusterIndex> _byInput = new();

    private readonly string _input;
    private readonly bool[] _isStart;
    private readonly object _walkLock = new();
    private TextElementEnumerator? _enumerator;
    private volatile bool _exhausted;
    // Highest position any MoveNext has visited so far. Volatile so a
    // reader can skip the lock when the walk has already reached the
    // requested target. Starts at -1 before the first MoveNext. The
    // volatile write inside the lock orders the _isStart updates that
    // preceded it, so a thread that observes _walkedTo >= target also
    // sees the _isStart writes for every cluster start in [0, _walkedTo].
    private volatile int _walkedTo = -1;

    private GraphemeClusterIndex(string input)
    {
        _input = input;
        _isStart = new bool[input.Length + 1];
        if (input.Length > 0)
            _isStart[0] = true;
        // EOF is always a boundary; pre-mark so callers can ask
        // IsClusterStart(input.Length) without a walk.
        _isStart[input.Length] = true;
    }

    // Get-or-create the cached index for `input`. Same string instance
    // returns the same index. The CWT's lifetime is tied to the input
    // string, so the cache vanishes when the input does.
    public static GraphemeClusterIndex For(string input)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        return _byInput.GetValue(input, key => new GraphemeClusterIndex(key));
    }

    // Length in chars (UTF-16 code units) of the cluster starting at
    // `position`. Returns 0 at end-of-input. Throws when `position`
    // isn't a cluster start, since asking for "the length of the
    // cluster starting at this offset" makes no sense if no cluster
    // starts there. The Lexer's _position invariant satisfies the
    // precondition on every Read / NextTokenLength call; tripping
    // this throw would mean a real Lexer bug, not a caller mistake.
    public int LengthAt(int position)
    {
        if (position < 0 || position >= _input.Length) return 0;
        EnsureWalkedTo(position);
        Invariant.That(_isStart[position],
            $"LengthAt called at position {position}, which is not a grapheme cluster start "
            + $"(input length {_input.Length}). The lexer's read cursor is supposed to land "
            + $"only on cluster boundaries.");
        int next = position + 1;
        while (next < _input.Length)
        {
            EnsureWalkedTo(next);
            if (_isStart[next]) break;
            next++;
        }
        return next - position;
    }

    // True iff `position` is a UAX #29 grapheme cluster boundary.
    // Position 0 (when input is non-empty) and position input.Length
    // are always boundaries. Used by the scanner-skip post-validation
    // gate to reject mid-cluster IndexOfAny landings.
    public bool IsClusterStart(int position)
    {
        if (position < 0 || position > _input.Length) return false;
        EnsureWalkedTo(position);
        return _isStart[position];
    }

    // Count of grapheme clusters fully contained in [0, charIndex).
    // Used by SourcePositionConverter.ToTokenIndex to map a char index
    // to the index of the token it sits in.
    //
    // A cluster counts only once its end boundary is at or before
    // charIndex. Those end boundaries are the cluster starts past
    // position 0 plus the always-marked input.Length boundary, so the
    // walk is _isStart over [1, charIndex], not the cluster starts over
    // [0, charIndex). The two agree when charIndex is a cluster boundary.
    // They differ inside a multi-char cluster, where counting starts
    // would also count the containing cluster and ToTokenIndex would name
    // the following token instead of the one the char is in.
    public int CountClustersUpTo(int charIndex)
    {
        if (charIndex <= 0) return 0;
        if (charIndex > _input.Length) charIndex = _input.Length;
        EnsureWalkedTo(charIndex);
        int count = 0;
        for (int i = 1; i <= charIndex; i++)
            if (_isStart[i]) count++;
        return count;
    }

    // Advance the enumerator until _isStart[target] has its final value.
    private void EnsureWalkedTo(int target)
    {
        // Fast path: already past target, or the walk has been exhausted.
        // Both reads are volatile so any _isStart writes that preceded the
        // last _walkedTo / _exhausted update are visible without the lock.
        if (_exhausted) return;
        if (target <= _walkedTo) return;
        if (target < 0) target = 0;
        if (target > _input.Length) target = _input.Length;

        // TextElementEnumerator.MoveNext isn't thread-safe: racing
        // threads can land a mid-cluster ElementIndex, which then marks
        // a non-cluster-start position in _isStart and makes the next
        // LengthAt against that position throw. The lock is per-instance,
        // so parses of different inputs don't contend, and same-input
        // parses only wait during that string's initial walk.
        lock (_walkLock)
        {
            // Re-check after acquiring the lock: another thread may have
            // exhausted the walk or already walked past target while this
            // thread was waiting on the lock.
            if (_exhausted) return;
            if (target <= _walkedTo) return;

            // The Lexer's NextTokenLength path queries this index on every
            // Read, so for any non-empty parse the enumerator gets created
            // on the first read. The null-coalescing init still pays off in
            // the corner cases that don't query: empty inputs, and pooled
            // sub-lexers that get a fresh For() call but operate in rune
            // mode and never go through LengthAt / IsClusterStart.
            _enumerator ??= StringInfo.GetTextElementEnumerator(_input);

            // Walk until we've passed `target` or run out. After MoveNext
            // returns idx, _isStart[idx] is final; we keep going if
            // idx < target so target itself gets its final value.
            while (true)
            {
                if (!_enumerator.MoveNext())
                {
                    _exhausted = true;
                    return;
                }
                int idx = _enumerator.ElementIndex;
                _isStart[idx] = true;
                // Volatile write publishes the _isStart update along with
                // the new walked-to high water mark. A reader hitting the
                // fast path on a later call sees both consistently.
                _walkedTo = idx;
                if (idx >= target) return;
            }
        }
    }
}
