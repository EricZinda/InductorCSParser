using System;
using System.Collections.Generic;
using System.Text;

namespace InductorParser.Lexing.Unicode;

/// <summary>
/// The parser's single entry point for Unicode normalization. Every
/// Normalize / IsNormalized call inside the library routes through this
/// class, so the whole parser uses one implementation.
/// </summary>
/// <remarks>
/// This class chooses between two implementations: the runtime's
/// string.Normalize, and a built-in UAX #15 normalizer
/// (https://www.unicode.org/reports/tr15/) whose generated tables in
/// UnicodeNormalization.Data.cs are the other half of this partial
/// class and are fixed at Unicode 15.0 like the built-in segmenter's table.
/// The implementation is chosen by the process-wide setting surfaced
/// as UnicodeEnvironment.Implementation, which governs this normalizer
/// and the segmenter together (they can never diverge) and is resolved
/// and frozen on the first query from either. The built-in
/// implementation exists because Unity's Mono runtime ships a
/// string.Normalize that doesn't apply compatibility mappings, misses
/// canonical mappings, and accepts ill-formed UTF-16, all of which
/// .NET's implementation gets right.
/// <para>
/// The built-in normalizer has no quick-check tables: it always
/// decomposes, reorders, and recomposes, then hands back the original
/// string instance when the result is content-identical. Quick-check
/// (the NFC_QC / NFD_QC properties) is the next optimization if
/// profiling ever shows the rebuild hurting, at the cost of a third
/// generated table family and the subtle maybe-resolution logic.
/// </para>
/// </remarks>
internal static partial class UnicodeNormalization
{
    /// <summary>
    /// Normalize <paramref name="input"/> to <paramref name="form"/>.
    /// The first call resolves and freezes the process-wide
    /// implementation choice (UnicodeEnvironment.Implementation): the
    /// normalized string comes from the runtime's string.Normalize or
    /// the built-in UAX #15 implementation below.
    /// </summary>
    public static string Normalize(string input, NormalizationForm form)
    {
        ValidateForm(form);
        if (UnicodeEnvironment.ResolveUseBundled())
            return NormalizeWithBundledImplementation(input, form);
        HostGlobalizationCheck.EnsureRuntimeNormalizationIsTrustworthy();
        return NormalizeWithRuntime(input, form);
    }

    // The host-globalization check runs on IsNormalized too, not just
    // Normalize, because invariant globalization makes IsNormalized
    // always report true, and the callers that check IsNormalized
    // before normalizing would then silently skip the Normalize call
    // and never reach its check.
    public static bool IsNormalized(string input, NormalizationForm form)
    {
        ValidateForm(form);
        if (UnicodeEnvironment.ResolveUseBundled())
            return IsNormalizedWithBundledImplementation(input, form);
        HostGlobalizationCheck.EnsureRuntimeNormalizationIsTrustworthy();
        return IsNormalizedWithRuntime(input, form);
    }

    // Reject values outside the four defined forms before dispatching,
    // with one culture-stable message. .NET's string.Normalize throws on
    // an out-of-range value itself, but the built-in pipeline selects its
    // decompose and compose steps by comparing against the K and C
    // forms, so without this check an out-of-range value would quietly
    // run as FormD there and the two implementations would disagree.
    private static void ValidateForm(NormalizationForm form)
    {
        if (form != NormalizationForm.FormC && form != NormalizationForm.FormD
            && form != NormalizationForm.FormKC && form != NormalizationForm.FormKD)
        {
            throw new ArgumentException(
                $"Not a defined NormalizationForm value: {(int)form}.", nameof(form));
        }
    }

    // Find the first index the normalizers reject: an unpaired UTF-16
    // surrogate, or U+FFFE (the byte-swapped BOM). The two are rejected
    // for different reasons. An unpaired surrogate makes the text
    // ill-formed UTF-16, which genuinely can't be normalized. U+FFFE is
    // a valid scalar that pure UAX #15 normalizes to itself (Corrigendum
    // #9 says noncharacters don't make text ill-formed), but .NET's
    // string.Normalize rejects exactly it and no other noncharacter, and
    // the parser matches .NET so behavior is identical on every runtime.
    // Valid surrogate pairs are skipped, so any surrogate the loop
    // reaches is genuinely unpaired. Returns -1 if it finds none. The
    // parser scans with this before normalizing instead of relying on
    // the runtime's string.Normalize to throw on such input, because not
    // every runtime throws: .NET's does, but Unity's Mono returns such
    // input unchanged.
    public static int FindFirstUnnormalizableIndex(string input)
    {
        for (int i = 0; i < input.Length; i++)
        {
            if (RuneHelpers.IsSurrogatePairAt(input, i))
            {
                i++;
                continue;
            }
            char c = input[i];
            if (char.IsSurrogate(c)) return i;
            if (c == (char)0xFFFE) return i;
        }
        return -1;
    }

