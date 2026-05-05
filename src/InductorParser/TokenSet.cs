using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace InductorParser;

// A set of tokens, used to describe character classes for OneOf and NoneOf.
// A token is either a single Unicode scalar value (rune) or a multi-rune
// grapheme cluster (skin-toned emoji, ZWJ family, regional-indicator pair,
// base+combining-mark cluster). Build one with the factory methods (Single,
// Range, Runes, Category) or one of the built-ins (Letters, Digits,
// InlineWhitespace, LineTerminators, and their Ascii.* variants), then
// compose larger classes with the set operators:
//
//     |   union           a | b           tokens in a or b
//     &   intersection    a & b           tokens in a and b
//     ~   complement      ~a              tokens not in a (rune-only sets)
//
// Set difference is the idiom a & ~b ("a minus b"). The operators return a
// new TokenSet. The struct is immutable.
//
//     var unicodeIdentifier = TokenSet.Letters | TokenSet.Digits | TokenSet.Runes("_");
//     var asciiConsonants   = TokenSet.Ascii.Letters & ~TokenSet.Runes("aeiouAEIOU");
//     var cyrillicLetters   = TokenSet.Letters & TokenSet.Range(0x0400, 0x04FF);
//     var emojiOrLetters    = TokenSet.Letters | TokenSet.Runes(USFlagGrapheme);
//
// Internally a TokenSet keeps two pieces. _ranges is a sorted, non-overlapping,
// non-adjacent array of code-point runs that holds every single-rune member.
// _multiRuneGraphemes is a sorted ordinal, deduped array of grapheme strings
// that holds every member that occupies two or more runes. Single-rune
// graphemes always go in _ranges, never in _multiRuneGraphemes, so a set
// that's never given a multi-rune entry pays nothing. The rune fast path
// (binary search of intervals) is unchanged, and a grammar rule that uses
// TokenSet.Letters a thousand times pays the Unicode-table scan once at
// startup and then a handful of Contains() calls per match.
//
// Complement is only defined when _multiRuneGraphemes is empty. The universe
// of grapheme clusters is unbounded (any rune sequence respecting UAX #29
// boundaries is a grapheme), so complement against it can't be represented
// by a finite explicit set. ~set on a mixed set throws InvalidOperationException
// rather than silently dropping multi-rune entries. The idiom a & ~b keeps
// working in the typical case where b is rune-only.
public readonly partial struct TokenSet : IEquatable<TokenSet>
{
    // One contiguous run of Unicode code points, inclusive on both ends:
    // the closed interval [Low, High]. A TokenSet's rune part is represented
    // as a sorted, non-overlapping, non-adjacent array of these runs. Named
    // Interval (not Range) to avoid colliding with the public Range(...)
    // factory method below.
    private readonly record struct Interval(int Low, int High);

    private readonly Interval[] _ranges;

    // Multi-rune grapheme members of the set. Sorted ordinal, deduped at
    // construction. Each entry is the UTF-16 string for one grapheme that
    // occupies two or more runes. Null is treated as the same as
    // Array.Empty<string>(); rune-only sets never allocate one.
    private readonly string[] _multiRuneGraphemes;

    private TokenSet(Interval[] ranges) : this(ranges, null) { }

    private TokenSet(Interval[] ranges, string[]? multiRuneGraphemes)
    {
        _ranges = ranges;
        _multiRuneGraphemes = multiRuneGraphemes ?? Array.Empty<string>();
    }

    // True when the set has any multi-rune grapheme members. Rules that
    // need to choose between the rune fast path and the grapheme-aware
    // path branch on this once at parse-time entry, not per-token.
    internal bool HasMultiRuneGraphemes =>
        _multiRuneGraphemes != null && _multiRuneGraphemes.Length > 0;

    // The rune-only portion of this set. Used by rules that need to
    // build a complement (NoneOfRule, ScanUntilRule) when the original
    // set has multi-rune entries: ~set throws on a mixed set, so callers
    // first project down to the rune-only part and then complement.
    internal TokenSet RunesOnlyPart =>
        HasMultiRuneGraphemes ? new TokenSet(_ranges) : this;

    // Read-only view of the multi-rune graphemes, sorted ordinal. Used
    // by rules that need to walk the grapheme entries (the lookahead
    // first-rune helper in OneOfRule, for instance). Empty when the
    // set has no multi-rune content.
    internal ReadOnlySpan<string> MultiRuneGraphemes =>
        _multiRuneGraphemes ?? Array.Empty<string>();

    public bool Contains(int codepoint)
    {
        var ranges = _ranges;
        if (ranges == null) return false;
        for (int index = 0; index < ranges.Length; index++)
        {
            if (codepoint < ranges[index].Low) return false;
            if (codepoint <= ranges[index].High) return true;
        }
        return false;
    }

    public bool Contains(char c) => Contains((int)c);
    public bool Contains(Rune r) => Contains(r.Value);

    // Membership over a grapheme. If the input decodes as exactly one
    // Unicode scalar value (one rune in 1 or 2 UTF-16 chars), check the
    // rune intervals. Otherwise binary search the sorted multi-rune
    // grapheme array for an ordinal match. Empty input is never a member.
    public bool Contains(string grapheme)
    {
        if (grapheme == null) throw new ArgumentNullException(nameof(grapheme));
        if (grapheme.Length == 0) return false;
        if (TrySingleRune(grapheme, out int runeValue))
            return Contains(runeValue);
        return BinarySearchMultiRune(grapheme.AsSpan()) >= 0;
    }

    // Span overload so rules can probe a token's Chars without building
    // a string. Same semantics as Contains(string): single-rune spans
    // hit the rune intervals, multi-rune spans hit the grapheme array.
    internal bool ContainsToken(ReadOnlySpan<char> grapheme)
    {
        if (grapheme.Length == 0) return false;
        if (TrySingleRune(grapheme, out int runeValue))
            return Contains(runeValue);
        return BinarySearchMultiRune(grapheme) >= 0;
    }

    private static bool TrySingleRune(string grapheme, out int runeValue) =>
        TrySingleRune(grapheme.AsSpan(), out runeValue);

    private static bool TrySingleRune(ReadOnlySpan<char> grapheme, out int runeValue)
    {
        if (grapheme.Length == 1)
        {
            char c = grapheme[0];
            if (char.IsSurrogate(c)) { runeValue = -1; return false; }
            runeValue = c;
            return true;
        }
        if (grapheme.Length == 2
            && char.IsHighSurrogate(grapheme[0])
            && char.IsLowSurrogate(grapheme[1]))
        {
            runeValue = char.ConvertToUtf32(grapheme[0], grapheme[1]);
            return true;
        }
        runeValue = -1;
        return false;
    }

    // Binary search the sorted multi-rune array for a span equal to
    // `target`. Returns the index on hit, or ~insertionPoint on miss
    // (the standard Array.BinarySearch convention so callers like the
    // sorted-merge in operator| can reuse this). MemoryExtensions.
    // SequenceCompareTo does the ordinal comparison without allocating.
    private int BinarySearchMultiRune(ReadOnlySpan<char> target)
    {
        var array = _multiRuneGraphemes;
        if (array == null || array.Length == 0) return ~0;
        int low = 0;
        int high = array.Length - 1;
        while (low <= high)
        {
            int mid = (low + high) >> 1;
            int cmp = array[mid].AsSpan().SequenceCompareTo(target);
            if (cmp == 0) return mid;
            if (cmp < 0) low = mid + 1;
            else high = mid - 1;
        }
        return ~low;
    }

    public bool IsEmpty =>
        (_ranges == null || _ranges.Length == 0)
        && (_multiRuneGraphemes == null || _multiRuneGraphemes.Length == 0);

    internal bool TryGetBmpChars(int maxChars, out char[] chars)
    {
        var ranges = _ranges;
        if (ranges == null || ranges.Length == 0)
        {
            chars = Array.Empty<char>();
            return false;
        }

        int count = 0;
        for (int index = 0; index < ranges.Length; index++)
        {
            int low = ranges[index].Low;
            int high = ranges[index].High;
            if (high > char.MaxValue)
            {
                chars = Array.Empty<char>();
                return false;
            }
            if (low <= 0xDFFF && high >= 0xD800)
            {
                chars = Array.Empty<char>();
                return false;
            }
            count += high - low + 1;
            if (count > maxChars)
            {
                chars = Array.Empty<char>();
                return false;
            }
        }

        chars = new char[count];
        int output = 0;
        for (int rangeIndex = 0; rangeIndex < ranges.Length; rangeIndex++)
        {
            for (int codepoint = ranges[rangeIndex].Low; codepoint <= ranges[rangeIndex].High; codepoint++)
                chars[output++] = (char)codepoint;
        }
        return true;
    }

    // Value equality: two TokenSets are equal iff they contain the same
    // tokens. Normalize guarantees a canonical interval list (sorted,
    // non-overlapping, non-adjacent), and the multi-rune array is sorted
    // ordinal and deduped at construction. Equal sets therefore have
    // identical _ranges and identical _multiRuneGraphemes element-wise.
    public bool Equals(TokenSet other)
    {
        var mine = _ranges;
        var theirs = other._ranges;
        int mineLength = mine?.Length ?? 0;
        int theirsLength = theirs?.Length ?? 0;
        if (mineLength != theirsLength) return false;
        for (int index = 0; index < mineLength; index++)
            if (mine![index] != theirs![index]) return false;

        var mineMulti = _multiRuneGraphemes;
        var theirsMulti = other._multiRuneGraphemes;
        int mineMultiLength = mineMulti?.Length ?? 0;
        int theirsMultiLength = theirsMulti?.Length ?? 0;
        if (mineMultiLength != theirsMultiLength) return false;
        for (int index = 0; index < mineMultiLength; index++)
            if (!string.Equals(mineMulti![index], theirsMulti![index], StringComparison.Ordinal))
                return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is TokenSet other && Equals(other);

    public override int GetHashCode()
    {
        var ranges = _ranges;
        var multi = _multiRuneGraphemes;
        int rangesLength = ranges?.Length ?? 0;
        int multiLength = multi?.Length ?? 0;
        // Equals treats null and an empty array as the same "empty" set
        // (both length 0) for both halves, so GetHashCode has to agree
        // or the contract breaks. A factory like Runes("") returns a
        // TokenSet with empty arrays, which Equals reports as equal to
        // default(TokenSet) but would hash differently if we only
        // short-circuited on null.
        if (rangesLength == 0 && multiLength == 0) return 0;
        var hash = new HashCode();
        for (int index = 0; index < rangesLength; index++)
            hash.Add(ranges![index]);
        for (int index = 0; index < multiLength; index++)
            hash.Add(multi![index], StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    public static bool operator ==(TokenSet a, TokenSet b) => a.Equals(b);
    public static bool operator !=(TokenSet a, TokenSet b) => !a.Equals(b);

    // Maximum number of entries ToString renders before truncating.
    // Large TokenSets (Unicode-category-wide classes like Letters) can
    // hold hundreds of ranges, which would produce an unreadable trace
    // line. Capping at 8 keeps trace output legible while preserving
    // the useful information for small, hand-built classes. The
    // truncated tail shows "+N more" so a reader can tell output was
    // dropped. Multi-rune graphemes count as entries on equal footing
    // with rune ranges, so a mixed set with 6 ranges and 3 graphemes
    // shows the first 8 and "+1 more".
    private const int MaxRenderedEntries = 8;

    // Human-readable rendering of the set, for trace output and
    // debugger display. Produces "[a-z,A-Z,0-9]" style output with
    // single-codepoint ranges collapsed to one char and long ranges
    // rendered as low-high. Printable ASCII code points render as the
    // literal character, everything else renders as U+XXXX. Multi-rune
    // graphemes render as the user-perceived character itself, no
    // special quoting (e.g. `[a-z,👋🏽,🇺🇸]`). Classes with more
    // than MaxRenderedEntries entries are truncated with a "+N more"
    // tail. Keeps trace lines legible without dragging in the entire
    // Unicode database.
    public override string ToString()
    {
        var ranges = _ranges;
        var multi = _multiRuneGraphemes;
        int rangesLength = ranges?.Length ?? 0;
        int multiLength = multi?.Length ?? 0;
        if (rangesLength == 0 && multiLength == 0) return "[]";
        var sb = new StringBuilder();
        sb.Append('[');
        int totalEntries = rangesLength + multiLength;
        int rendered = totalEntries <= MaxRenderedEntries
            ? totalEntries
            : MaxRenderedEntries;
        int written = 0;
        for (int index = 0; index < rangesLength && written < rendered; index++)
        {
            if (written > 0) sb.Append(',');
            var interval = ranges![index];
            sb.Append(RenderCodepoint(interval.Low));
            if (interval.High != interval.Low)
            {
                sb.Append('-');
                sb.Append(RenderCodepoint(interval.High));
            }
            written++;
        }
        for (int index = 0; index < multiLength && written < rendered; index++)
        {
            if (written > 0) sb.Append(',');
            sb.Append(multi![index]);
            written++;
        }
        if (totalEntries > MaxRenderedEntries)
        {
            sb.Append(",...+");
            sb.Append(totalEntries - MaxRenderedEntries);
            sb.Append(" more");
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static string RenderCodepoint(int codepoint)
    {
        if (codepoint >= 0x20 && codepoint <= 0x7E)
            return ((char)codepoint).ToString();
        return $"U+{codepoint:X4}";
    }

    // The empty set, containing no runes. Equivalent to default(TokenSet),
    // exposed as a named constant so callers can write TokenSet.Empty
    // instead of relying on "default happens to mean empty."
    public static readonly TokenSet Empty = default;

    // The universal set, containing every valid Unicode scalar value
    // (0..0x10FFFF minus the surrogate block). The complement of Empty.
    // Used as the "unknown / anything goes" default for FirstConsumedTokens
    // (see RuleStartRequirements) so rules with no tighter information
    // never get filtered out.
    public static readonly TokenSet Universe = ~default(TokenSet);

    public static TokenSet Single(char c) => Single((int)c);
    public static TokenSet Single(Rune r) => Single(r.Value);
    public static TokenSet Single(int codepoint)
    {
        ValidateScalarValue(codepoint, nameof(codepoint));
        return new TokenSet(new[] { new Interval(codepoint, codepoint) });
    }

    public static TokenSet Range(char low, char high) => Range((int)low, (int)high);
    public static TokenSet Range(Rune low, Rune high) => Range(low.Value, high.Value);
    // Validates the endpoints themselves, not the interior of the range.
    // That means Range(0, 0x10FFFF) is allowed even though the interval
    // covers the surrogate block 0xD800..0xDFFF, which isn't a set of
    // valid scalar values. The internal set ends up with "dead" slots in
    // that block, which is harmless: the lexer never produces surrogate
    // halves as token values, so no Contains() check against those slots
    // can ever fire. Auto-splitting the range around the surrogate gap
    // would be more principled but also more code for zero user-visible
    // effect.
    public static TokenSet Range(int low, int high)
    {
        ValidateScalarValue(low, nameof(low));
        ValidateScalarValue(high, nameof(high));
        if (high < low) throw new ArgumentException("high must be >= low");
        return new TokenSet(new[] { new Interval(low, high) });
    }

    // Construct a TokenSet from a list of code-point intervals. Each element
    // is a closed range [Low, High]. The input doesn't need to be sorted or
    // non-overlapping; Normalize takes care of that. Intended as the bulk
    // factory for large hand-curated or generated tables that would be
    // tedious to chain through the | operator.
    internal static TokenSet FromRanges(ReadOnlySpan<(int Low, int High)> ranges)
    {
        var list = new List<Interval>(ranges.Length);
        for (int index = 0; index < ranges.Length; index++)
        {
            var (low, high) = ranges[index];
            ValidateScalarValue(low, nameof(ranges));
            ValidateScalarValue(high, nameof(ranges));
            if (high < low)
                throw new ArgumentException(
                    $"Range at index {index} has high ({high:X}) < low ({low:X}).",
                    nameof(ranges));
            list.Add(new Interval(low, high));
        }
        return new TokenSet(Normalize(list));
    }

    public static TokenSet Runes(string characters)
    {
        if (characters == null) throw new ArgumentNullException(nameof(characters));
        var intervals = new List<Interval>();
        List<string>? graphemes = null;
        // Walk the string one grapheme at a time. A single-rune text
        // element joins the rune intervals; a multi-rune grapheme
        // (skin-toned emoji, ZWJ family, regional-indicator pair,
        // decomposed accent, even CRLF) joins the multi-rune array.
        // GetNextTextElement is the same API the lexer uses, so what
        // gets stored agrees with what the lexer will hand back at
        // parse time.
        int index = 0;
        while (index < characters.Length)
        {
            string grapheme = StringInfo.GetNextTextElement(characters, index);
            int graphemeStart = index;
            int graphemeLength = grapheme.Length;
            // First rune of the grapheme. Used both for the single-rune
            // case (interval add) and to detect lone surrogate halves
            // before the multi-rune branch can swallow them.
            int firstRuneLength;
            int firstRune;
            if (char.IsHighSurrogate(characters[index])
                && index + 1 < characters.Length
                && char.IsLowSurrogate(characters[index + 1]))
            {
                firstRune = char.ConvertToUtf32(characters[index], characters[index + 1]);
                firstRuneLength = 2;
            }
            else
            {
                firstRune = characters[index];
                firstRuneLength = 1;
            }
            // Catches lone surrogate halves in the input string. A
            // well-formed UTF-16 string shouldn't contain them, but
            // we can't trust every caller's string to be well-formed.
            if (!Rune.IsValid(firstRune))
                throw new ArgumentException(
                    $"Runes(string) encountered an invalid Unicode scalar value (0x{firstRune:X4}) at UTF-16 offset {graphemeStart}. " +
                    "Lone surrogate halves aren't valid runes.",
                    nameof(characters));

            if (graphemeLength == firstRuneLength)
            {
                intervals.Add(new Interval(firstRune, firstRune));
            }
            else
            {
                // Multi-rune grapheme. Validate every rune in it so we
                // catch lone surrogate halves past the first rune too.
                ValidateGraphemeRunes(grapheme, graphemeStart);
                graphemes ??= new List<string>();
                graphemes.Add(grapheme);
            }
            index += graphemeLength;
        }
        return new TokenSet(Normalize(intervals), NormalizeGraphemes(graphemes));
    }

    // Walks a grapheme's runes by hand and throws on any lone surrogate
    // half. We've already validated the first rune in the outer loop;
    // this is for runes 2..N of a multi-rune grapheme.
    private static void ValidateGraphemeRunes(string grapheme, int graphemeStart)
    {
        for (int runeIndex = 0; runeIndex < grapheme.Length;)
        {
            int runeCodepoint;
            if (char.IsHighSurrogate(grapheme[runeIndex])
                && runeIndex + 1 < grapheme.Length
                && char.IsLowSurrogate(grapheme[runeIndex + 1]))
            {
                runeCodepoint = char.ConvertToUtf32(grapheme[runeIndex], grapheme[runeIndex + 1]);
                runeIndex += 2;
            }
            else
            {
                runeCodepoint = grapheme[runeIndex];
                runeIndex++;
            }
            if (!Rune.IsValid(runeCodepoint))
                throw new ArgumentException(
                    $"Runes(string) encountered an invalid Unicode scalar value (0x{runeCodepoint:X4}) " +
                    $"inside the grapheme at UTF-16 offset {graphemeStart}. " +
                    "Lone surrogate halves aren't valid runes.",
                    nameof(grapheme));
        }
    }

    // Sort and dedupe the multi-rune grapheme list with ordinal
    // comparison so the canonical form matches what Equals and the
    // binary-search Contains expect.
    private static string[] NormalizeGraphemes(List<string>? graphemes)
    {
        if (graphemes == null || graphemes.Count == 0) return Array.Empty<string>();
        graphemes.Sort(StringComparer.Ordinal);
        int writeIndex = 1;
        for (int readIndex = 1; readIndex < graphemes.Count; readIndex++)
        {
            if (!string.Equals(graphemes[readIndex], graphemes[writeIndex - 1], StringComparison.Ordinal))
                graphemes[writeIndex++] = graphemes[readIndex];
        }
        if (writeIndex == graphemes.Count) return graphemes.ToArray();
        var result = new string[writeIndex];
        graphemes.CopyTo(0, result, 0, writeIndex);
        return result;
    }

    // Shared validator for the int factories. A rune is any code point in
    // 0..0x10FFFF except the surrogate halves 0xD800..0xDFFF. Rune.IsValid
    // enforces both constraints.
    private static void ValidateScalarValue(int codepoint, string parameterName)
    {
        if (!Rune.IsValid(codepoint))
            throw new ArgumentOutOfRangeException(parameterName, codepoint,
                "Must be a valid Unicode scalar value (0..0x10FFFF, excluding surrogates 0xD800..0xDFFF).");
    }

    public static TokenSet operator |(TokenSet a, TokenSet b)
    {
        var combined = new List<Interval>();
        if (a._ranges != null) combined.AddRange(a._ranges);
        if (b._ranges != null) combined.AddRange(b._ranges);
        var mergedGraphemes = MergeMultiRuneUnion(a._multiRuneGraphemes, b._multiRuneGraphemes);
        return new TokenSet(Normalize(combined), mergedGraphemes);
    }

    // Sorted-range intersection: walk both sets once, picking [max(low), min(high)]
    // whenever the current intervals overlap, and advancing whichever interval
    // ends first. Linear in the sum of the two interval counts. Both inputs are
    // already normalized (sorted, non-overlapping, non-adjacent), and so is the
    // result. Adjacent overlap fragments can't appear because that would imply
    // the inputs themselves had adjacent intervals, contradicting normalization.
    // Multi-rune graphemes are intersected separately by sorted ordinal merge.
    public static TokenSet operator &(TokenSet a, TokenSet b)
    {
        var aRanges = a._ranges;
        var bRanges = b._ranges;
        var mergedGraphemes = MergeMultiRuneIntersect(a._multiRuneGraphemes, b._multiRuneGraphemes);
        if (aRanges == null || bRanges == null || aRanges.Length == 0 || bRanges.Length == 0)
            return new TokenSet(Array.Empty<Interval>(), mergedGraphemes);

        var result = new List<Interval>();
        int aIndex = 0;
        int bIndex = 0;
        while (aIndex < aRanges.Length && bIndex < bRanges.Length)
        {
            int overlapLow = Math.Max(aRanges[aIndex].Low, bRanges[bIndex].Low);
            int overlapHigh = Math.Min(aRanges[aIndex].High, bRanges[bIndex].High);
            if (overlapLow <= overlapHigh)
                result.Add(new Interval(overlapLow, overlapHigh));
            // Advance past whichever interval ends first. The other may still
            // overlap with the next interval in the first set.
            if (aRanges[aIndex].High < bRanges[bIndex].High) aIndex++;
            else bIndex++;
        }
        return new TokenSet(result.ToArray(), mergedGraphemes);
    }

    // Complement against the full set of Unicode scalar values: everything
    // in 0..0x10FFFF except the surrogate block 0xD800..0xDFFF (which isn't
    // a set of valid scalar values) and except the input set's intervals.
    // Implementation walks the input intervals and emits the gaps between
    // them, splitting any gap that straddles the surrogate block.
    //
    // Throws InvalidOperationException when the input has any multi-rune
    // grapheme entries. The universe of grapheme clusters is unbounded
    // (any rune sequence respecting UAX #29 boundaries is a grapheme), so
    // complement against it can't be represented as a finite explicit
    // set. The workaround is to project the input down to its rune-only
    // part first: `set & ~runeOnlyMask`.
    public static TokenSet operator ~(TokenSet a)
    {
        if (a.HasMultiRuneGraphemes)
            throw new InvalidOperationException(
                "Cannot complement a TokenSet that contains multi-rune graphemes. " +
                "The universe of grapheme clusters is unbounded, so the result " +
                "isn't representable as a finite set. Build the rune-only mask " +
                "you want to subtract and use `set & ~runeOnlyMask` instead.");
        const int MinScalarValue = 0;
        const int MaxScalarValue = 0x10FFFF;
        var inputRanges = a._ranges;
        var result = new List<Interval>();
        int cursor = MinScalarValue;
        if (inputRanges != null)
        {
            for (int index = 0; index < inputRanges.Length; index++)
            {
                if (cursor < inputRanges[index].Low)
                    EmitIntervalSkippingSurrogates(result, cursor, inputRanges[index].Low - 1);
                cursor = inputRanges[index].High + 1;
            }
        }
        if (cursor <= MaxScalarValue)
            EmitIntervalSkippingSurrogates(result, cursor, MaxScalarValue);
        return new TokenSet(result.ToArray());
    }

    // Sorted-merge union of two sorted-ordinal grapheme arrays. Linear
    // in the sum of the two array lengths. Skips duplicates so the
    // result stays canonical. Fast-paths when either side is empty so
    // rune-only sets pay no allocation past the empty-array sentinel.
    private static string[] MergeMultiRuneUnion(string[]? a, string[]? b)
    {
        int aLength = a?.Length ?? 0;
        int bLength = b?.Length ?? 0;
        if (aLength == 0 && bLength == 0) return Array.Empty<string>();
        if (aLength == 0) return b!;
        if (bLength == 0) return a!;
        var merged = new List<string>(aLength + bLength);
        int aIndex = 0;
        int bIndex = 0;
        while (aIndex < aLength && bIndex < bLength)
        {
            int cmp = string.CompareOrdinal(a![aIndex], b![bIndex]);
            if (cmp == 0)
            {
                merged.Add(a[aIndex]);
                aIndex++;
                bIndex++;
            }
            else if (cmp < 0)
            {
                merged.Add(a[aIndex++]);
            }
            else
            {
                merged.Add(b[bIndex++]);
            }
        }
        while (aIndex < aLength) merged.Add(a![aIndex++]);
        while (bIndex < bLength) merged.Add(b![bIndex++]);
        return merged.ToArray();
    }

    // Sorted-merge intersection of two sorted-ordinal grapheme arrays.
    // Returns the empty sentinel when either side is empty so a
    // rune-only side erases the other's multi-rune content under &.
    private static string[] MergeMultiRuneIntersect(string[]? a, string[]? b)
    {
        int aLength = a?.Length ?? 0;
        int bLength = b?.Length ?? 0;
        if (aLength == 0 || bLength == 0) return Array.Empty<string>();
        var merged = new List<string>(Math.Min(aLength, bLength));
        int aIndex = 0;
        int bIndex = 0;
        while (aIndex < aLength && bIndex < bLength)
        {
            int cmp = string.CompareOrdinal(a![aIndex], b![bIndex]);
            if (cmp == 0)
            {
                merged.Add(a[aIndex]);
                aIndex++;
                bIndex++;
            }
            else if (cmp < 0) aIndex++;
            else bIndex++;
        }
        return merged.Count == 0 ? Array.Empty<string>() : merged.ToArray();
    }

    // Emit [low, high] into result, splitting around the surrogate block
    // 0xD800..0xDFFF if the interval straddles it. Called only from the
    // complement operator, where the input interval came from a gap
    // calculation and is therefore guaranteed non-empty (low <= high).
    private static void EmitIntervalSkippingSurrogates(List<Interval> result, int low, int high)
    {
        const int SurrogateLow = 0xD800;
        const int SurrogateHigh = 0xDFFF;
        if (high < SurrogateLow || low > SurrogateHigh)
        {
            result.Add(new Interval(low, high));
            return;
        }
        if (low < SurrogateLow)
            result.Add(new Interval(low, SurrogateLow - 1));
        if (high > SurrogateHigh)
            result.Add(new Interval(SurrogateHigh + 1, high));
    }

    private static Interval[] Normalize(List<Interval> ranges)
    {
        if (ranges.Count == 0) return Array.Empty<Interval>();
        ranges.Sort((first, second) => first.Low.CompareTo(second.Low));
        var merged = new List<Interval>();
        var current = ranges[0];
        for (int index = 1; index < ranges.Count; index++)
        {
            var next = ranges[index];
            if (next.Low <= current.High + 1)
            {
                if (next.High > current.High) current = new Interval(current.Low, next.High);
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }
        merged.Add(current);
        return merged.ToArray();
    }

    // Per-category cache. Each UnicodeCategory's set of scalar values is
    // expensive to compute (a full 0..0x10FFFF scan), so we cache the result
    // the first time anyone asks. Subsequent lookups are hash-table reads.
    // Concurrent because nothing else in TokenSet holds a lock. Multiple
    // threads resolving Letters on startup are fine.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<UnicodeCategory, TokenSet> _categoryCache
        = new System.Collections.Concurrent.ConcurrentDictionary<UnicodeCategory, TokenSet>();

    // One UnicodeCategory is one TokenSet and it's cached (if used).
    public static TokenSet Category(UnicodeCategory category)
    {
        if (_categoryCache.TryGetValue(category, out var cached)) return cached;
        BuildCategories(new[] { category });
        return _categoryCache[category];
    }

    // Composite built-ins. Letters is the union of the five "Letter"
    // UnicodeCategory values. Digits is one category. Wrapped in Lazy so
    // the union work happens once and is cached. Without it, every access
    // to TokenSet.Letters would redo the four | merges.
    //
    // The Lazy factory calls BuildCategories with the full batch first, so
    // all five category scans happen in a single 0..0x10FFFF pass rather
    // than five separate passes. Each subsequent Category(...) call lookup
    // is then a cache hit.
    private static readonly Lazy<TokenSet> _letters = new Lazy<TokenSet>(() =>
        CategoriesUnion(
            UnicodeCategory.UppercaseLetter,
            UnicodeCategory.LowercaseLetter,
            UnicodeCategory.TitlecaseLetter,
            UnicodeCategory.ModifierLetter,
            UnicodeCategory.OtherLetter));
    private static readonly Lazy<TokenSet> _digits = new Lazy<TokenSet>(() =>
        Category(UnicodeCategory.DecimalDigitNumber));

    // Shared helper for built-ins: ensure every target category is cached
    // (one scan for all missing targets), then union them in order.
    private static TokenSet CategoriesUnion(params UnicodeCategory[] targets)
    {
        BuildCategories(targets);
        var result = Category(targets[0]);
        for (int index = 1; index < targets.Length; index++)
            result = result | Category(targets[index]);
        return result;
    }

    // InlineWhitespace is "whitespace within a line": every rune that
    // char.IsWhiteSpace accepts MINUS the seven UAX #18 single-rune line
    // terminators (LF, VT, FF, CR, NEL, LS, PS). It doesn't decompose
    // cleanly into UnicodeCategory values, so it's built by a predicate
    // scan rather than CategoriesUnion. For line terminators see
    // LineTerminators below and Rules.EndOfLine().
    private static readonly Lazy<TokenSet> _inlineWhitespace = new Lazy<TokenSet>(BuildInlineWhitespace);

    // The set of Unicode scalar values that are letters in Unicode's
    // General_Category sense (Lu, Ll, Lt, Lm, Lo). Matches what
    // char.IsLetter and Rune.IsLetter consider a letter.
    //
    // Use this for things that are literally letters: identifier characters
    // in a name, keyword text inside an alphabetic token, a rule that
    // accepts 'a' through 'z' plus 'é' and '漢' and 'ж'.
    //
    // DON'T use this as a way to match "any character" or "any content." It
    // rejects digits, whitespace, punctuation, symbols, and any multi-rune
    // grapheme like emoji. A grammar that wants "match everything up to the
    // next delimiter" or "match anything the other rules didn't claim"
    // should use NoneOf(stopSet) for delimiter-based stops, or
    // Not(stopRule) + AnyToken() for rule-based stops.
    public static TokenSet Letters => _letters.Value;
    public static TokenSet Digits => _digits.Value;
    public static TokenSet InlineWhitespace => _inlineWhitespace.Value;

    // The line terminators defined by UAX #18 Annex C: the seven
    // single-rune terminators LF (U+000A), VT (U+000B), FF (U+000C),
    // CR (U+000D), NEL (U+0085), LINE SEPARATOR (U+2028),
    // PARAGRAPH SEPARATOR (U+2029), plus the CRLF two-rune cluster
    // (which UAX #29 GB3 keeps glued together in one grapheme).
    // Matches what Java's \R, ECMAScript's "line terminator" concept,
    // and most modern regex engines treat as a newline.
    //
    // CRLF lives in the set as a multi-rune entry (TokenSet supports
    // mixing single-rune and multi-rune entries), so OneOf / NoneOf / 
    // ScanUntil / ScanWhile against
    // this set all treat the CRLF cluster as one terminator. 
    public static readonly TokenSet LineTerminators =
          Single(0x000A)   // LF
        | Single(0x000B)   // VT
        | Single(0x000C)   // FF
        | Single(0x000D)   // CR
        | Single(0x0085)   // NEL
        | Single(0x2028)   // LS
        | Single(0x2029)   // PS
        | Runes("\r\n");   // CRLF cluster

    // Full-Unicode intra-line whitespace plus every UAX #18 line
    // terminator (the seven single-rune terminators and the CRLF
    // two-rune cluster, which LineTerminators carries as a multi-rune
    // entry). Use this for grammars that treat any whitespace as
    // ordinary separator. For ASCII-only whitespace use
    // Ascii.AnyWhitespace.
    public static TokenSet AnyWhitespace => _anyWhitespace.Value;
    private static readonly Lazy<TokenSet> _anyWhitespace =
        new Lazy<TokenSet>(() => InlineWhitespace | LineTerminators);

    // U+FFFD REPLACEMENT CHARACTER. .NET's Unicode-encoding decoders
    // (Encoding.UTF8, Encoding.Unicode, Encoding.UTF32) substitute
    // U+FFFD for ill-formed byte sequences when using the default
    // DecoderReplacementFallback. So a U+FFFD in your input is the
    // fingerprint of an upstream decoder that swallowed something
    // malformed. Grammars that want to surface or reject those
    // markers can use OneOf(TokenSet.Replacement) or
    // NoneOf(TokenSet.Replacement | ...).
    public static readonly TokenSet Replacement = Single(0xFFFD);

    public static class Ascii
    {
        public static readonly TokenSet Letters = Range('A', 'Z') | Range('a', 'z');
        public static readonly TokenSet Digits = Range('0', '9');
        // ASCII intra-line whitespace: SPACE and TAB only. Mirrors the
        // full-Unicode TokenSet.InlineWhitespace.
        public static readonly TokenSet InlineWhitespace = Runes(" \t");
        // ASCII whitespace including line terminators: SPACE, TAB, CR,
        // LF, plus the CRLF two-rune cluster as a multi-rune entry.
        // Use this for grammars that treat newlines as ordinary
        // whitespace (the regex \s convention). For grammars that need
        // to distinguish intra-line whitespace from line terminators,
        // use InlineWhitespace and Rules.EndOfLine() instead.
        // CR, LF, and the CRLF cluster all live in the set so that
        // OneOf / NoneOf / ScanUntil match each consistently: an input
        // CRLF cluster is the multi-rune entry, a bare CR or LF is the
        // matching single-rune entry.
        public static readonly TokenSet AnyWhitespace = InlineWhitespace | Single('\r') | Single('\n') | Runes("\r\n");
        public static readonly TokenSet HexDigits = Digits | Range('a', 'f') | Range('A', 'F');
    }

    // Build from predicate over code points that fit in one UTF-16 char
    // (U+0000..U+FFFF). Supplementary-plane whitespace is rare in real
    // input and not needed for the smallest core. Includes a code point
    // when char.IsWhiteSpace accepts it AND it's not one of the seven
    // UAX #18 single-rune line terminators (those belong to EndOfLine).
    private static TokenSet BuildInlineWhitespace()
    {
        var list = new List<Interval>();
        int? currentLow = null;
        int currentHigh = 0;
        for (int codepoint = 0; codepoint <= 0xFFFF; codepoint++)
        {
            // Skip the surrogate block: not valid Unicode scalar values.
            // See BuildFromPredicate below for the full rationale.
            if (codepoint >= 0xD800 && codepoint <= 0xDFFF) continue;
            if (char.IsWhiteSpace((char)codepoint) && !IsLineTerminator(codepoint))
            {
                if (currentLow == null) { currentLow = codepoint; currentHigh = codepoint; }
                else currentHigh = codepoint;
            }
            else if (currentLow != null)
            {
                list.Add(new Interval(currentLow.Value, currentHigh));
                currentLow = null;
            }
        }
        if (currentLow != null) list.Add(new Interval(currentLow.Value, currentHigh));
        return new TokenSet(list.ToArray());
    }

    // Mirror of LineTerminators contents, used by BuildInlineWhitespace
    // at type-init time. Kept as an inline check so we don't depend on
    // the LineTerminators field initialization order.
    private static bool IsLineTerminator(int codepoint) =>
        codepoint == 0x000A   // LF
        || codepoint == 0x000B   // VT
        || codepoint == 0x000C   // FF
        || codepoint == 0x000D   // CR
        || codepoint == 0x0085   // NEL
        || codepoint == 0x2028   // LS
        || codepoint == 0x2029;  // PS

    // Scan 0..0x10FFFF once and populate the cache with a TokenSet for every
    // requested category that isn't already cached. Each code point's
    // UnicodeCategory is looked up exactly once and compared against every
    // target in the missing list. So asking for one category costs one
    // full scan with one compare per codepoint. Asking for five (the
    // Letters case) costs one full scan with five compares per codepoint,
    // not five full scans.
    //
    // The scan's expensive part is GetUnicodeCategory (internal table
    // lookup in the BCL). Comparing enum values is nearly free. Batching
    // saves (N - 1) * ~1M category lookups when bootstrapping a composite
    // like Letters.
    //
    // Safe under concurrent callers: TryAdd is atomic, and two threads
    // both computing the same category just mean the second result is
    // discarded.
    private static void BuildCategories(UnicodeCategory[] targets)
    {
        // Filter to just the categories that aren't already cached.
        // Nothing forces callers to check first, so this method does it.
        var missing = new List<UnicodeCategory>();
        foreach (var target in targets)
            if (!_categoryCache.ContainsKey(target) && !missing.Contains(target))
                missing.Add(target);
        if (missing.Count == 0) return;

        // Parallel state, one slot per missing target.
        var lists = new List<Interval>[missing.Count];
        var currentLows = new int?[missing.Count];
        var currentHighs = new int[missing.Count];
        for (int index = 0; index < missing.Count; index++)
            lists[index] = new List<Interval>();

        for (int codepoint = 0; codepoint <= 0x10FFFF; codepoint++)
        {
            // Skip the surrogate block. These code units exist to encode
            // supplementary-plane code points as UTF-16 pairs. They aren't
            // valid Unicode scalar values (runes) on their own.
            if (codepoint >= 0xD800 && codepoint <= 0xDFFF) continue;

            // CharUnicodeInfo.GetUnicodeCategory has a (char) overload for
            // code points that fit in one UTF-16 char (U+0000..U+FFFF) and
            // a (string, int) overload for supplementary-plane code points.
            // We pick the cheaper path for the single-char half.
            UnicodeCategory category;
            if (codepoint <= 0xFFFF)
                category = CharUnicodeInfo.GetUnicodeCategory((char)codepoint);
            else
                category = CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32(codepoint), 0);

            for (int index = 0; index < missing.Count; index++)
            {
                if (category == missing[index])
                {
                    if (currentLows[index] == null) { currentLows[index] = codepoint; currentHighs[index] = codepoint; }
                    else currentHighs[index] = codepoint;
                }
                else if (currentLows[index] != null)
                {
                    lists[index].Add(new Interval(currentLows[index]!.Value, currentHighs[index]));
                    currentLows[index] = null;
                }
            }
        }

        // Close any still-open intervals, materialize TokenSets, publish to cache.
        for (int index = 0; index < missing.Count; index++)
        {
            if (currentLows[index] != null)
                lists[index].Add(new Interval(currentLows[index]!.Value, currentHighs[index]));
            _categoryCache.TryAdd(missing[index], new TokenSet(lists[index].ToArray()));
        }
    }
}
