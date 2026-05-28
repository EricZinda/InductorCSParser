using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using InductorParser.Lexing;

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
//     var emojiOrLetters    = TokenSet.Letters | TokenSet.Graphemes(USFlagGrapheme);
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

    /// <summary>
    /// True when this set has any multi-rune grapheme members
    /// (e.g. CRLF, a skin-toned emoji, a ZWJ family).
    /// </summary>
    /// <remarks>
    /// Rules that need to choose between a rune-only fast path and a
    /// grapheme-aware path branch on this once at entry, not per-token.
    /// </remarks>
    public bool HasMultiRuneGraphemes =>
        _multiRuneGraphemes != null && _multiRuneGraphemes.Length > 0;

    // The rune-only portion of this set. Used by rules that need to
    // build a complement (NoneOfRule, ScanUntilRule) when the original
    // set has multi-rune entries: ~set throws on a mixed set, so callers
    // first project down to the rune-only part and then complement.
    internal TokenSet RunesOnlyPart =>
        HasMultiRuneGraphemes ? new TokenSet(_ranges) : this;

    // A rune-only set covering every possible first rune of any member
    // of this set. Single-rune members contribute themselves;
    // multi-rune members contribute their first rune. Returns this
    // unchanged when there are no multi-rune entries.
    //
    // Used by primitives that operate strictly on rune intervals and
    // can't query multi-rune entries directly: the scanner-skip path
    // (Lexer.AdvanceUntilRuneIn requires a rune-only set). CannotMatchLookahead does the
    // equivalent first-rune-or-cluster check inline so it doesn't need
    // the inflation.
    internal TokenSet LookaheadFirstRunes
    {
        get
        {
            if (!HasMultiRuneGraphemes) return this;
            var firstRunes = RunesOnlyPart;
            foreach (string grapheme in MultiRuneGraphemes)
            {
                int firstRune;
                if (SurrogateHelpers.IsSurrogatePairAt(grapheme, 0))
                {
                    firstRune = char.ConvertToUtf32(grapheme[0], grapheme[1]);
                }
                else
                {
                    firstRune = grapheme[0];
                }
                firstRunes = firstRunes | Single(firstRune);
            }
            return firstRunes;
        }
    }

    // Read-only view of the multi-rune graphemes, sorted ordinal. Used
    // by rules that need to walk the grapheme entries (LookaheadFirstRunes
    // above is the canonical caller). Empty when the set has no
    // multi-rune content.
    internal ReadOnlySpan<string> MultiRuneGraphemes =>
        _multiRuneGraphemes ?? Array.Empty<string>();

    public bool ContainsRune(int codepoint)
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

    public bool ContainsRune(char c) => ContainsRune((int)c);
    public bool ContainsRune(Rune r) => ContainsRune(r.Value);

    public bool ContainsToken(string grapheme)
    {
        if (grapheme == null) throw new ArgumentNullException(nameof(grapheme));
        return ContainsToken(grapheme.AsSpan());
    }

    // Span overload so rules can probe a token's Chars without building
    // a string. Three token shapes:
    //   * Single-rune span (1 char BMP, or surrogate-paired 2 chars):
    //     hit the rune intervals via Contains(int).
    //   * Lone-surrogate span (1 char that's a high or low surrogate
    //     without its pair): hit the rune intervals using the surrogate's
    //     UTF-16 code unit. A surrogate isn't a Unicode scalar value,
    //     but a user-typed Range that covers the surrogate range under
    //     an unnormalized Compile should still match it.
    //   * Multi-rune span (2+ chars that aren't a surrogate pair):
    //     binary search the multi-rune array. Sets with no multi-rune
    //     entries skip the search entirely.
    internal bool ContainsToken(ReadOnlySpan<char> grapheme)
    {
        if (grapheme.Length == 0) return false;
        if (grapheme.Length == 1 && char.IsSurrogate(grapheme[0]))
            return ContainsRune((int)grapheme[0]);
        if (TrySingleRune(grapheme, out int runeValue))
            return ContainsRune(runeValue);
        // Cache to local + null-check, matching the Contains(int) shape.
        // default(TokenSet) (= TokenSet.Empty) leaves _multiRuneGraphemes
        // null because the field-coalescing constructor never ran on it.
        var multi = _multiRuneGraphemes;
        return multi != null && multi.Length > 0 && BinarySearchMultiRuneGrapheme(grapheme) >= 0;
    }

    // The MultiRuneGraphemes ReadOnlySpan accessor can't cross a yield
    // boundary in a C# iterator. This list-shaped variant lets the
    // OneOfRule / NoneOfRule Compile-time validation walk multi-rune
    // entries via an index with no allocations other than the wrapping array
    // (which is the existing _multiRuneGraphemes field).
    internal IReadOnlyList<string> MultiRuneGraphemesAsList =>
        _multiRuneGraphemes ?? Array.Empty<string>();

    // Yield every rune in this set's _ranges (which are single-rune entries).
    // Used by OneOfRule / NoneOfRule's Compile-time validation walks.
    // Cost is O(total range size) � for big sets like Letters that's
    // ~130K iterations.
    //
    // Skips the surrogate gap (U+D800..U+DFFF). Surrogates aren't
    // runes � they're UTF-16 code units only, not Unicode scalar values.
    internal IEnumerable<int> EnumerateRunes()
    {
        var ranges = _ranges;
        if (ranges == null) yield break;
        for (int index = 0; index < ranges.Length; index++)
        {
            int low = ranges[index].Low;
            int high = ranges[index].High;
            for (int rune = low; rune <= high; rune++)
            {
                if (rune >= 0xD800 && rune <= 0xDFFF) continue;
                yield return rune;
            }
        }
    }

    // Form-project this TokenSet: every entry E becomes Normalize(E, form).
    // Used by OneOfRule / NoneOfRule's Compile-time pipeline so the set's
    // entries are in the same canonical (or compatibility) form the lexer
    // will produce on match-time input. Under FormC the lexer emits NFC,
    // so the set must contain NFC entries; under FormD it emits NFD, etc.
    //
    // The replacement is exact, not additive: an entry whose form-projection
    // differs is REPLACED by the projection, not augmented. The original
    // is unreachable under that form � the lexer never produces it � so
    // keeping it would just be dead weight in the set.
    //
    // Maintains the TokenSet invariant that every entry is exactly one
    // grapheme. If an entry's projection is multi-grapheme (a
    // compatibility conversion like '?' -> "fi" under FormKC, two
    // graphemes), the entry is excluded from the result and recorded
    // in `multiGraphemeConversions` for the caller to surface as an
    // offender. The caller is OneOfRule / NoneOfRule, which match one
    // grapheme per token � a multi-grapheme entry can never match
    // anything in isolation, so dropping it and reporting via the
    // offenders list gives users a clear Compile-time error pointing
    // at the fix.
    //
    // Surrogate runes (U+D800..U+DFFF) pass through unchanged. Surrogates
    // aren't runes � string.Normalize throws on them � and a normalized
    // Compile won't see them in input, so leaving them in the set is
    // harmless and preserves the unnormalized-Compile semantics that some
    // grammars rely on.
    //
    // Entries whose Normalize call throws ArgumentException (the BCL's
    // way of saying "I won't normalize this") are dropped silently. The
    // exact set of rejected code points varies by runtime � Windows NLS
    // and Linux ICU don't agree � so we don't catalog them here. Inputs
    // the lexer can produce go through the same Normalize call and would
    // hit the same rejection, so a dropped entry can't match anything
    // the rule would otherwise have seen.
    internal TokenSet NormalizedFor(NormalizationForm form, List<(string original, string normalized)>? multiGraphemeConversions = null)
    {
        // The body walks every rune in every range and P/Invokes
        // IsNormalized per rune. On a built-in the size of TokenSet.Letters
        // (~tens of thousands of code points across Lu/Ll/Lt/Lm/Lo) that's
        // ~10-20ms per call. Without this cache, every Compile of a
        // grammar with N OneOf(TokenSet.Letters) leaves repeats the same
        // walk N times for the same (set, form) pair, since TokenSet is
        // immutable and the projection is a pure function of its inputs.
        // 960 IdAssignment sweep cases with 11 leaves each was paying
        // ~150s in tests for an answer that's the same every time.
        var key = (this, form);
        if (_normalizedCache.TryGetValue(key, out var hit))
        {
            if (multiGraphemeConversions != null && hit.Conversions.Count > 0)
                multiGraphemeConversions.AddRange(hit.Conversions);
            return hit.Result;
        }

        // Collect conversions into a local list (not the caller's,
        // possibly-null one) so the cache entry holds the conversions
        // alongside the projected set. A cache hit replays the
        // conversions into whatever list the caller passed.
        var localConversions = new List<(string original, string normalized)>();
        bool changed = false;
        var newIntervals = new List<Interval>();
        var newGraphemes = new List<string>();

        var ranges = _ranges;
        if (ranges != null)
        {
            for (int index = 0; index < ranges.Length; index++)
            {
                int low = ranges[index].Low;
                int high = ranges[index].High;
                for (int rune = low; rune <= high; rune++)
                {
                    if (rune >= 0xD800 && rune <= 0xDFFF)
                    {
                        newIntervals.Add(new Interval(rune, rune));
                        continue;
                    }
                    string runeString = char.ConvertFromUtf32(rune);
                    string normalized;
                    try
                    {
                        if (runeString.IsNormalized(form))
                        {
                            newIntervals.Add(new Interval(rune, rune));
                            continue;
                        }
                        normalized = runeString.Normalize(form);
                    }
                    catch (ArgumentException)
                    {
                        changed = true;
                        continue;
                    }
                    changed = true;
                    AddProjectedEntry(runeString, normalized, newIntervals, newGraphemes, localConversions);
                }
            }
        }

        var multiRune = _multiRuneGraphemes;
        if (multiRune != null)
        {
            foreach (var entry in multiRune)
            {
                string normalized;
                try
                {
                    if (entry.IsNormalized(form))
                    {
                        newGraphemes.Add(entry);
                        continue;
                    }
                    normalized = entry.Normalize(form);
                }
                catch (ArgumentException)
                {
                    changed = true;
                    continue;
                }
                changed = true;
                AddProjectedEntry(entry, normalized, newIntervals, newGraphemes, localConversions);
            }
        }

        TokenSet result;
        if (!changed)
        {
            result = this;
        }
        else
        {
            var graphemes = newGraphemes.Count == 0 ? null : NormalizeGraphemes(newGraphemes);
            result = new TokenSet(Normalize(newIntervals), graphemes);
        }

        IReadOnlyList<(string Original, string Normalized)> cachedConversions =
            localConversions.Count == 0
                ? Array.Empty<(string, string)>()
                : localConversions.ConvertAll(c => (c.original, c.normalized));
        _normalizedCache.TryAdd(key, new CachedProjection(result, cachedConversions));

        if (multiGraphemeConversions != null && localConversions.Count > 0)
            multiGraphemeConversions.AddRange(localConversions);

        return result;
    }

    // The (input set, form) pair determines the projected set and the
    // conversions emitted, so caching on the pair is safe. Concurrent
    // because nothing else in TokenSet holds a lock and built-ins like
    // TokenSet.Letters can be touched by multiple threads racing on
    // first Compile. TokenSet has value-based Equals / GetHashCode, so
    // two sets built through different code paths that hold the same
    // tokens share an entry.
    private sealed record CachedProjection(
        TokenSet Result,
        IReadOnlyList<(string Original, string Normalized)> Conversions);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        (TokenSet Source, NormalizationForm Form), CachedProjection> _normalizedCache = new();

    // Helper: place `normalized` into the right bucket (intervals for
    // single-rune, graphemes for multi-rune-but-single-grapheme), or report
    // it as a multi-grapheme conversion and skip it.
    private static void AddProjectedEntry(
        string original, string normalized,
        List<Interval> intervals, List<string> graphemes,
        List<(string original, string normalized)>? multiGraphemeConversions)
    {
        if (TrySingleRune(normalized, out int newRune))
        {
            intervals.Add(new Interval(newRune, newRune));
            return;
        }
        if (CountGraphemes(normalized) <= 1)
        {
            graphemes.Add(normalized);
            return;
        }
        // Multi-grapheme conversion: drop from the projected set so the
        // single-grapheme invariant holds, and report so the caller can
        // surface a clear Compile-time error.
        multiGraphemeConversions?.Add((original, normalized));
    }

    // Explicit opt-in for sets that contain entries whose Normalize(form)
    // produces a multi-grapheme sequence (a compatibility conversion like
    // '?' -> "fi" under FormKC, where one source grapheme lexes as two
    // output tokens). Replaces every such entry with its individual
    // graphemes as separate set members. Single-grapheme entries are
    // left alone for NormalizedFor to project at Compile time.
    //
    // Why opt-in instead of automatic: for a singleton set like
    // OneOf("?"), expansion would silently change semantics ("match this
    // ligature" becomes "match an 'f' or 'i' token"). The user has to
    // ask for that. For a category set like OneOf(XidStart), the
    // expansion adds 'f' and 'i' which were already members, so the
    // result is functionally unchanged but the convertible original
    // entries are removed from the set � making it Compile-safe under
    // FormKC.
    //
    // Canonical forms (FormC, FormD) don't produce multi-grapheme results,
    // so calling this with FormC or FormD is a no-op.
    public TokenSet WithCompatibilityEquivalents(NormalizationForm form)
    {
        bool changed = false;
        var newIntervals = new List<Interval>();
        var newGraphemes = new List<string>();

        var ranges = _ranges;
        if (ranges != null)
        {
            for (int index = 0; index < ranges.Length; index++)
            {
                int low = ranges[index].Low;
                int high = ranges[index].High;
                for (int rune = low; rune <= high; rune++)
                {
                    if (rune >= 0xD800 && rune <= 0xDFFF)
                    {
                        newIntervals.Add(new Interval(rune, rune));
                        continue;
                    }
                    if (!TryGetMultiGraphemeConversion(char.ConvertFromUtf32(rune), form, out string? converted))
                    {
                        newIntervals.Add(new Interval(rune, rune));
                        continue;
                    }
                    AddGraphemePieces(converted, newIntervals, newGraphemes);
                    changed = true;
                }
            }
        }

        var multiRune = _multiRuneGraphemes;
        if (multiRune != null)
        {
            foreach (var entry in multiRune)
            {
                if (!TryGetMultiGraphemeConversion(entry, form, out string? converted))
                {
                    newGraphemes.Add(entry);
                    continue;
                }
                AddGraphemePieces(converted, newIntervals, newGraphemes);
                changed = true;
            }
        }

        if (!changed) return this;
        var graphemes = newGraphemes.Count == 0 ? null : NormalizeGraphemes(newGraphemes);
        return new TokenSet(Normalize(newIntervals), graphemes);
    }

    // Returns true and sets `converted` if `entry`'s normalization
    // under `form` is a multi-grapheme sequence. Returns false otherwise
    // (entry is already form-stable, single-grapheme conversion, or
    // noncharacter).
    private static bool TryGetMultiGraphemeConversion(string entry, NormalizationForm form, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? converted)
    {
        converted = null;
        try
        {
            if (entry.IsNormalized(form)) return false;
            string normalized = entry.Normalize(form);
            if (CountGraphemes(normalized) <= 1) return false;
            converted = normalized;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void AddGraphemePieces(string text, List<Interval> intervals, List<string> graphemes)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            string grapheme = (string)enumerator.Current;
            if (TrySingleRune(grapheme, out int rune))
                intervals.Add(new Interval(rune, rune));
            else
                graphemes.Add(grapheme);
        }
    }

    private static int CountGraphemes(string text)
    {
        if (text.Length == 0) return 0;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        int count = 0;
        while (enumerator.MoveNext()) count++;
        return count;
    }

    // True iff the string is exactly one Unicode rune (one non-surrogate
    // UTF-16 char, or one surrogate pair), giving its code point. internal
    // so rules like GraphemeRule can reuse it instead of duplicating the
    // surrogate-pair decode. The span overload below stays private.
    internal static bool TrySingleRune(string grapheme, out int runeValue) =>
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
        if (grapheme.Length == 2 && SurrogateHelpers.IsSurrogatePairAt(grapheme, 0))
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
    private int BinarySearchMultiRuneGrapheme(ReadOnlySpan<char> target)
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
    // special quoting (e.g. `[a-z,????,????]`), except that a control
    // character or line/paragraph separator inside a grapheme renders as
    // U+XXXX so a cluster like the CRLF entry in LineTerminators /
    // Ascii.AnyWhitespace can't inject a raw newline into a one-line
    // trace. Classes with more
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
            AppendGraphemeForDisplay(sb, multi![index]);
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

    // Append a multi-rune grapheme entry for ToString. Most multi-rune
    // entries are printable user-perceived characters (skin-toned emoji,
    // ZWJ families, regional-indicator flags) and render verbatim so a
    // reader sees the actual character. But a grapheme can also carry a
    // character that doesn't stay on one line: the CRLF cluster in
    // LineTerminators and Ascii.AnyWhitespace is U+000D U+000A. Appending
    // those verbatim would splice a raw carriage return and line feed
    // into the rendered string, and since rules cache ToString() into
    // the text they put on every trace line, one logical trace line
    // would break into three physical lines. Single-rune entries already
    // render non-printing characters as U+XXXX via RenderCodepoint; this
    // gives multi-rune ones the same treatment, char by char, while
    // leaving every printable char (including the surrogate halves of a
    // supplementary-plane emoji) untouched so the character still shows
    // through.
    private static void AppendGraphemeForDisplay(StringBuilder sb, string grapheme)
    {
        foreach (char c in grapheme)
        {
            if (IsControlOrLineSeparator(c))
                sb.Append("U+").Append(((int)c).ToString("X4"));
            else
                sb.Append(c);
        }
    }

    // True for characters that corrupt or vanish from a single-line trace
    // when written verbatim. Read straight from the Unicode
    // General_Category tables the BCL ships, so it tracks new Unicode
    // versions automatically instead of from a hand-kept code-point list:
    //   * Control (Cc): LF, CR, VT, FF, NEL, and the rest of the C0/C1
    //     block. (This is exactly what char.IsControl reports.)
    //   * LineSeparator (Zl): U+2028.
    //   * ParagraphSeparator (Zp): U+2029.
    // Format characters (Cf) such as ZWJ are deliberately NOT included.
    // ZWJ is the invisible glue inside emoji ZWJ families and similar
    // clusters we want to render as the user-perceived character, and it
    // doesn't break the line. Every line-breaking and control character
    // lives in the BMP, so the per-char (vs per-rune) lookup is enough:
    // the surrogate halves of a supplementary-plane character report as
    // Surrogate and fall through to the verbatim path, reassembling the
    // original character.
    private static bool IsControlOrLineSeparator(char c)
    {
        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
        return category == UnicodeCategory.Control
            || category == UnicodeCategory.LineSeparator
            || category == UnicodeCategory.ParagraphSeparator;
    }

    // The empty set, containing no runes. Equivalent to default(TokenSet),
    // exposed as a named constant so callers can write TokenSet.Empty
    // instead of relying on "default happens to mean empty."
    public static readonly TokenSet Empty = default;

    // The scalar-value universe: every code point in 0..0x10FFFF except
    // the surrogate block 0xD800..0xDFFF. Equal to ~Empty, since
    // operator ~ complements over scalar values only. Used as the
    // "unknown / anything goes" default for FirstConsumedTokens (see
    // RuleStartRequirements) so rules with no tighter information never
    // get filtered out, and the default is safe by construction: a
    // grammar with no opinion about surrogates won't quietly admit them.
    public static readonly TokenSet Universe = ~default(TokenSet);

    // All surrogate code units U+D800..U+DFFF as a TokenSet. Matchable
    // only by grammars compiled with Compile(null), where the lexer
    // surfaces a lone surrogate as a one-char token. Under the default
    // Compile(FormC), the lexer pre-rejects lone surrogates from input,
    // so this set has nothing to match against.
    //
    // The two named entry points for putting surrogates into a TokenSet
    // are this constant (the whole block) and SurrogateRange (a sub-
    // block). Range, Single, and Runes reject surrogate endpoints /
    // arguments, and operator ~ never fabricates surrogates from a
    // surrogate-free input. So Surrogates and SurrogateRange are the
    // only places fresh surrogates come from; from there union and
    // intersection move them between sets, and complement strips them
    // out. A grammar that doesn't say "Surrogates" or "SurrogateRange"
    // never gets one in any TokenSet it builds.
    public static readonly TokenSet Surrogates =
        new TokenSet(new[] { new Interval(0xD800, 0xDFFF) });

    public static TokenSet Single(char c) => Single((int)c);
    public static TokenSet Single(Rune r) => Single(r.Value);
    public static TokenSet Single(int codepoint)
    {
        ValidateScalarValue(codepoint, nameof(codepoint));
        return new TokenSet(new[] { new Interval(codepoint, codepoint) });
    }

    public static TokenSet Range(char low, char high) => Range((int)low, (int)high);
    public static TokenSet Range(Rune low, Rune high) => Range(low.Value, high.Value);
    // Builds a TokenSet of scalar values in [low, high]. Both endpoints
    // must be valid Unicode scalar values (no surrogate halves) and a
    // range that straddles the surrogate block is split into two
    // intervals so the result contains no surrogate code units. So
    // Range(0, 0x10FFFF) is the set of every scalar value, with no
    // surrogates, and that's the only thing it can mean. To get
    // surrogates into a set, name them with Surrogates or SurrogateRange
    // and union them in.
    public static TokenSet Range(int low, int high)
    {
        ValidateScalarValue(low, nameof(low));
        ValidateScalarValue(high, nameof(high));
        if (high < low) throw new ArgumentException("high must be >= low");
        if (low < SurrogateLow && high > SurrogateHigh)
            return new TokenSet(new[]
            {
                new Interval(low, SurrogateLow - 1),
                new Interval(SurrogateHigh + 1, high),
            });
        return new TokenSet(new[] { new Interval(low, high) });
    }

    // Builds a TokenSet covering [low, high] inside the surrogate block
    // 0xD800..0xDFFF. Both endpoints must themselves be surrogate code
    // units. Use this to select a specific sub-range of the surrogate
    // block (just the leading-surrogate half, just the trailing-surrogate
    // half, etc.); for the whole block use the Surrogates constant.
    //
    // The result is matchable only by grammars compiled with
    // Compile(null), where the lexer surfaces lone surrogates as one-char
    // tokens. Under the default Compile(FormC) the lexer pre-rejects
    // lone surrogates from input, so the set has nothing to match.
    public static TokenSet SurrogateRange(int low, int high)
    {
        if (low < SurrogateLow || low > SurrogateHigh)
            throw new ArgumentOutOfRangeException(nameof(low), low,
                "Must be a surrogate code unit (0xD800..0xDFFF). " +
                "Use Range for scalar-value ranges.");
        if (high < SurrogateLow || high > SurrogateHigh)
            throw new ArgumentOutOfRangeException(nameof(high), high,
                "Must be a surrogate code unit (0xD800..0xDFFF). " +
                "Use Range for scalar-value ranges.");
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

    /// <summary>
    /// Build a set whose members are the runes (Unicode scalar values)
    /// in <paramref name="text"/>. Each rune of the input becomes one
    /// set element. The input is walked grapheme by grapheme, and a
    /// grapheme that spans more than one rune throws: the caller
    /// passing <c>"\r\n"</c> or NFD <c>"e + U+0301"</c> almost
    /// certainly wanted either the cluster (use
    /// <see cref="Graphemes(string[])"/>) or the scalars built
    /// explicitly (<c>Single(c1) | Single(c2)</c>), not whatever a
    /// silent rune-walk would have produced. Use this when you want
    /// "the set of these N characters" from a string of ASCII or
    /// otherwise non-combining scalars.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="text"/> contains a multi-rune grapheme cluster
    /// (CRLF, NFD accent, ZWJ emoji, skin-toned face, regional flag,
    /// or similar) or a lone surrogate half.
    /// </exception>
    public static TokenSet Runes(string text)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        var intervals = new List<Interval>();
        int index = 0;
        while (index < text.Length)
        {
            string grapheme = StringInfo.GetNextTextElement(text, index);
            int rune;
            int consumed;
            if (SurrogateHelpers.IsSurrogatePairAt(text, index))
            {
                rune = char.ConvertToUtf32(text[index], text[index + 1]);
                consumed = 2;
            }
            else
            {
                rune = text[index];
                consumed = 1;
            }
            if (grapheme.Length != consumed)
                throw new ArgumentException(
                    $"Runes(string) input \"{text}\" contains a multi-rune grapheme cluster (\"{grapheme}\") at UTF-16 offset {index}. " +
                    "Runes(string) walks rune by rune and rejects inputs where consecutive runes form one cluster. " +
                    "If you want a set of grapheme clusters, use Graphemes(string[]). " +
                    "If you want separate scalars that visually combine, build the set explicitly with Single(c1) | Single(c2).",
                    nameof(text));
            if (!Rune.IsValid(rune))
                throw new ArgumentException(
                    $"Runes(string) encountered an invalid Unicode scalar value (0x{rune:X4}) at UTF-16 offset {index}. " +
                    "Lone surrogate halves aren't valid scalars.",
                    nameof(text));
            intervals.Add(new Interval(rune, rune));
            index += consumed;
        }
        return new TokenSet(Normalize(intervals));
    }

    /// <summary>
    /// Build a set whose members are one or more grapheme clusters.
    /// Each array element must be exactly one cluster (one full UAX #29
    /// text element); passing a string with multiple clusters throws.
    /// Use this when the set members are clusters: CRLF, NFD
    /// <c>a + U+0301</c>, ZWJ emoji, skin-toned faces,
    /// regional-indicator flags. For sets of runes (the common ASCII
    /// case), use <see cref="Runes(string)"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="clusters"/> contains a null element, an empty
    /// element, an element that parses as more than one grapheme
    /// cluster, or an element containing a lone surrogate half.
    /// </exception>
    public static TokenSet Graphemes(params string[] clusters)
    {
        if (clusters == null) throw new ArgumentNullException(nameof(clusters));
        var intervals = new List<Interval>();
        List<string>? multiRuneGraphemes = null;
        for (int clusterIndex = 0; clusterIndex < clusters.Length; clusterIndex++)
        {
            string cluster = clusters[clusterIndex];
            if (cluster == null)
                throw new ArgumentException(
                    $"Graphemes element at index {clusterIndex} is null.",
                    nameof(clusters));
            if (cluster.Length == 0)
                throw new ArgumentException(
                    $"Graphemes element at index {clusterIndex} is empty. Each element must be exactly one grapheme cluster.",
                    nameof(clusters));
            string firstCluster = StringInfo.GetNextTextElement(cluster, 0);
            if (firstCluster.Length != cluster.Length)
                throw new ArgumentException(
                    $"Graphemes element at index {clusterIndex} (\"{cluster}\") contains more than one grapheme cluster. " +
                    "Each element must be exactly one cluster. " +
                    "If you want a set of runes, use Runes(string).",
                    nameof(clusters));

            int firstRune;
            int firstRuneLength;
            if (SurrogateHelpers.IsSurrogatePairAt(cluster, 0))
            {
                firstRune = char.ConvertToUtf32(cluster[0], cluster[1]);
                firstRuneLength = 2;
            }
            else
            {
                firstRune = cluster[0];
                firstRuneLength = 1;
            }
            if (!Rune.IsValid(firstRune))
                throw new ArgumentException(
                    $"Graphemes element at index {clusterIndex} starts with an invalid Unicode scalar (0x{firstRune:X4}). " +
                    "Lone surrogate halves aren't valid scalars.",
                    nameof(clusters));

            if (cluster.Length == firstRuneLength)
            {
                intervals.Add(new Interval(firstRune, firstRune));
            }
            else
            {
                ValidateGraphemeRunes(cluster, 0);
                multiRuneGraphemes ??= new List<string>();
                multiRuneGraphemes.Add(cluster);
            }
        }
        return new TokenSet(Normalize(intervals), NormalizeGraphemes(multiRuneGraphemes));
    }

    // Walks a grapheme's runes by hand and throws on any lone surrogate
    // half. We've already validated the first rune in the outer loop;
    // this is for runes 2..N of a multi-rune grapheme.
    private static void ValidateGraphemeRunes(string grapheme, int graphemeStart)
    {
        for (int runeIndex = 0; runeIndex < grapheme.Length;)
        {
            int runeCodepoint;
            if (SurrogateHelpers.IsSurrogatePairAt(grapheme, runeIndex))
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
                    $"Graphemes element at UTF-16 offset {graphemeStart} contains an invalid Unicode scalar value (0x{runeCodepoint:X4}). " +
                    "Lone surrogate halves aren't valid scalars.",
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
        var mergedGraphemes = MergeMultiRuneGraphemeUnion(a._multiRuneGraphemes, b._multiRuneGraphemes);
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
        var mergedGraphemes = MergeMultiRuneGraphemeIntersect(a._multiRuneGraphemes, b._multiRuneGraphemes);
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

    // Complement over the scalar-value universe. The result is always
    // surrogate-free, regardless of whether the input had surrogates.
    // So ~Letters is "every scalar non-letter" without quietly admitting
    // U+D800..U+DFFF, and ~Empty is the scalar universe (also exposed
    // as TokenSet.Universe). A grammar that wants surrogates in the
    // complement writes `~set | Surrogates`. The rule is: surrogates
    // enter a TokenSet through the Surrogates constant or SurrogateRange
    // and never through a complement. Union and intersection move them
    // between sets, but ~ never fabricates them.
    //
    // The cost is that ~ is not involutive across the surrogate
    // boundary: ~~Surrogates is the scalar universe, not Surrogates,
    // because the first ~ strips Surrogates' code units and the second
    // ~ can't put them back. The opposite ordering (~ as true complement
    // and surrogates riding along in ~A whenever A doesn't have them)
    // would buy involution at the cost of NoneOf(Letters) silently
    // matching a lone surrogate under Compile(null). The surface every
    // author hits wins.
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
        var inputRanges = a._ranges;
        var result = new List<Interval>();
        int cursor = MinScalarValue;
        if (inputRanges != null)
        {
            for (int index = 0; index < inputRanges.Length; index++)
            {
                if (cursor < inputRanges[index].Low)
                    EmitScalarInterval(result, cursor, inputRanges[index].Low - 1);
                cursor = inputRanges[index].High + 1;
            }
        }
        if (cursor <= MaxScalarValue)
            EmitScalarInterval(result, cursor, MaxScalarValue);
        return new TokenSet(result.ToArray());
    }

    // Emit [low, high] into result, splitting around the surrogate block
    // if the interval straddles it so the result holds only scalar
    // values. Called from operator ~ to keep surrogates out of complement
    // results.
    private static void EmitScalarInterval(List<Interval> result, int low, int high)
    {
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

    // Sorted-merge union of two sorted-ordinal grapheme arrays. Linear
    // in the sum of the two array lengths. Skips duplicates so the
    // result stays canonical. Fast-paths when either side is empty so
    // rune-only sets pay no allocation past the empty-array sentinel.
    private static string[] MergeMultiRuneGraphemeUnion(string[]? a, string[]? b)
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
    private static string[] MergeMultiRuneGraphemeIntersect(string[]? a, string[]? b)
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

    // Boundary constants for the surrogate code-unit block (UTF-16
    // surrogate halves, not valid Unicode scalar values) and the closed
    // [0, 0x10FFFF] code-point universe. Used by Range, SurrogateRange,
    // operator ~, and the per-rune walks in NormalizedFor /
    // WithCompatibilityEquivalents / BuildCategories /
    // BuildInlineWhitespace.
    private const int SurrogateLow = 0xD800;
    private const int SurrogateHigh = 0xDFFF;
    private const int MinScalarValue = 0;
    private const int MaxScalarValue = 0x10FFFF;

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
    // accepts 'a' through 'z' plus '�' and '?' and '?'.
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
        | Single(0x2029)     // PS
        | Graphemes("\r\n"); // CRLF cluster

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
        public static readonly TokenSet AnyWhitespace = InlineWhitespace | Single('\r') | Single('\n') | Graphemes("\r\n");
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