    // One factory for the "this text can't be normalized" ArgumentException,
    // so every place that rejects such text produces the same exception
    // with the same stable message, instead of the culture-localized one
    // the BCL throws. The two reasons get different wording because they
    // are different: an unpaired surrogate makes the text ill-formed
    // UTF-16, while U+FFFE is well-formed text that .NET's normalization
    // API rejects anyway (the FindFirstUnnormalizableIndex comment has
    // the full story).
    public static ArgumentException CreateUnnormalizableTextException(string text, int badIndex)
    {
        char badCodeUnit = text[badIndex];
        string reason = char.IsSurrogate(badCodeUnit)
            ? $"U+{(int)badCodeUnit:X4} is an unpaired surrogate half, so the text isn't "
              + "well-formed UTF-16"
            : "U+FFFE is a noncharacter that .NET's string.Normalize rejects, and this "
              + "library matches that behavior on every runtime";
        return new ArgumentException(
            $"Text can't be normalized at index {badIndex}: {reason}.");
    }

    // The runtime-backed implementation. Internal (not private) so the
    // differential tests can compare it against the built-in one directly.
    // Deliberately without the host-globalization check: these are the
    // raw runtime oracle for those tests, and every library caller goes
    // through the checked dispatchers above.
    internal static string NormalizeWithRuntime(string input, NormalizationForm form) =>
        input.Normalize(form);

    internal static bool IsNormalizedWithRuntime(string input, NormalizationForm form) =>
        input.IsNormalized(form);

    // The built-in UAX #15 pipeline: reject text the scan flags (see
    // FindFirstUnnormalizableIndex), decompose
    // (canonically, or fully for the K forms), put combining marks in
    // canonical order, recompose for the composing forms, and hand back
    // the original string instance when nothing changed. Returning the
    // same instance is what lets NormalizedPositionMap's ReferenceEquals
    // fast path skip position mapping, matching the runtime
    // implementation's behavior.
    internal static string NormalizeWithBundledImplementation(string input, NormalizationForm form)
    {
        // ASCII is invariant under all four forms, and an all-ASCII
        // string can't contain a surrogate or U+FFFE, so the common case
        // is one scan with no allocation.
        if (IsAllAscii(input)) return input;

        int badIndex = FindFirstUnnormalizableIndex(input);
        if (badIndex >= 0) throw CreateUnnormalizableTextException(input, badIndex);

        bool useCompatibility =
            form == NormalizationForm.FormKC || form == NormalizationForm.FormKD;
        bool compose =
            form == NormalizationForm.FormC || form == NormalizationForm.FormKC;

        List<int> scalars = Decompose(input, useCompatibility);
        SortCombiningMarks(scalars);
        if (compose) ComposeCanonically(scalars);

        string result = EncodeUtf16(scalars);
        return string.Equals(result, input, StringComparison.Ordinal) ? input : result;
    }

    internal static bool IsNormalizedWithBundledImplementation(string input, NormalizationForm form) =>
        ReferenceEquals(NormalizeWithBundledImplementation(input, form), input);

    private static bool IsAllAscii(string input)
    {
        foreach (char c in input)
        {
            if (c >= 0x80) return false;
        }
        return true;
    }

    // Decode the UTF-16 input (well-formed after the scan above) and
    // append each scalar's full decomposition.
    private static List<int> Decompose(string input, bool useCompatibility)
    {
        var scalars = new List<int>(input.Length + 8);
        int offset = 0;
        while (offset < input.Length)
        {
            char first = input[offset];
            int scalar;
            if (char.IsHighSurrogate(first))
            {
                scalar = char.ConvertToUtf32(first, input[offset + 1]);
                offset += 2;
            }
            else
            {
                scalar = first;
                offset += 1;
            }
            AppendDecomposition(scalar, useCompatibility, scalars);
        }
        return scalars;
    }

