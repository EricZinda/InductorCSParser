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
internal sealed class GraphemeClusterIndex
{
    private static readonly ConditionalWeakTable<string, GraphemeClusterIndex> _byInput = new();

    private readonly string _input;
    private readonly bool[] _isStart;
    private TextElementEnumerator? _enumerator;
    private bool _exhausted;

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
        if (!_isStart[position])
            throw new InvalidOperationException(
                $"Internal lexer invariant violated: LengthAt called at position {position}, " +
                $"which is not a grapheme cluster start (input length {_input.Length}). " +
                $"The lexer's read cursor is supposed to land only on cluster boundaries.");
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
    // Used by SourcePositionConverter.ToTokenIndex.
    public int CountClustersUpTo(int charIndex)
    {
        if (charIndex <= 0) return 0;
        if (charIndex > _input.Length) charIndex = _input.Length;
        EnsureWalkedTo(charIndex);
        int count = 0;
        for (int i = 0; i < charIndex; i++)
            if (_isStart[i]) count++;
        return count;
    }

    // Advance the enumerator until _isStart[target] has its final value.
    private void EnsureWalkedTo(int target)
    {
        if (_exhausted) return;
        if (target < 0) target = 0;
        if (target > _input.Length) target = _input.Length;

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
            if (idx >= target) return;
        }
    }
}
