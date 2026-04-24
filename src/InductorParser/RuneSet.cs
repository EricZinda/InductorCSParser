using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace InductorParser;

// A set of Unicode scalar values (runes), used to describe character classes
// for OneOf and NoneOf. Build one with the factory methods (Single, Range,
// Runes, Category) or one of the built-ins (Letters, Digits, Whitespace, and
// their Ascii.* variants), then compose larger classes with the set operators:
//
//     |   union           a | b           runes in a or b
//     &   intersection    a & b           runes in a and b
//     ~   complement      ~a              runes not in a
//
// Set difference is the idiom a & ~b ("a minus b"). The operators return a
// new RuneSet. The struct is immutable.
//
//     var unicodeIdentifier = RuneSet.Letters | RuneSet.Digits | RuneSet.Runes("_");
//     var asciiConsonants   = RuneSet.Ascii.Letters & ~RuneSet.Runes("aeiouAEIOU");
//     var cyrillicLetters   = RuneSet.Letters & RuneSet.Range(0x0400, 0x04FF);
//
// Internally a RuneSet is a sorted, non-overlapping, non-adjacent array of
// code-point runs. That makes every operator linear in the number of runs,
// which is small for typical grammars (Letters is a few dozen runs, not a
// million code points). A grammar rule that uses RuneSet.Letters a thousand
// times pays the Unicode-table scan once at startup and then a handful of
// Contains() calls per match.
//
// RuneSet is a set of code points, not graphemes. Multi-rune graphemes
// (emoji sequences, combining-mark clusters) aren't a single element of any
// RuneSet. See docs/ProgrammingModel.md for how that interacts with the
// grapheme lexer.
public readonly partial struct RuneSet : IEquatable<RuneSet>
{
    // One contiguous run of Unicode code points, inclusive on both ends:
    // the closed interval [Low, High]. A RuneSet is represented as a sorted,
    // non-overlapping, non-adjacent array of these runs. Named Interval
    // (not Range) to avoid colliding with the public Range(...) factory
    // method below.
    private readonly record struct Interval(int Low, int High);

    private readonly Interval[] _ranges;

    private RuneSet(Interval[] ranges) => _ranges = ranges;

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

    public bool IsEmpty => _ranges == null || _ranges.Length == 0;

    // Value equality: two RuneSets are equal iff they contain the same runes.
    // Normalize guarantees a canonical interval list (sorted, non-overlapping,
    // non-adjacent), so equal sets necessarily have identical _ranges arrays.
    // That reduces equality to a length check plus a pairwise Interval compare.
    public bool Equals(RuneSet other)
    {
        var mine = _ranges;
        var theirs = other._ranges;
        int mineLength = mine?.Length ?? 0;
        int theirsLength = theirs?.Length ?? 0;
        if (mineLength != theirsLength) return false;
        for (int index = 0; index < mineLength; index++)
            if (mine![index] != theirs![index]) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is RuneSet other && Equals(other);

    public override int GetHashCode()
    {
        var ranges = _ranges;
        if (ranges == null) return 0;
        var hash = new HashCode();
        for (int index = 0; index < ranges.Length; index++)
            hash.Add(ranges[index]);
        return hash.ToHashCode();
    }

    public static bool operator ==(RuneSet a, RuneSet b) => a.Equals(b);
    public static bool operator !=(RuneSet a, RuneSet b) => !a.Equals(b);

    // Maximum number of ranges ToString renders before truncating.
    // Large RuneSets (Unicode-category-wide classes like Letters) can
    // hold hundreds of ranges, which would produce an unreadable trace
    // line. Capping at 8 keeps trace output legible while preserving
    // the useful information for small, hand-built classes. The
    // truncated tail shows "+N more" so a reader can tell output was
    // dropped.
    private const int MaxRenderedRanges = 8;

    // Human-readable rendering of the range list, for trace output and
    // debugger display. Produces "[a-z,A-Z,0-9]" style output with
    // single-codepoint ranges collapsed to one char and long ranges
    // rendered as low-high. Printable ASCII code points render as the
    // literal character, everything else renders as U+XXXX. Classes
    // with more than MaxRenderedRanges ranges are truncated with a
    // "+N more" tail. Keeps trace lines legible without dragging in
    // the entire Unicode database.
    public override string ToString()
    {
        var ranges = _ranges;
        if (ranges == null || ranges.Length == 0) return "[]";
        var sb = new StringBuilder();
        sb.Append('[');
        int rendered = ranges.Length <= MaxRenderedRanges
            ? ranges.Length
            : MaxRenderedRanges;
        for (int index = 0; index < rendered; index++)
        {
            if (index > 0) sb.Append(',');
            var interval = ranges[index];
            sb.Append(RenderCodepoint(interval.Low));
            if (interval.High != interval.Low)
            {
                sb.Append('-');
                sb.Append(RenderCodepoint(interval.High));
            }
        }
        if (ranges.Length > MaxRenderedRanges)
        {
            sb.Append(",...+");
            sb.Append(ranges.Length - MaxRenderedRanges);
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

    // The empty set, containing no runes. Equivalent to default(RuneSet),
    // exposed as a named constant so callers can write RuneSet.Empty
    // instead of relying on "default happens to mean empty."
    public static readonly RuneSet Empty = default;

    // The universal set, containing every valid Unicode scalar value
    // (0..0x10FFFF minus the surrogate block). The complement of Empty.
    // Used as the "unknown / anything goes" default for FirstConsumedRunes
    // (see RuleStartRequirements) so rules with no tighter information
    // never get filtered out.
    public static readonly RuneSet Universe = ~default(RuneSet);

    public static RuneSet Single(char c) => Single((int)c);
    public static RuneSet Single(Rune r) => Single(r.Value);
    public static RuneSet Single(int codepoint)
    {
        ValidateScalarValue(codepoint, nameof(codepoint));
        return new RuneSet(new[] { new Interval(codepoint, codepoint) });
    }

    public static RuneSet Range(char low, char high) => Range((int)low, (int)high);
    public static RuneSet Range(Rune low, Rune high) => Range(low.Value, high.Value);
    // Validates the endpoints themselves, not the interior of the range.
    // That means Range(0, 0x10FFFF) is allowed even though the interval
    // covers the surrogate block 0xD800..0xDFFF, which isn't a set of
    // valid scalar values. The internal set ends up with "dead" slots in
    // that block, which is harmless: the lexer never produces surrogate
    // halves as token values, so no Contains() check against those slots
    // can ever fire. Auto-splitting the range around the surrogate gap
    // would be more principled but also more code for zero user-visible
    // effect.
    public static RuneSet Range(int low, int high)
    {
        ValidateScalarValue(low, nameof(low));
        ValidateScalarValue(high, nameof(high));
        if (high < low) throw new ArgumentException("high must be >= low");
        return new RuneSet(new[] { new Interval(low, high) });
    }

    // Construct a RuneSet from a list of code-point intervals. Each element
    // is a closed range [Low, High]. The input doesn't need to be sorted or
    // non-overlapping; Normalize takes care of that. Intended as the bulk
    // factory for large hand-curated or generated tables that would be
    // tedious to chain through the | operator.
    internal static RuneSet FromRanges(ReadOnlySpan<(int Low, int High)> ranges)
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
        return new RuneSet(Normalize(list));
    }

    public static RuneSet Runes(string characters)
    {
        if (characters == null) throw new ArgumentNullException(nameof(characters));
        var list = new List<Interval>();
        // Walk the string one grapheme at a time. A RuneSet holds single
        // Unicode scalar values, so a multi-rune grapheme (skin-toned emoji,
        // ZWJ family, decomposed accent) can't be one element of the set
        // the caller is asking to build. Catching it here turns what used
        // to be a silent "I built a two-rune set that matches neither
        // rune the way the caller expected" into a loud exception at
        // construction time. GetNextTextElement is the same API the
        // GraphemeLexer uses, so validation and tokenization agree on
        // what a grapheme is.
        for (int index = 0; index < characters.Length;)
        {
            string grapheme = StringInfo.GetNextTextElement(characters, index);
            int graphemeStart = index;
            int codepoint;
            if (char.IsHighSurrogate(characters[index]) && index + 1 < characters.Length && char.IsLowSurrogate(characters[index + 1]))
            {
                codepoint = char.ConvertToUtf32(characters[index], characters[index + 1]);
                index += 2;
            }
            else
            {
                codepoint = characters[index];
                index++;
            }
            // Catches lone surrogate halves in the input string. A well-formed
            // UTF-16 string shouldn't contain them, but we can't trust every
            // caller's string to be well-formed.
            if (!Rune.IsValid(codepoint))
                throw new ArgumentException(
                    $"Runes(string) encountered an invalid Unicode scalar value (0x{codepoint:X4}) at UTF-16 offset {index - 1}. " +
                    "Lone surrogate halves aren't valid runes.",
                    nameof(characters));
            // If the grapheme extends past the first rune we just
            // consumed, it's a multi-rune grapheme. Refuse it. Token
            // and Literal are the grapheme-matching primitives.
            //
            // Exception: CRLF (\r\n) is one grapheme per UAX #29, but
            // nobody calling Runes("\r\n") means "the CRLF grapheme as
            // a unit." They mean "the set {CR, LF}," two separate
            // scalars. CRLF is the only ASCII multi-rune grapheme, so
            // letting it through without complaint keeps the common
            // "line-terminator runes" idiom working while still
            // catching the real silent-misuse cases (emoji with skin
            // tone, decomposed accents, ZWJ sequences, etc.).
            bool isCrlf = grapheme.Length == 2 && grapheme[0] == '\r' && grapheme[1] == '\n';
            if (graphemeStart + grapheme.Length != index && !isCrlf)
            {
                // Enumerate the grapheme's runes by hand since
                // string.EnumerateRunes is .NET 5+ and this project targets
                // netstandard2.1. Same surrogate-pair logic the outer loop uses.
                var runeList = new List<string>();
                for (int runeIndex = 0; runeIndex < grapheme.Length;)
                {
                    int runeCodepoint;
                    if (char.IsHighSurrogate(grapheme[runeIndex]) && runeIndex + 1 < grapheme.Length && char.IsLowSurrogate(grapheme[runeIndex + 1]))
                    {
                        runeCodepoint = char.ConvertToUtf32(grapheme[runeIndex], grapheme[runeIndex + 1]);
                        runeIndex += 2;
                    }
                    else
                    {
                        runeCodepoint = grapheme[runeIndex];
                        runeIndex++;
                    }
                    runeList.Add($"U+{runeCodepoint:X4}");
                }
                throw new ArgumentException(
                    $"Runes(string) cannot accept the multi-rune grapheme \"{grapheme}\" ({string.Join(", ", runeList)}) at UTF-16 offset {graphemeStart}. " +
                    "A RuneSet holds single Unicode scalar values. To match this grapheme as a unit, use Token(\"" + grapheme + "\") or Literal(\"" + grapheme + "\").",
                    nameof(characters));
            }
            list.Add(new Interval(codepoint, codepoint));
        }
        return new RuneSet(Normalize(list));
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

    public static RuneSet operator |(RuneSet a, RuneSet b)
    {
        var combined = new List<Interval>();
        if (a._ranges != null) combined.AddRange(a._ranges);
        if (b._ranges != null) combined.AddRange(b._ranges);
        return new RuneSet(Normalize(combined));
    }

    // Sorted-range intersection: walk both sets once, picking [max(low), min(high)]
    // whenever the current intervals overlap, and advancing whichever interval
    // ends first. Linear in the sum of the two interval counts. Both inputs are
    // already normalized (sorted, non-overlapping, non-adjacent), and so is the
    // result. Adjacent overlap fragments can't appear because that would imply
    // the inputs themselves had adjacent intervals, contradicting normalization.
    public static RuneSet operator &(RuneSet a, RuneSet b)
    {
        var aRanges = a._ranges;
        var bRanges = b._ranges;
        if (aRanges == null || bRanges == null) return new RuneSet(Array.Empty<Interval>());
        if (aRanges.Length == 0 || bRanges.Length == 0) return new RuneSet(Array.Empty<Interval>());

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
        return new RuneSet(result.ToArray());
    }

    // Complement against the full set of Unicode scalar values: everything
    // in 0..0x10FFFF except the surrogate block 0xD800..0xDFFF (which isn't
    // a set of valid scalar values) and except the input set's intervals.
    // Implementation walks the input intervals and emits the gaps between
    // them, splitting any gap that straddles the surrogate block.
    public static RuneSet operator ~(RuneSet a)
    {
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
        return new RuneSet(result.ToArray());
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
    // Concurrent because nothing else in RuneSet holds a lock. Multiple
    // threads resolving Letters on startup are fine.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<UnicodeCategory, RuneSet> _categoryCache
        = new System.Collections.Concurrent.ConcurrentDictionary<UnicodeCategory, RuneSet>();

    // One UnicodeCategory is one RuneSet and it is cached (if used).
    public static RuneSet Category(UnicodeCategory category)
    {
        if (_categoryCache.TryGetValue(category, out var cached)) return cached;
        BuildCategories(new[] { category });
        return _categoryCache[category];
    }

    // Composite built-ins. Letters is the union of the five "Letter"
    // UnicodeCategory values. Digits is one category. Wrapped in Lazy so
    // the union work happens once and is cached. Without it, every access
    // to RuneSet.Letters would redo the four | merges.
    //
    // The Lazy factory calls BuildCategories with the full batch first, so
    // all five category scans happen in a single 0..0x10FFFF pass rather
    // than five separate passes. Each subsequent Category(...) call lookup
    // is then a cache hit.
    private static readonly Lazy<RuneSet> _letters = new Lazy<RuneSet>(() =>
        CategoriesUnion(
            UnicodeCategory.UppercaseLetter,
            UnicodeCategory.LowercaseLetter,
            UnicodeCategory.TitlecaseLetter,
            UnicodeCategory.ModifierLetter,
            UnicodeCategory.OtherLetter));
    private static readonly Lazy<RuneSet> _digits = new Lazy<RuneSet>(() =>
        Category(UnicodeCategory.DecimalDigitNumber));

    // Shared helper for built-ins: ensure every target category is cached
    // (one scan for all missing targets), then union them in order.
    private static RuneSet CategoriesUnion(params UnicodeCategory[] targets)
    {
        BuildCategories(targets);
        var result = Category(targets[0]);
        for (int index = 1; index < targets.Length; index++)
            result = result | Category(targets[index]);
        return result;
    }

    // Whitespace doesn't decompose cleanly into UnicodeCategory values
    // (char.IsWhiteSpace includes a few specific Control-category code
    // points like \t and \n, plus SpaceSeparator/LineSeparator/ParagraphSeparator).
    // Keep it as its own predicate-based scan.
    private static readonly Lazy<RuneSet> _whitespace = new Lazy<RuneSet>(BuildWhitespace);

    // The set of Unicode scalar values that are letters in Unicode's
    // General_Category sense (Lu, Ll, Lt, Lm, Lo). Matches what
    // char.IsLetter and Rune.IsLetter consider a letter.
    //
    // Use this for things that are literally letters: identifier characters
    // in a name, keyword text inside an alphabetic token, a rule that
    // accepts 'a' through 'z' plus 'é' and '漢' and 'ж'.
    //
    // Do NOT use this as a way to match "any character" or "any content." It
    // rejects digits, whitespace, punctuation, symbols, and any multi-rune
    // grapheme like emoji. A grammar that wants "match everything up to the
    // next delimiter" or "match anything the other rules didn't claim"
    // should use the pass-through-text recipe (see docs/Recipes.md): either
    // NoneOf(stopSet) for delimiter-based stops, or Not(stopRule) + AnyToken()
    // for rule-based stops.
    public static RuneSet Letters => _letters.Value;
    public static RuneSet Digits => _digits.Value;
    public static RuneSet Whitespace => _whitespace.Value;

    // The single-rune line terminators defined by UAX #18 Annex C:
    // LF (U+000A), VT (U+000B), FF (U+000C), CR (U+000D), NEL (U+0085),
    // LINE SEPARATOR (U+2028), and PARAGRAPH SEPARATOR (U+2029). This
    // matches what Java's \R, ECMAScript's "line terminator" concept,
    // and most modern regex engines treat as a newline rune.
    //
    // Named SingleRuneLineTerminators (not just LineTerminators) as a
    // reminder that a RuneSet holds individual code points, not
    // sequences. The CRLF two-rune pair is also a line terminator
    // under UAX #18, but it can't live in a rune set. Grammars that
    // want CRLF-as-one-terminator combine this set with a
    // Literal("\r\n") alternative, which is what Rules.EndOfLine()
    // does.
    public static readonly RuneSet SingleRuneLineTerminators =
          Single(0x000A)   // LF
        | Single(0x000B)   // VT
        | Single(0x000C)   // FF
        | Single(0x000D)   // CR
        | Single(0x0085)   // NEL
        | Single(0x2028)   // LS
        | Single(0x2029);  // PS

    public static class Ascii
    {
        public static readonly RuneSet Letters = Range('A', 'Z') | Range('a', 'z');
        public static readonly RuneSet Digits = Range('0', '9');
        public static readonly RuneSet Whitespace = Runes(" \t\r\n");
        public static readonly RuneSet HexDigits = Digits | Range('a', 'f') | Range('A', 'F');
    }

    // Build from predicate over code points that fit in one UTF-16 char
    // (U+0000..U+FFFF). Supplementary-plane whitespace is rare in real
    // input and not needed for the smallest core.
    private static RuneSet BuildWhitespace()
    {
        var list = new List<Interval>();
        int? currentLow = null;
        int currentHigh = 0;
        for (int codepoint = 0; codepoint <= 0xFFFF; codepoint++)
        {
            // Skip the surrogate block: not valid Unicode scalar values.
            // See BuildFromPredicate below for the full rationale.
            if (codepoint >= 0xD800 && codepoint <= 0xDFFF) continue;
            if (char.IsWhiteSpace((char)codepoint))
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
        return new RuneSet(list.ToArray());
    }

    // Scan 0..0x10FFFF once and populate the cache with a RuneSet for every
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

        // Close any still-open intervals, materialize RuneSets, publish to cache.
        for (int index = 0; index < missing.Count; index++)
        {
            if (currentLows[index] != null)
                lists[index].Add(new Interval(currentLows[index]!.Value, currentHighs[index]));
            _categoryCache.TryAdd(missing[index], new RuneSet(lists[index].ToArray()));
        }
    }
}