    // Recursively expand one scalar's decomposition mapping into the
    // buffer. The table stores mappings single-level, exactly as
    // UnicodeData.txt states them, so a mapped piece can have a mapping
    // of its own (U+01D5 maps to U+00DC + U+0304, and U+00DC maps
    // further). The chains are short, five levels at most. For the
    // canonical forms only canonical mappings apply. For the K forms both
    // kinds apply, and every canonical piece is re-checked because it can
    // have a compatibility mapping of its own (U+1E9B's piece U+017F, the
    // long s, maps to plain s only under the K forms).
    private static void AppendDecomposition(int scalar, bool useCompatibility, List<int> scalars)
    {
        if (scalar >= HangulSyllableBase && scalar < HangulSyllableBase + HangulSyllableCount)
        {
            AppendHangulDecomposition(scalar, scalars);
            return;
        }
        if (TryGetDecomposition(scalar, out ReadOnlySpan<int> expansion, out bool isCompatibility)
            && (useCompatibility || !isCompatibility))
        {
            foreach (int piece in expansion)
                AppendDecomposition(piece, useCompatibility, scalars);
            return;
        }
        scalars.Add(scalar);
    }

    // Hangul syllables decompose arithmetically (Unicode chapter 3.12):
    // a syllable is leading jamo + vowel jamo, plus a trailing jamo when
    // its index within the block says so.
    private const int HangulSyllableBase = 0xAC00;
    private const int HangulLeadingJamoBase = 0x1100;
    private const int HangulVowelJamoBase = 0x1161;
    // Trailing index 0 means "no trailing jamo", so real trailing jamo
    // sit at base + 1 through base + 27.
    private const int HangulTrailingJamoBase = 0x11A7;
    private const int HangulLeadingJamoCount = 19;
    private const int HangulVowelJamoCount = 21;
    private const int HangulTrailingJamoCount = 28;
    private const int HangulSyllableCount =
        HangulLeadingJamoCount * HangulVowelJamoCount * HangulTrailingJamoCount;

    private static void AppendHangulDecomposition(int syllable, List<int> scalars)
    {
        int syllableIndex = syllable - HangulSyllableBase;
        int leadingIndex = syllableIndex / (HangulVowelJamoCount * HangulTrailingJamoCount);
        int vowelIndex = syllableIndex % (HangulVowelJamoCount * HangulTrailingJamoCount)
            / HangulTrailingJamoCount;
        int trailingIndex = syllableIndex % HangulTrailingJamoCount;
        scalars.Add(HangulLeadingJamoBase + leadingIndex);
        scalars.Add(HangulVowelJamoBase + vowelIndex);
        if (trailingIndex > 0)
            scalars.Add(HangulTrailingJamoBase + trailingIndex);
    }

    // The Canonical Ordering Algorithm (The Unicode Standard, section
    // 3.11, D109): within each maximal stretch of scalars whose
    // combining class is nonzero, sort by class, keeping the original
    // order of scalars with equal classes (the stability the spec
    // requires). Real text puts a base plus a few marks in a stretch,
    // and those go through the short exchange sort. Hostile input can
    // pile thousands of marks into one stretch (the case UAX #15
    // section 13's Stream-Safe Text Format exists for), and an exchange
    // sort goes quadratic on that, so stretches past ShortStretchLimit
    // take the stable counting sort instead, which keeps the whole pass
    // linear in the input length.
    private static void SortCombiningMarks(List<int> scalars)
    {
        int index = 0;
        while (index < scalars.Count)
        {
            if (GetCanonicalCombiningClass(scalars[index]) == 0)
            {
                index++;
                continue;
            }
            int stretchStart = index;
            do
            {
                index++;
            }
            while (index < scalars.Count && GetCanonicalCombiningClass(scalars[index]) != 0);

            int stretchLength = index - stretchStart;
            if (stretchLength < 2) continue;
            if (stretchLength <= ShortStretchLimit)
                SortShortStretch(scalars, stretchStart, index);
            else
                SortLongStretch(scalars, stretchStart, index);
        }
    }

    // Stream-safe text (UAX #15 section 13) never has more than 30
    // consecutive non-starters, so every stretch the counting sort sees
    // is already longer than well-behaved text produces.
    private const int ShortStretchLimit = 32;

    // Exchange sort over one stretch, for the common case of a few
    // marks. The exchange only fires when the left class is strictly
    // greater, so scalars with equal classes keep their order.
    private static void SortShortStretch(List<int> scalars, int start, int end)
    {
        int index = start + 1;
        while (index < end)
        {
            byte combiningClass = GetCanonicalCombiningClass(scalars[index]);
            byte previousClass = GetCanonicalCombiningClass(scalars[index - 1]);
            if (previousClass <= combiningClass)
            {
                index++;
                continue;
            }
            (scalars[index - 1], scalars[index]) = (scalars[index], scalars[index - 1]);
            index = index > start + 1 ? index - 1 : index + 1;
        }
    }

