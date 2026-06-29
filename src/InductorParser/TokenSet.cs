using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using InductorParser.Lexing;
using InductorParser.Tracing;

namespace InductorParser;

/// <summary>
/// A set of tokens, used to describe character classes for OneOf and NoneOf.
/// A token is either a single Unicode scalar value (rune) or a multi-rune
/// grapheme cluster (skin-toned emoji, ZWJ family, regional-indicator pair,
/// base+combining-mark cluster).
/// </summary>
/// <remarks>
/// Build one with the factory methods on this class (e.g. <see cref="Single(int)"/>,
/// <see cref="Range(int, int)"/>) or one of the built-ins (<see cref="Letters"/>,
/// <see cref="Digits"/>),
/// then compose larger classes with the set operators:
/// <code>
///     |   union           a | b           tokens in a or b
///     &amp;   intersection    a &amp; b           tokens in a and b
///     -   difference      a - b           tokens in a but not in b
///     ~   complement      ~a              tokens not in a (rune-only sets)
/// </code>
/// The operators return a new TokenSet. The struct is immutable.
/// <code>
///     var unicodeIdentifier = TokenSet.Letters | TokenSet.Digits | TokenSet.Runes("_");
///     var asciiConsonants   = TokenSet.Ascii.Letters - TokenSet.Runes("aeiouAEIOU");
///     var cyrillicLetters   = TokenSet.Letters &amp; TokenSet.Range(0x0400, 0x04FF);
///     var emojiOrLetters    = TokenSet.Letters | TokenSet.Graphemes(USFlagGrapheme);
/// </code>
/// <para>
/// Internally a TokenSet keeps two pieces. _ranges is a sorted, non-overlapping,
/// non-adjacent array of code-point runs that holds every single-rune member.
/// _multiRuneGraphemes is a sorted ordinal, deduped array of grapheme strings
/// that holds every member that occupies two or more runes. Single-rune
/// graphemes always go in _ranges, never in _multiRuneGraphemes, so a set
/// that's never given a multi-rune entry pays nothing. The rune fast path
/// (binary search of intervals) is unchanged, and a grammar rule that uses
/// TokenSet.Letters a thousand times pays the Unicode-table scan once at
/// startup and then a handful of Contains() calls per match.
/// </para>
/// <para>
/// Complement is only defined when _multiRuneGraphemes is empty. The universe
/// of grapheme clusters is unbounded (any rune sequence respecting UAX #29
/// boundaries is a grapheme), so complement against it can't be represented
/// by a finite explicit set. ~set on a mixed set throws InvalidOperationException
/// rather than silently dropping multi-rune entries. Difference doesn't have
/// that limitation: a - b complements against the bounded set b, not the
/// unbounded cluster universe, so it works on a mixed set and never throws.
/// The a &amp; ~b shorthand works only when a is rune-only (see the operator
/// table above). On a mixed a it drops a's clusters, which is the silent
/// loss a - b avoids.
/// </para>
/// </remarks>
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
                if (RuneHelpers.IsSurrogatePairAt(grapheme, 0))
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

    /// <summary>
    /// Read-only view of the multi-rune grapheme entries, sorted ordinal.
    /// Empty when the set has no multi-rune content. Returns a
    /// <c>ReadOnlySpan</c> so callers get foreach and indexing without
    /// allocation; for the iterator shape that composes with LINQ and
    /// can cross yield boundaries, use
    /// <see cref="EnumerateMultiRuneGraphemes"/> instead.
    /// </summary>
    public ReadOnlySpan<string> MultiRuneGraphemes =>
        _multiRuneGraphemes ?? Array.Empty<string>();

    /// <summary>
    /// Iterator-shaped companion to <see cref="MultiRuneGraphemes"/>.
    /// Yields the same entries in the same order, but as
    /// <c>IEnumerable&lt;string&gt;</c> so LINQ operators apply and the
    /// values can flow through a yield-returning method. The
    /// <c>ReadOnlySpan</c> property is faster (no enumerator, supports
    /// indexing) and should be preferred when the caller doesn't need
    /// either of those features.
    /// </summary>
    public IEnumerable<string> EnumerateMultiRuneGraphemes() =>
        _multiRuneGraphemes ?? Array.Empty<string>();

    // Number of intervals ContainsRune scans linearly before switching to
    // binary search. The first few intervals of every built-in set hold the
    // low code points (ASCII, then Latin-1, then common Latin extensions),
    // which dominate real input even in international grammars. A short
    // linear probe with an early-out finds those (or rules them out) in one
    // or two comparisons, beating binary search's ~log(n) array loads for
    // the common case. Sets with no more intervals than this are pure
    // linear, so small hand-built classes and the Ascii.* sets pay nothing
    // for the binary-search machinery.
    private const int LinearScanPrefix = 8;

    /// <summary>
    /// True when <paramref name="codepoint"/> is a single-rune member of this
    /// set. Multi-rune grapheme members are matched by
    /// <see cref="ContainsToken(string)"/>, not this.
    /// </summary>
    public bool ContainsRune(int codepoint)
    {
        // Normalize guarantees _ranges is sorted ascending by Low,
        // non-overlapping, and non-adjacent, so a code point lands in at most
        // one interval. Scan a short prefix linearly, then binary-search the
        // tail. The built-in sets are large (TokenSet.Letters is ~660
        // intervals, XidContinue ~775) and this is the per-token membership
        // test on the OneOf / NoneOf / ScanWhile / ScanUntil hot path, so the
        // binary search keeps a high code point (CJK / Cyrillic / Hangul, or
        // any miss past the Latin block) at O(log ranges) instead of
        // O(ranges). The linear prefix keeps the common ASCII case at one or
        // two comparisons, where binary search's ~log(n) array loads would be
        // slower.
        var ranges = _ranges;
        if (ranges == null) return false;
        int length = ranges.Length;

        int prefixLimit = length < LinearScanPrefix ? length : LinearScanPrefix;
        int index = 0;
        for (; index < prefixLimit; index++)
        {
            // Sorted intervals: a code point below this interval's Low is
            // below every later interval too, so it's not in the set.
            if (codepoint < ranges[index].Low) return false;
            if (codepoint <= ranges[index].High) return true;
        }
        if (index == length) return false;

        // codepoint is above ranges[prefixLimit - 1].High; binary search the
        // remaining intervals.
        int low = index;
        int high = length - 1;
        while (low <= high)
        {
            int mid = (int)((uint)(low + high) >> 1);
            var interval = ranges[mid];
            if (codepoint < interval.Low) high = mid - 1;
            else if (codepoint > interval.High) low = mid + 1;
            else return true;
        }
        return false;
    }

    /// <summary><see cref="ContainsRune(int)"/> for a <see cref="char"/>.</summary>
    public bool ContainsRune(char c) => ContainsRune((int)c);

    /// <summary><see cref="ContainsRune(int)"/> for a <see cref="Rune"/>.</summary>
    public bool ContainsRune(Rune r) => ContainsRune(r.Value);

    /// <summary>
    /// True when <paramref name="grapheme"/> (one whole token, rune or
    /// multi-rune cluster) is a member of this set. See
    /// <see cref="ContainsToken(ReadOnlySpan{char})"/> for the matching rules.
    /// </summary>
    public bool ContainsToken(string grapheme)
    {
        if (grapheme == null) throw new ArgumentNullException(nameof(grapheme));
        return ContainsToken(grapheme.AsSpan());
    }

    /// <summary>
    /// True when <paramref name="grapheme"/> (one whole token) is a member of
    /// this set, without building a string. Lets rules probe a token's chars
    /// directly.
    /// </summary>
    /// <remarks>
    /// Three token shapes. A single-rune span (1 BMP char, or a surrogate pair)
    /// hits the rune intervals. A lone-surrogate span (one high or low surrogate
    /// without its pair) hits the rune intervals using the surrogate's UTF-16
    /// code unit, so a user-typed Range covering the surrogate range under an
    /// unnormalized Compile still matches it. A multi-rune span (2+ chars that
    /// aren't a surrogate pair) binary-searches the multi-rune array, which
    /// sets with no multi-rune entries skip entirely.
    /// </remarks>
    public bool ContainsToken(ReadOnlySpan<char> grapheme)
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

    /// <summary>
    /// Enumerate every rune (Unicode scalar value) in this set's
    /// single-rune entries. Cost is O(total range size); for big sets like
    /// <see cref="Letters"/> that's ~130K iterations. Skips the surrogate
    /// gap (U+D800..U+DFFF) since surrogates aren't Unicode scalar values;
    /// call <see cref="EnumerateSurrogates"/> for those. A rule that wants
    /// to walk every member of a set iterates all three of
    /// <see cref="EnumerateRunes"/>, <see cref="EnumerateSurrogates"/>, and
    /// <see cref="EnumerateMultiRuneGraphemes"/>.
    /// </summary>
    public IEnumerable<int> EnumerateRunes()
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

    /// <summary>
    /// Enumerate every surrogate code unit (U+D800..U+DFFF) in this set's
    /// single-rune entries. Surrogates aren't Unicode scalar values, so
    /// they're excluded from <see cref="EnumerateRunes"/>; this is the
    /// companion accessor for rules that want to walk them anyway. Empty
    /// for sets with no surrogate entries (the common case, including
    /// every built-in (<see cref="Letters"/>, <see cref="XidStart"/>, etc.)
    /// since the spec subtracts the surrogate gap). Populated by
    /// <see cref="SurrogateRange(int, int)"/>.
    /// </summary>
    public IEnumerable<int> EnumerateSurrogates()
    {
        var ranges = _ranges;
        if (ranges == null) yield break;
        for (int index = 0; index < ranges.Length; index++)
        {
            int low = ranges[index].Low;
            int high = ranges[index].High;
            if (high < 0xD800 || low > 0xDFFF) continue;
            int start = low < 0xD800 ? 0xD800 : low;
            int end = high > 0xDFFF ? 0xDFFF : high;
            for (int s = start; s <= end; s++)
                yield return s;
        }
    }

    /// <summary>
    /// Form-project this set: every entry E becomes Normalize(E, form), so the
    /// set's entries are in the same form the lexer produces on match-time
    /// input. Under FormC the lexer emits NFC, so the set must contain NFC
    /// entries. Used by OneOfRule / NoneOfRule's Compile-time pipeline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ensures that every entry is exactly one grapheme. If an
    /// entry's projection is multi-grapheme (a compatibility conversion like the
    /// fi-ligature to "fi" under FormKC), the entry is excluded from the result
    /// and recorded in <paramref name="multiGraphemeConversions"/> for the
    /// caller to surface as an offender. OneOfRule / NoneOfRule match one
    /// grapheme per token, so a multi-grapheme entry can never match in
    /// isolation, and dropping it with a reported offender gives the user a
    /// clear Compile-time error.
    /// </para>
    /// <para>
    /// Surrogate runes (U+D800..U+DFFF) pass through unchanged: they aren't
    /// runes (string.Normalize throws on them), and a normalized Compile won't
    /// see them in input, so leaving them in is harmless and preserves the
    /// unnormalized-Compile semantics some grammars rely on.
    /// </para>
    /// <para>
    /// Entries whose Normalize call throws ArgumentException (the BCL's way of
    /// saying "I won't normalize this") are dropped silently. The exact set of
    /// rejected code points varies by runtime (Windows NLS and Linux ICU don't
    /// agree), so they aren't cataloged here. Input the lexer produces goes
    /// through the same Normalize call and would hit the same rejection, so a
    /// dropped entry can't match anything the rule would otherwise have seen.
    /// </para>
    /// </remarks>
    public TokenSet NormalizedFor(NormalizationForm form, List<(string original, string normalized)>? multiGraphemeConversions = null)
    {
        // The body walks every rune in every range and P/Invokes
        // IsNormalized per rune, which is expensive on a built-in the size
        // of TokenSet.Letters (tens of thousands of code points across
        // Lu/Ll/Lt/Lm/Lo). Without this cache, every Compile of a grammar
        // with N OneOf(TokenSet.Letters) leaves repeats the same walk N
        // times for the same (set, form) pair, even though TokenSet is
        // immutable and the projection is a pure function of its inputs.
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
        if (GraphemeHelpers.Count(normalized) <= 1)
        {
            graphemes.Add(normalized);
            return;
        }
        // Multi-grapheme conversion: drop from the projected set so the
        // single-grapheme invariant holds, and report so the caller can
        // surface a clear Compile-time error.
        multiGraphemeConversions?.Add((original, normalized));
    }

    /// <summary>
    /// Returns a copy of this set with every non-form-stable entry projected
    /// through <c>Normalize(form)</c>. An entry whose conversion is a single
    /// rune is replaced by that rune. An entry whose conversion is a
    /// single-grapheme multi-rune cluster (e.g. "e + combining acute" under
    /// FormD) is added as a multi-rune grapheme member. An entry whose
    /// conversion is multiple graphemes (e.g. the fi-ligature under FormKC
    /// becomes the two members 'f' and 'i') is split into one member per
    /// cluster. Form-stable entries pass through unchanged.
    /// </summary>
    /// <remarks>
    /// Useful for opting into compatibility equivalence: pass
    /// <see cref="NormalizationForm.FormKC"/> to expand ligatures and
    /// fullwidth forms into their plain counterparts so a downstream
    /// <c>OneOf</c> matches either input shape.
    /// </remarks>
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
                    string entry = char.ConvertFromUtf32(rune);
                    if (!TryGetNormalizedConversion(entry, form, out string? normalized))
                    {
                        newIntervals.Add(new Interval(rune, rune));
                        continue;
                    }
                    AddProjectedConversion(normalized, newIntervals, newGraphemes);
                    changed = true;
                }
            }
        }

        var multiRune = _multiRuneGraphemes;
        if (multiRune != null)
        {
            foreach (var entry in multiRune)
            {
                if (!TryGetNormalizedConversion(entry, form, out string? normalized))
                {
                    newGraphemes.Add(entry);
                    continue;
                }
                AddProjectedConversion(normalized, newIntervals, newGraphemes);
                changed = true;
            }
        }

        if (!changed) return this;
        var graphemes = newGraphemes.Count == 0 ? null : NormalizeGraphemes(newGraphemes);
        return new TokenSet(Normalize(newIntervals), graphemes);
    }

    // Returns true and sets `converted` if `entry` isn't already in `form`
    // (the projection actually changes something). Returns false when
    // `entry` is form-stable or when Normalize throws (in practice, an
    // unpaired surrogate). The caller inspects the conversion's piece count
    // itself, since single-piece and multi-piece projections take different
    // paths through the result builder.
    private static bool TryGetNormalizedConversion(
        string entry, NormalizationForm form,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? converted)
    {
        converted = null;
        try
        {
            if (entry.IsNormalized(form)) return false;
            converted = entry.Normalize(form);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // Place the projected (already-normalized) form into the result's
    // intervals / graphemes buckets. Multi-grapheme outputs route through
    // AddGraphemePieces to split into one member per cluster. Single-grapheme
    // outputs are added verbatim: a single rune lands in intervals, and
    // anything else TrySingleRune rejects (a multi-rune cluster like
    // "e + combining acute" under FormD, or a lone surrogate that isn't a
    // Unicode scalar) lands in graphemes.
    private static void AddProjectedConversion(
        string normalized,
        List<Interval> intervals, List<string> graphemes)
    {
        if (GraphemeHelpers.Count(normalized) > 1)
        {
            AddGraphemePieces(normalized, intervals, graphemes);
            return;
        }
        if (TrySingleRune(normalized, out int singleRune))
        {
            intervals.Add(new Interval(singleRune, singleRune));
            return;
        }
        graphemes.Add(normalized);
    }

    // Splits `text` into its grapheme clusters and adds each as a set member
    // (via AddGraphemePiece). Used when a string spans more than one cluster,
    // such as a ligature that normalizes to several letters.
    private static void AddGraphemePieces(string text, List<Interval> intervals, List<string> graphemes)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            AddGraphemePiece((string)enumerator.Current, intervals, graphemes);
        }
    }

    // Adds one grapheme cluster as a set member: a single rune goes into
    // `intervals` as a one-rune interval, a multi-rune cluster goes into
    // `graphemes`. This is the one place that routes a cluster to the right
    // member kind.
    private static void AddGraphemePiece(string grapheme, List<Interval> intervals, List<string> graphemes)
    {
        if (TrySingleRune(grapheme, out int rune))
            intervals.Add(new Interval(rune, rune));
        else
            graphemes.Add(grapheme);
    }

    /// <summary>
    /// True when <paramref name="grapheme"/> is exactly one Unicode rune (one
    /// non-surrogate UTF-16 char, or one surrogate pair), giving its code point
    /// in <paramref name="runeValue"/>. Exposed so user-defined rules can reuse
    /// it instead of duplicating the surrogate-pair decode.
    /// </summary>
    public static bool TrySingleRune(string grapheme, out int runeValue)
    {
        if (grapheme == null)
            throw new ArgumentNullException(nameof(grapheme));

        return TrySingleRune(grapheme.AsSpan(), out runeValue);
    }

    private static bool TrySingleRune(ReadOnlySpan<char> grapheme, out int runeValue)
    {
        if (grapheme.Length == 1)
        {
            char c = grapheme[0];
            if (char.IsSurrogate(c)) { runeValue = -1; return false; }
            runeValue = c;
            return true;
        }
        if (grapheme.Length == 2 && RuneHelpers.IsSurrogatePairAt(grapheme, 0))
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

    /// <summary>
    /// True when this set has no members (no runes and no multi-rune graphemes).
    /// Equivalent to <c>this == TokenSet.Empty</c>.
    /// </summary>
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

    /// <summary>
    /// Value equality: two TokenSets are equal when they contain the same
    /// tokens.
    /// </summary>
    /// <remarks>
    /// Construction canonicalizes both halves (the rune intervals are sorted,
    /// non-overlapping, and non-adjacent, and the multi-rune array is sorted
    /// ordinal and deduped), so equal sets have identical _ranges and identical
    /// _multiRuneGraphemes element-wise.
    /// </remarks>
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

    /// <summary><see cref="Equals(TokenSet)"/> for an arbitrary object; false for non-TokenSet values.</summary>
    public override bool Equals(object? obj) => obj is TokenSet other && Equals(other);

    /// <summary>
    /// Hash consistent with <see cref="Equals(TokenSet)"/>: equal sets (including
    /// the empty set however it was built) hash equal.
    /// </summary>
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

    /// <summary>Value equality, same as <see cref="Equals(TokenSet)"/>.</summary>
    public static bool operator ==(TokenSet a, TokenSet b) => a.Equals(b);

    /// <summary>Value inequality, the negation of <see cref="Equals(TokenSet)"/>.</summary>
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

    /// <summary>
    /// Human-readable rendering of the set, for trace output and debugger
    /// display. Produces "[a-z,A-Z,0-9]" style output.
    /// </summary>
    /// <remarks>
    /// Single-codepoint ranges collapse to one char and long ranges render as
    /// low-high. Printable ASCII renders as the literal character, everything
    /// else as U+XXXX. Multi-rune graphemes render as the user-perceived
    /// character itself, except that a control character or line/paragraph
    /// separator inside a grapheme renders as U+XXXX so a cluster like the CRLF
    /// entry in LineTerminators / Ascii.AnyWhitespace can't inject a raw newline
    /// into a one-line trace. Sets with more than MaxRenderedEntries entries are
    /// truncated with a "+N more" tail, keeping trace lines legible without
    /// dragging in the entire Unicode database.
    /// </remarks>
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
            DisplayEscape.AppendEscaped(sb, multi![index]);
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

    /// <summary>
    /// The empty set, containing no tokens. Equivalent to default(TokenSet),
    /// exposed as a named constant so callers can write TokenSet.Empty instead
    /// of relying on "default happens to mean empty."
    /// </summary>
    public static readonly TokenSet Empty = default;

    /// <summary>
    /// The scalar-value universe: every code point in 0..0x10FFFF except the
    /// surrogate block 0xD800..0xDFFF. Equal to ~Empty, since operator ~
    /// complements over scalar values only.
    /// </summary>
    /// <remarks>
    /// Used as the "anything goes" default for a rule with no tighter
    /// first-token information, so such a rule never gets filtered out by a
    /// lookahead. Safe by construction: a grammar with no opinion about
    /// surrogates won't quietly admit them.
    /// </remarks>
    public static readonly TokenSet Universe = ~default(TokenSet);

    /// <summary>
    /// All surrogate code units U+D800..U+DFFF as a TokenSet. Matchable only by
    /// grammars compiled with Compile(null), where the lexer surfaces a lone
    /// surrogate as a one-char token. Under the default Compile(FormC) the lexer
    /// pre-rejects lone surrogates from input, so this set has nothing to match.
    /// </summary>
    /// <remarks>
    /// The two named entry points for putting surrogates into a TokenSet are
    /// this constant (the whole block) and <see cref="SurrogateRange"/> (a
    /// sub-block). Range, Single, and Runes reject surrogate endpoints /
    /// arguments, and operator ~ never fabricates surrogates from a
    /// surrogate-free input. So Surrogates and SurrogateRange are the only places
    /// fresh surrogates come from. From there union and intersection move them
    /// between sets, and complement strips them out. A grammar that doesn't name
    /// Surrogates or SurrogateRange never gets one in any TokenSet it builds.
    /// </remarks>
    public static readonly TokenSet Surrogates =
        new TokenSet(new[] { new Interval(0xD800, 0xDFFF) });

    /// <summary><see cref="Single(int)"/> for a <see cref="char"/>.</summary>
    public static TokenSet Single(char c) => Single((int)c);

    /// <summary><see cref="Single(int)"/> for a <see cref="Rune"/>.</summary>
    public static TokenSet Single(Rune r) => Single(r.Value);

    /// <summary>
    /// A one-member set holding the single rune <paramref name="codepoint"/>.
    /// Throws if it isn't a valid Unicode scalar value (surrogate halves are
    /// rejected).
    /// </summary>
    public static TokenSet Single(int codepoint)
    {
        ValidateScalarValue(codepoint, nameof(codepoint));
        return new TokenSet(new[] { new Interval(codepoint, codepoint) });
    }

    /// <summary><see cref="Range(int, int)"/> for <see cref="char"/> endpoints.</summary>
    public static TokenSet Range(char low, char high) => Range((int)low, (int)high);

    /// <summary><see cref="Range(int, int)"/> for <see cref="Rune"/> endpoints.</summary>
    public static TokenSet Range(Rune low, Rune high) => Range(low.Value, high.Value);

    /// <summary>
    /// A set of the scalar values in [<paramref name="low"/>,
    /// <paramref name="high"/>]. Both endpoints must be valid Unicode scalar
    /// values (no surrogate halves).
    /// </summary>
    /// <remarks>
    /// A range that straddles the surrogate block is split into two intervals so
    /// the result contains no surrogate code units. So Range(0, 0x10FFFF) is
    /// every scalar value, with no surrogates, and that's the only thing it can
    /// mean. To get surrogates into a set, name them with
    /// <see cref="Surrogates"/> or <see cref="SurrogateRange"/> and union them
    /// in.
    /// </remarks>
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

    /// <summary>
    /// A set covering [<paramref name="low"/>, <paramref name="high"/>] inside
    /// the surrogate block 0xD800..0xDFFF. Both endpoints must themselves be
    /// surrogate code units. For the whole block use the
    /// <see cref="Surrogates"/> constant.
    /// </summary>
    /// <remarks>
    /// Use this to select a specific sub-range of the surrogate block (just the
    /// leading-surrogate half, just the trailing-surrogate half, etc.). The
    /// result is matchable only by grammars compiled with Compile(null), where
    /// the lexer surfaces lone surrogates as one-char tokens. Under the default
    /// Compile(FormC) the lexer pre-rejects lone surrogates from input, so the
    /// set has nothing to match.
    /// </remarks>
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

    /// <summary>
    /// Construct a TokenSet from a list of code-point intervals. Each element
    /// is a closed range [Low, High]. The input doesn't need to be sorted or
    /// non-overlapping; the constructor normalizes (sorts, merges adjacent,
    /// drops empties). Bulk factory for large generated tables that would be
    /// tedious to chain through the <see cref="op_BitwiseOr"/> operator
    /// (each <c>|</c> allocates and merges, so an N-rune build is O(N²)).
    /// </summary>
    /// <remarks>
    /// Like <see cref="Range(int, int)"/>, a tuple that straddles the surrogate
    /// block is split so the result contains no surrogate code units. Both
    /// endpoints must be valid scalars, but the interval between them can still
    /// cover 0xD800..0xDFFF, so <c>FromRanges((0, 0x10FFFF))</c> is every scalar
    /// value with no surrogates, the same set as <c>Range(0, 0x10FFFF)</c>. To
    /// put surrogates into a set, name them with <see cref="Surrogates"/> or
    /// <see cref="SurrogateRange"/> and union them in.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Any range has <c>high &lt; low</c>, or contains a value outside the
    /// Unicode scalar range.
    /// </exception>
    public static TokenSet FromRanges(ReadOnlySpan<(int Low, int High)> ranges)
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
            // Split a tuple that straddles the surrogate block so the result
            // stays surrogate-free, matching Range(int, int). Both endpoints
            // are valid scalars (checked above), but the closed interval
            // between them can still cover 0xD800..0xDFFF, e.g. (0, 0x10FFFF).
            // Surrogates enter a TokenSet only through Surrogates /
            // SurrogateRange, never through a scalar-range factory.
            EmitScalarInterval(list, low, high);
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
            if (RuneHelpers.IsSurrogatePairAt(text, index))
            {
                rune = char.ConvertToUtf32(text[index], text[index + 1]);
                consumed = 2;
            }
            else
            {
                rune = text[index];
                consumed = 1;
            }
            if (!Rune.IsValid(rune))
                throw new ArgumentException(
                    $"Runes(string) encountered an invalid Unicode scalar value (0x{rune:X4}) at UTF-16 offset {index}. " +
                    "Lone surrogate halves aren't valid scalars.",
                    nameof(text));
            if (grapheme.Length != consumed)
                throw new ArgumentException(
                    $"Runes(string) input \"{text}\" contains a multi-rune grapheme cluster (\"{grapheme}\") at UTF-16 offset {index}. " +
                    "Runes(string) walks rune by rune and rejects inputs where consecutive runes form one cluster. " +
                    "If you want a set of grapheme clusters, use Graphemes(string[]). " +
                    "If you want separate scalars that visually combine, build the set explicitly with Single(c1) | Single(c2).",
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
            ValidateGraphemeScalars(cluster, clusterIndex, nameof(clusters));
            string firstCluster = StringInfo.GetNextTextElement(cluster, 0);
            if (firstCluster.Length != cluster.Length)
                throw new ArgumentException(
                    $"Graphemes element at index {clusterIndex} (\"{cluster}\") contains more than one grapheme cluster. " +
                    "Each element must be exactly one cluster. " +
                    "If you want a set of runes, use Runes(string).",
                    nameof(clusters));

            int firstRune;
            int firstRuneLength;
            if (RuneHelpers.IsSurrogatePairAt(cluster, 0))
            {
                firstRune = char.ConvertToUtf32(cluster[0], cluster[1]);
                firstRuneLength = 2;
            }
            else
            {
                firstRune = cluster[0];
                firstRuneLength = 1;
            }

            if (cluster.Length == firstRuneLength)
            {
                intervals.Add(new Interval(firstRune, firstRune));
            }
            else
            {
                multiRuneGraphemes ??= new List<string>();
                multiRuneGraphemes.Add(cluster);
            }
        }
        return new TokenSet(Normalize(intervals), NormalizeGraphemes(multiRuneGraphemes));
    }

    // Walks a Graphemes element's runes by hand and throws on any lone
    // surrogate half before the single-cluster shape check can mask it.
    private static void ValidateGraphemeScalars(string grapheme, int clusterIndex, string parameterName)
    {
        for (int runeIndex = 0; runeIndex < grapheme.Length;)
        {
            int runeStart = runeIndex;
            int runeCodepoint;
            if (RuneHelpers.IsSurrogatePairAt(grapheme, runeIndex))
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
                    $"Graphemes element at index {clusterIndex} contains an invalid Unicode scalar value (0x{runeCodepoint:X4}) at UTF-16 offset {runeStart}. " +
                    "Lone surrogate halves aren't valid scalars.",
                    parameterName);
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

    /// <summary>Union: a set whose members are in <paramref name="a"/> or <paramref name="b"/>.</summary>
    public static TokenSet operator |(TokenSet a, TokenSet b)
    {
        var combined = new List<Interval>();
        if (a._ranges != null) combined.AddRange(a._ranges);
        if (b._ranges != null) combined.AddRange(b._ranges);
        var mergedGraphemes = MergeMultiRuneGraphemeUnion(a._multiRuneGraphemes, b._multiRuneGraphemes);
        return new TokenSet(Normalize(combined), mergedGraphemes);
    }

    /// <summary>Intersection: a set whose members are in both <paramref name="a"/> and <paramref name="b"/>.</summary>
    /// <remarks>
    /// Sorted-range intersection: walk both sets once, picking
    /// [max(low), min(high)] whenever the current intervals overlap and
    /// advancing whichever interval ends first. Linear in the sum of the two
    /// interval counts. The result is normalized like the inputs. Multi-rune
    /// graphemes are intersected separately by sorted ordinal merge.
    /// </remarks>
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

    /// <summary>
    /// Complement over the scalar-value universe: a set of every scalar value
    /// not in <paramref name="a"/>. The result is always surrogate-free,
    /// regardless of whether the input had surrogates.
    /// </summary>
    /// <remarks>
    /// So ~Letters is "every scalar non-letter" without quietly admitting
    /// U+D800..U+DFFF, and ~Empty is the scalar universe (also exposed as
    /// <see cref="Universe"/>). A grammar that wants surrogates in the complement
    /// writes <c>~set | Surrogates</c>. The rule is: surrogates enter a TokenSet
    /// through the Surrogates constant or SurrogateRange and never through a
    /// complement. Union and intersection move them between sets, but ~ never
    /// fabricates them.
    /// <para>
    /// The cost is that applying ~ twice doesn't always get you back where you
    /// started, once surrogates are involved: ~~Surrogates is Empty, not
    /// Surrogates, because the first ~ turns Surrogates into the scalar universe
    /// (the surrogate code units are stripped) and the second ~ complements that
    /// universe down to nothing rather than restoring the surrogates. The
    /// opposite ordering (~ as true complement and surrogates riding along in ~A
    /// whenever A doesn't have them) would make ~ round-trip cleanly, at the cost
    /// of OneOf(~Letters) silently matching a lone surrogate under Compile(null).
    /// NoneOf(Letters) matches a lone surrogate either way: it's a direct
    /// non-membership test, not OneOf(~Letters), so the ~ design never reaches it.
    /// </para>
    /// <para>
    /// Throws InvalidOperationException when the input has any multi-rune grapheme
    /// entries. The universe of grapheme clusters is unbounded (any rune sequence
    /// respecting UAX #29 boundaries is a grapheme), so complement against it
    /// can't be represented as a finite explicit set. To subtract this set from
    /// another, use <c>a - b</c> (set difference), which handles multi-rune
    /// members directly.
    /// </para>
    /// </remarks>
    public static TokenSet operator ~(TokenSet a)
    {
        if (a.HasMultiRuneGraphemes)
            throw new InvalidOperationException(
                "Cannot complement a TokenSet that contains multi-rune graphemes. " +
                "The universe of grapheme clusters is unbounded, so the result " +
                "isn't representable as a finite set. To subtract this set from " +
                "another, use `a - b` (set difference), which handles multi-rune members.");
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

    // True set difference: every member of `a` that isn't a member of
    // `b`. This is what "a minus b" means for any pair of sets, including
    // ones with multi-rune grapheme members.
    //
    // This never throws, even when `a` or `b` has cluster members, where
    // `~` does. The reason is that the result is always a subset of `a`:
    // we keep `a`'s members and drop the ones `b` also has, so the answer
    // is at most as big as `a` and is always a finite set we can build.
    // `~a` is the opposite problem. It has to name every token that isn't
    // in `a`, and the universe of grapheme clusters is unbounded (any rune
    // sequence respecting UAX #29 boundaries is a cluster), so there's no
    // finite set to return and `~` throws on a cluster-bearing set.
    public static TokenSet operator -(TokenSet a, TokenSet b)
    {
        // Rune part: a's runes minus b's runes, by direct interval
        // subtraction. We subtract b's intervals from a's own intervals, so
        // every code point of a that b doesn't cover survives, surrogates
        // included. Routing through `a & ~b` instead would strip every
        // surrogate from a, because ~ complements over scalar values only
        // (surrogates are excluded by design, see operator ~). Surrogates
        // are first-class set members (TokenSet.Surrogates / SurrogateRange),
        // so dropping them here would contradict "every member of a that
        // isn't a member of b" and silently empty a set like
        // Surrogates - Single('a').
        var runeDifference = SubtractRuneIntervals(a._ranges, b._ranges);
        var clusters = MergeMultiRuneGraphemeDifference(a._multiRuneGraphemes, b._multiRuneGraphemes);
        return new TokenSet(runeDifference, clusters);
    }

    // Interval difference of two normalized (sorted, non-overlapping,
    // non-adjacent) interval arrays: every code point in `a` that isn't
    // covered by any interval in `b`. Surrogate-preserving, unlike the
    // `a & ~b` route, because it never appeals to the scalar-only universe.
    // Linear in the sum of the two array lengths.
    private static Interval[] SubtractRuneIntervals(Interval[]? aRanges, Interval[]? bRanges)
    {
        if (aRanges == null || aRanges.Length == 0) return Array.Empty<Interval>();
        if (bRanges == null || bRanges.Length == 0) return aRanges;
        var result = new List<Interval>();
        int bIndex = 0;
        for (int aIndex = 0; aIndex < aRanges.Length; aIndex++)
        {
            int cursor = aRanges[aIndex].Low;
            int high = aRanges[aIndex].High;
            // b intervals that end before this a interval starts can never
            // overlap it or any later (higher) a interval, so retire them.
            while (bIndex < bRanges.Length && bRanges[bIndex].High < cursor) bIndex++;
            // Walk the b intervals overlapping [cursor, high]. Use a local
            // index: a b interval can also straddle the gap into the next a
            // interval, so it stays available to the next iteration.
            int localB = bIndex;
            while (localB < bRanges.Length && bRanges[localB].Low <= high)
            {
                var subtract = bRanges[localB];
                if (subtract.Low > cursor)
                    result.Add(new Interval(cursor, subtract.Low - 1));
                if (subtract.High >= cursor) cursor = subtract.High + 1;
                if (cursor > high) break;
                localB++;
            }
            if (cursor <= high)
                result.Add(new Interval(cursor, high));
        }
        return result.Count == 0 ? Array.Empty<Interval>() : result.ToArray();
    }

    // Sorted-merge difference of two sorted-ordinal grapheme arrays: every
    // entry in `a` that doesn't appear in `b`. Linear in the sum of the two
    // array lengths. Fast-paths when `a` is empty (nothing to keep) or `b`
    // is empty (keep all of a, sharing the already-canonical array).
    private static string[] MergeMultiRuneGraphemeDifference(string[]? a, string[]? b)
    {
        int aLength = a?.Length ?? 0;
        if (aLength == 0) return Array.Empty<string>();
        int bLength = b?.Length ?? 0;
        if (bLength == 0) return a!;
        var result = new List<string>(aLength);
        int aIndex = 0;
        int bIndex = 0;
        while (aIndex < aLength && bIndex < bLength)
        {
            int cmp = string.CompareOrdinal(a![aIndex], b![bIndex]);
            if (cmp == 0)
            {
                aIndex++;
                bIndex++;
            }
            else if (cmp < 0)
            {
                result.Add(a[aIndex++]);
            }
            else
            {
                bIndex++;
            }
        }
        while (aIndex < aLength) result.Add(a![aIndex++]);
        return result.Count == 0 ? Array.Empty<string>() : result.ToArray();
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
    // rune-only sets pay no allocation past the shared empty array.
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
    // Returns the shared empty array when either side is empty so a
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
    // BuildInlineWhitespace. internal (not private) so the test suite's
    // scalar-space sweeps reference the same bounds the set is built from
    // instead of re-spelling the magic numbers.
    internal const int SurrogateLow = 0xD800;
    internal const int SurrogateHigh = 0xDFFF;
    internal const int MinScalarValue = 0;
    internal const int MaxScalarValue = 0x10FFFF;

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

    /// <summary>
    /// The set of every scalar value in the given <see cref="UnicodeCategory"/>.
    /// The result is cached, so repeated calls for the same category are
    /// hash-table lookups.
    /// </summary>
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
    // char.IsWhiteSpace accepts minus the seven UAX #18 single-rune line
    // terminators (LF, VT, FF, CR, NEL, LS, PS). It doesn't decompose
    // cleanly into UnicodeCategory values, so it's built by a predicate
    // scan rather than CategoriesUnion. For line terminators see
    // LineTerminators below and Rules.EndOfLine().
    private static readonly Lazy<TokenSet> _inlineWhitespace = new Lazy<TokenSet>(BuildInlineWhitespace);

    /// <summary>
    /// The set of Unicode scalar values that are letters in Unicode's
    /// General_Category sense (Lu, Ll, Lt, Lm, Lo). Matches what char.IsLetter
    /// and Rune.IsLetter consider a letter.
    /// </summary>
    /// <remarks>
    /// Use this for things that are literally letters: identifier characters in
    /// a name, keyword text inside an alphabetic token, a rule that accepts 'a'
    /// through 'z' plus a few accented letters.
    /// <para>
    /// Don't use this as a way to match "any character" or "any content." It
    /// rejects digits, whitespace, punctuation, symbols, and any multi-rune
    /// grapheme like emoji. A grammar that wants "match everything up to the next
    /// delimiter" or "match anything the other rules didn't claim" should use
    /// NoneOf(stopSet) for delimiter-based stops, or Not(stopRule) + AnyToken()
    /// for rule-based stops.
    /// </para>
    /// </remarks>
    public static TokenSet Letters => _letters.Value;

    /// <summary>The decimal-digit set (Unicode General_Category Nd).</summary>
    public static TokenSet Digits => _digits.Value;

    /// <summary>
    /// Intra-line whitespace: every rune char.IsWhiteSpace accepts except the
    /// UAX #18 line terminators. See <see cref="LineTerminators"/> for those.
    /// </summary>
    public static TokenSet InlineWhitespace => _inlineWhitespace.Value;

    /// <summary>
    /// The line terminators defined by UAX #18 Annex C: the seven single-rune
    /// terminators LF (U+000A), VT (U+000B), FF (U+000C), CR (U+000D), NEL
    /// (U+0085), LINE SEPARATOR (U+2028), PARAGRAPH SEPARATOR (U+2029), plus the
    /// CRLF two-rune cluster (which UAX #29 GB3 keeps glued together in one
    /// grapheme).
    /// </summary>
    /// <remarks>
    /// Matches what Java's \R, ECMAScript's "line terminator" concept, and most
    /// modern regex engines treat as a newline. CRLF lives in the set as a
    /// multi-rune entry, so OneOf / NoneOf / ScanUntil / ScanWhile against this
    /// set all treat the CRLF cluster as one terminator.
    /// </remarks>
    public static readonly TokenSet LineTerminators =
          Single(0x000A)   // LF
        | Single(0x000B)   // VT
        | Single(0x000C)   // FF
        | Single(0x000D)   // CR
        | Single(0x0085)   // NEL
        | Single(0x2028)   // LS
        | Single(0x2029)     // PS
        | Graphemes("\r\n"); // CRLF cluster

    /// <summary>
    /// Full-Unicode intra-line whitespace plus every UAX #18 line terminator
    /// (the seven single-rune terminators and the CRLF two-rune cluster). Use
    /// this for grammars that treat any whitespace as an ordinary separator. For
    /// ASCII-only whitespace use <see cref="Ascii.AnyWhitespace"/>.
    /// </summary>
    public static TokenSet AnyWhitespace => _anyWhitespace.Value;
    private static readonly Lazy<TokenSet> _anyWhitespace =
        new Lazy<TokenSet>(() => InlineWhitespace | LineTerminators);

    /// <summary>
    /// The single-member set holding U+FFFD REPLACEMENT CHARACTER.
    /// </summary>
    /// <remarks>
    /// .NET's Unicode-encoding decoders (Encoding.UTF8, Encoding.Unicode,
    /// Encoding.UTF32) substitute U+FFFD for ill-formed byte sequences under the
    /// default DecoderReplacementFallback, so a U+FFFD in your input is the
    /// fingerprint of an upstream decoder that swallowed something malformed.
    /// Grammars that want to surface or reject those markers can use
    /// OneOf(TokenSet.Replacement) or NoneOf(TokenSet.Replacement | ...).
    /// </remarks>
    public static readonly TokenSet Replacement = Single(0xFFFD);

    /// <summary>
    /// ASCII-restricted versions of the built-in sets, for grammars that want
    /// only the 0x00..0x7F range.
    /// </summary>
    public static class Ascii
    {
        /// <summary>The ASCII letters A-Z and a-z.</summary>
        public static readonly TokenSet Letters = Range('A', 'Z') | Range('a', 'z');

        /// <summary>The ASCII digits 0-9.</summary>
        public static readonly TokenSet Digits = Range('0', '9');

        /// <summary>
        /// ASCII intra-line whitespace: SPACE and TAB only. Mirrors the
        /// full-Unicode <see cref="TokenSet.InlineWhitespace"/>.
        /// </summary>
        public static readonly TokenSet InlineWhitespace = Runes(" \t");
        /// <summary>
        /// ASCII whitespace including every ASCII line terminator: SPACE, TAB,
        /// LF, VT, FF, CR, plus the CRLF two-rune cluster. The full-Unicode
        /// <see cref="TokenSet.AnyWhitespace"/> restricted to ASCII.
        /// </summary>
        /// <remarks>
        /// VT (U+000B) and FF (U+000C) are line terminators under UAX #18, so
        /// they live in <see cref="TokenSet.LineTerminators"/> and are consumed
        /// by Rules.EndOfLine(), and a set "including line terminators" carries
        /// them too. Use this for grammars that treat newlines as ordinary
        /// whitespace. To distinguish intra-line whitespace from line
        /// terminators, use <see cref="InlineWhitespace"/> and Rules.EndOfLine()
        /// instead. CR, LF, and the CRLF cluster all live in the set so OneOf /
        /// NoneOf / ScanUntil match each consistently: an input CRLF cluster is
        /// the multi-rune entry, a bare CR or LF is the matching single-rune
        /// entry.
        /// </remarks>
        public static readonly TokenSet AnyWhitespace =
            InlineWhitespace
            | Single('\n')       // LF
            | Single('\v')       // VT
            | Single('\f')       // FF
            | Single('\r')       // CR
            | Graphemes("\r\n");  // CRLF cluster
        /// <summary>The ASCII hex digits 0-9, a-f, and A-F.</summary>
        public static readonly TokenSet HexDigits = Digits | Range('a', 'f') | Range('A', 'F');
    }

    // Build from predicate over code points that fit in one UTF-16 char
    // (U+0000..U+FFFF). Supplementary-plane whitespace is rare in real
    // input and not needed for the smallest core. Includes a code point
    // when char.IsWhiteSpace accepts it and it's not one of the seven
    // UAX #18 single-rune line terminators (those belong to EndOfLine).
    private static TokenSet BuildInlineWhitespace()
    {
        var list = new List<Interval>();
        int? currentLow = null;
        int currentHigh = 0;
        for (int codepoint = 0; codepoint <= 0xFFFF; codepoint++)
        {
            // The surrogate block holds no valid Unicode scalar values, so
            // nothing in it can be a member. Close any open run and jump past
            // the whole block. Closing matters: a bare `continue` would leave
            // an open run spanning the gap, so a run with members just below
            // 0xD800 and just above 0xDFFF would merge into one interval that
            // swallowed the surrogate code units. No whitespace sits at that
            // boundary today, but the close keeps the result surrogate-free
            // regardless of the data, matching BuildCategories.
            if (codepoint >= 0xD800 && codepoint <= 0xDFFF)
            {
                if (currentLow != null)
                {
                    list.Add(new Interval(currentLow.Value, currentHigh));
                    currentLow = null;
                }
                codepoint = 0xDFFF; // the loop's ++ moves to 0xE000
                continue;
            }
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

    // Scalar membership test for the UAX #18 single-rune line terminators,
    // mirroring LineTerminators. Kept independent of the LineTerminators
    // field so a caller that runs at type-init time (BuildInlineWhitespace)
    // doesn't depend on that field's initialization order.
    internal static bool IsLineTerminator(int codepoint) =>
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
            // The surrogate block (U+D800..U+DFFF) holds UTF-16 code units used
            // to encode supplementary-plane code points as pairs. They aren't
            // valid Unicode scalar values (runes) on their own and belong to no
            // scalar-category set. Close every open run and jump past the whole
            // block. Closing matters: a bare `continue` would leave a run open
            // across the gap, so a category with members just below 0xD800 and
            // just above 0xDFFF (0xD7FF and 0xE000) would merge into one
            // interval that swallowed the surrogate code units. No category
            // straddles the gap in the Unicode the BCL ships today (0xD7FF is
            // Cn, 0xE000 is Co), but the close keeps the result surrogate-free
            // for any category and any future Unicode version, instead of
            // silently relying on that adjacency never happening.
            if (codepoint >= 0xD800 && codepoint <= 0xDFFF)
            {
                for (int index = 0; index < missing.Count; index++)
                {
                    if (currentLows[index] != null)
                    {
                        lists[index].Add(new Interval(currentLows[index]!.Value, currentHighs[index]));
                        currentLows[index] = null;
                    }
                }
                codepoint = 0xDFFF; // the loop's ++ moves to 0xE000
                continue;
            }

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
