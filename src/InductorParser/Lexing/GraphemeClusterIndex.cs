using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace InductorParser.Lexing;

// A per-input cache of UAX #29 grapheme cluster boundaries, populated
// lazily as positions are queried. The single source of truth for
// "where does one user-visible character end and the next begin" in
// this parser. Caching the boundaries once means reading a cluster
// length is a bool-array lookup rather than a
// StringInfo.GetNextTextElement call, which allocates a substring on
// every query.
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
// to the input. bool[] gives O(1) IsClusterStart lookups. LengthAt
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
// per input string), so concurrent parses of different inputs don't
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
    // How far the walk has reached: the highest position any MoveNext
    // has marked so far, or -1 before the first one. It doubles as a
    // "the boundary data is ready" flag for the lock-free fast path in
    // EnsureWalkedTo.
    //
    // Why the fast path is safe: each _isStart entry only ever goes from
    // false to true, once, and never changes back. The walk does that
    // write first, then sets _walkedTo. 
    // 
    // Marking _walkedTo volatile ensures that 
    // that order remains the same in other threads, so a reader that sees
    // _walkedTo >= position is guaranteed the _isStart write for that
    // position is already done. 
    // 
    // Checking _walkedTo before reading  _isStart[position] is therefore always safe. 
    // 
    // Without volatile, those reads and writes still appear in the same
    // order in the source code, but nothing forces the compiler or the
    // CPU to preserve that order as another thread observes it. A reader
    // on a different CPU could see the new _walkedTo and a stale _isStart,
    // and give the wrong answer.
    //
    // The guarantee being relied on is the acquire/release semantics of
    // volatile in ECMA-335 (CLI), Partition I, section 12.6.7 "Volatile
    // reads and writes". Verbatim:
    //
    //   "A volatile read has 'acquire semantics'; that is, it is
    //    guaranteed to occur prior to any references to [any] memory that occur
    //    after it in the instruction sequence."
    //
    //   "A volatile write has 'release semantics'; that is, it is
    //    guaranteed to happen after any memory references prior to the
    //    write instruction in the instruction sequence."
    //
    // Applied here: the volatile write of _walkedTo (release) will always happen after
    // the _isStart write that precedes it, and the volatile read of
    // _walkedTo (acquire) will always happen before the _isStart read that follows it.
    // The C# language spec restates this under "Volatile fields".
    private volatile int _walkedTo = -1;

    private GraphemeClusterIndex(string input)
    {
        _input = input;
        _isStart = new bool[input.Length + 1];
        if (input.Length > 0)
            _isStart[0] = true;
        // EOF is always a boundary, but the walk never lands on it: the
        // enumerator's ElementIndex only reaches the last cluster start,
        // never input.Length. The constructor marks it here so
        // IsClusterStart(input.Length) returns true instead of false.
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
    // isn't a cluster start, since asking for the length of a cluster
    // that starts at this offset makes no sense when none does. The
    // Lexer's read cursor only lands on cluster boundaries, so this
    // throw firing would mean a Lexer bug, not a caller mistake.
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
    // are always boundaries. The scanner fast paths use it to reject
    // mid-cluster IndexOf / IndexOfAny landings before they advance.
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
    // count is the number of true _isStart entries in [1, charIndex].
    // When charIndex sits inside a multi-char cluster, that range stops
    // short of the cluster's own end, so the cluster the char belongs
    // to is left out of the count.
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
        // Fast path, no lock: if the walk is exhausted, or has already
        // reached target, then _isStart[target] is final and safe to read
        // directly. These two reads are volatile, which is what makes that
        // safe without the lock. The _walkedTo field has the ordering
        // argument for why.
        if (_exhausted) return;
        if (target <= _walkedTo) return;
        if (target < 0) target = 0;
        if (target > _input.Length) target = _input.Length;

        // TextElementEnumerator.MoveNext isn't thread-safe, so the walk
        // runs under a lock.
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
            // returns idx, _isStart[idx] has its final value. Keep going
            // while idx < target so target itself gets marked.
            while (true)
            {
                if (!_enumerator.MoveNext())
                {
                    _exhausted = true;
                    return;
                }
                int idx = _enumerator.ElementIndex;
                _isStart[idx] = true;
                // Set the flag after the _isStart write above, never
                // before. The _walkedTo field comment explains why the
                // order matters.
                _walkedTo = idx;
                if (idx >= target) return;
            }
        }
    }
}