    // Stable counting sort over one stretch, keyed on the combining
    // class byte. Three passes: count the scalars in each class, turn
    // the counts into each class's first output slot, then place every
    // scalar in order, which keeps the original order within a class.
    private static void SortLongStretch(List<int> scalars, int start, int end)
    {
        int length = end - start;
        var classes = new byte[length];
        var firstSlotForClass = new int[256];
        for (int offset = 0; offset < length; offset++)
        {
            classes[offset] = GetCanonicalCombiningClass(scalars[start + offset]);
            firstSlotForClass[classes[offset]]++;
        }
        int nextSlot = 0;
        for (int classValue = 0; classValue < firstSlotForClass.Length; classValue++)
        {
            int scalarsInClass = firstSlotForClass[classValue];
            firstSlotForClass[classValue] = nextSlot;
            nextSlot += scalarsInClass;
        }
        var sorted = new int[length];
        for (int offset = 0; offset < length; offset++)
            sorted[firstSlotForClass[classes[offset]]++] = scalars[start + offset];
        for (int offset = 0; offset < length; offset++)
            scalars[start + offset] = sorted[offset];
    }

    // The Canonical Composition Algorithm (The Unicode Standard,
    // section 3.11, D117):
    // walk the decomposed, reordered sequence and compose each scalar
    // with the last starter when nothing blocks it. A scalar is blocked
    // (D115)
    // when a scalar between it and the starter has combining class zero
    // or a class at least as high, which the walk tracks as lastClass,
    // the class of the most recent scalar it kept. lastClass == 0 also
    // covers the starter-plus-starter case (Hangul leading + vowel jamo
    // compose, and both are starters). A sequence that opens with a
    // non-starter has no starter to compose with, which the 256 stand-in
    // encodes: no real class exceeds it, so everything is blocked until
    // the first starter comes along. Composing in place: writeIndex
    // trails readIndex, and each composition shortens the sequence by
    // one.
    private static void ComposeCanonically(List<int> scalars)
    {
        if (scalars.Count == 0) return;

        int starterIndex = 0;
        int starterScalar = scalars[0];
        int lastClass = GetCanonicalCombiningClass(starterScalar);
        if (lastClass != 0) lastClass = 256;

        int writeIndex = 1;
        for (int readIndex = 1; readIndex < scalars.Count; readIndex++)
        {
            int scalar = scalars[readIndex];
            int combiningClass = GetCanonicalCombiningClass(scalar);
            if ((lastClass < combiningClass || lastClass == 0)
                && TryComposePair(starterScalar, scalar, out int composite))
            {
                scalars[starterIndex] = composite;
                starterScalar = composite;
            }
            else
            {
                if (combiningClass == 0)
                {
                    starterIndex = writeIndex;
                    starterScalar = scalar;
                }
                lastClass = combiningClass;
                scalars[writeIndex] = scalar;
                writeIndex++;
            }
        }
        scalars.RemoveRange(writeIndex, scalars.Count - writeIndex);
    }

    // A pair composes when Hangul arithmetic says so (leading + vowel
    // jamo make an LV syllable, LV syllable + trailing jamo make an LVT
    // syllable) or when the derived pair table has an entry.
    private static bool TryComposePair(int first, int second, out int composite)
    {
        if (first >= HangulLeadingJamoBase
            && first < HangulLeadingJamoBase + HangulLeadingJamoCount
            && second >= HangulVowelJamoBase
            && second < HangulVowelJamoBase + HangulVowelJamoCount)
        {
            int leadingIndex = first - HangulLeadingJamoBase;
            int vowelIndex = second - HangulVowelJamoBase;
            composite = HangulSyllableBase
                + (leadingIndex * HangulVowelJamoCount + vowelIndex) * HangulTrailingJamoCount;
            return true;
        }
        if (first >= HangulSyllableBase
            && first < HangulSyllableBase + HangulSyllableCount
            && (first - HangulSyllableBase) % HangulTrailingJamoCount == 0
            && second > HangulTrailingJamoBase
            && second < HangulTrailingJamoBase + HangulTrailingJamoCount)
        {
            composite = first + (second - HangulTrailingJamoBase);
            return true;
        }
        return CompositionPairs.Value.TryGetValue(PairKey(first, second), out composite);
    }

    // UTF-16 encoding constants (Unicode chapter 3.9): a scalar above
    // the Basic Multilingual Plane is rebased against the start of the
    // supplementary planes and split into a surrogate pair, ten payload
    // bits to each half.
    private const int BasicMultilingualPlaneEnd = 0xFFFF;
    private const int SupplementaryPlaneStart = 0x10000;
    private const int HighSurrogateStart = 0xD800;
    private const int LowSurrogateStart = 0xDC00;
    private const int SurrogatePayloadBits = 10;
    private const int SurrogatePayloadMask = 0x3FF;

    private static string EncodeUtf16(List<int> scalars)
    {
        var builder = new StringBuilder(scalars.Count + 8);
        foreach (int scalar in scalars)
        {
            if (scalar <= BasicMultilingualPlaneEnd)
            {
                builder.Append((char)scalar);
            }
            else
            {
                int supplementaryOffset = scalar - SupplementaryPlaneStart;
                builder.Append((char)(
                    HighSurrogateStart + (supplementaryOffset >> SurrogatePayloadBits)));
                builder.Append((char)(
                    LowSurrogateStart + (supplementaryOffset & SurrogatePayloadMask)));
            }
        }
        return builder.ToString();
    }

    // Canonical combining class lookup in the generated transition
    // arrays. Same search shape as GraphemeSegmentation.GetBreakType.
    internal static byte GetCanonicalCombiningClass(int codePointValue)
    {
        // ASCII never has combining marks, so handle it with one range
        // check instead of a binary search.
        if ((uint)codePointValue < 0x80) return 0;

        // Binary search for the last transition at or below the code
        // point. CanonicalCombiningClassRangeStarts[0] is 0, so the
        // search always lands.
        int low = 0;
        int high = CanonicalCombiningClassRangeStarts.Length - 1;
        while (low < high)
        {
            int middle = (low + high + 1) >> 1;
            if (CanonicalCombiningClassRangeStarts[middle] <= codePointValue)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }
        return CanonicalCombiningClassRangeValues[low];
    }

    // Single-level decomposition mapping lookup in the generated tables.
    // The expansion is exactly what UnicodeData.txt states for the code
    // point. Recursive expansion is AppendDecomposition's job.
    internal static bool TryGetDecomposition(
        int codePointValue, out ReadOnlySpan<int> expansion, out bool isCompatibility)
    {
        int index = Array.BinarySearch(DecompositionCodePoints, codePointValue);
        if (index < 0)
        {
            expansion = default;
            isCompatibility = false;
            return false;
        }
        int packed = DecompositionEntries[index];
        isCompatibility = (packed & 1) != 0;
        int expansionLength = (packed >> 1) & 0x1F;
        int expansionOffset = packed >> 6;
        expansion = new ReadOnlySpan<int>(
            DecompositionExpansions, expansionOffset, expansionLength);
        return true;
    }

    internal static bool IsFullCompositionExclusion(int codePointValue) =>
        Array.BinarySearch(FullCompositionExclusions, codePointValue) >= 0;

    // The canonical composition pairs, derived once from the
    // decomposition table instead of generated: a pair is every
    // non-excluded canonical two-scalar mapping, read backward. Deriving
    // keeps the pair table incapable of drifting from the decomposition
    // data it must mirror, at the cost of one walk over the table per
    // process. Lazy rather than a field initializer because the
    // generated arrays live in the other half of this partial class, and
    // C# doesn't promise an initialization order across partial-class
    // files: a field initializer here can run before the arrays it
    // reads are assigned. Lazy defers the build to the first
    // composition lookup, long after type initialization finished.
    private static readonly Lazy<Dictionary<long, int>> CompositionPairs = new(BuildCompositionPairs);

    private static Dictionary<long, int> BuildCompositionPairs()
    {
        var pairs = new Dictionary<long, int>(1024);
        for (int entryIndex = 0; entryIndex < DecompositionCodePoints.Length; entryIndex++)
        {
            int packed = DecompositionEntries[entryIndex];
            bool isCompatibility = (packed & 1) != 0;
            int expansionLength = (packed >> 1) & 0x1F;
            if (isCompatibility || expansionLength != 2) continue;

            int composite = DecompositionCodePoints[entryIndex];
            if (IsFullCompositionExclusion(composite)) continue;

            int expansionOffset = packed >> 6;
            int first = DecompositionExpansions[expansionOffset];
            int second = DecompositionExpansions[expansionOffset + 1];
            Invariant.That(GetCanonicalCombiningClass(composite) == 0,
                $"composition pair target U+{composite.ToString("X4")} must be a starter");
            long key = PairKey(first, second);
            Invariant.That(!pairs.ContainsKey(key),
                $"duplicate composition pair for U+{first.ToString("X4")} U+{second.ToString("X4")}");
            pairs.Add(key, composite);
        }
        return pairs;
    }

    private static long PairKey(int first, int second) => ((long)first << 32) | (uint)second;
}
