using System;
using System.Globalization;
using System.Text;

namespace InductorParser.Lexing;

// Maps a char index in a normalized string back to a char index in
// the caller's original (un-normalized) string, so positions land in
// the coordinate system the caller passed in rather than the internal
// normalized one. Used for failure positions (Rule's RecordFailure
// path) and for symbol source ranges (Symbol.SourceRange). See
// Rule.Compile(NormalizationForm?) for the wider picture.
//
// Two walker shapes, one picked by the form:
//
//   * Canonical forms (FormC, FormD): lockstep walk, one grapheme
//     per step on each side. UAX #29 §3 guarantees grapheme
//     boundaries don't change under canonical normalization, so the
//     two sides stay in sync.
//
//   * Compatibility forms (FormKC, FormKD): walk the original
//     grapheme by grapheme, verify at each boundary that normalizing
//     the chunk-since-the-last-verified-boundary matches the next
//     portion of the normalized string. The check fails when the
//     conversions of adjacent original graphemes merge into one
//     grapheme, which in Unicode 16 only Korean jamo sequences do
//     (compatibility and halfwidth forms). The chunk then absorbs
//     the next grapheme and the check is tried again. A lone
//     grapheme that expands (the fi ligature becoming "fi", Thai
//     SARA AM splitting in two) passes the check on the first try
//     and never needs absorption. The check is general: it catches
//     any drift between the per-grapheme walk and the normalized
//     string, so correctness doesn't depend on which script
//     triggered it. Korean jamo being the only merge case in
//     Unicode 16 just keeps the absorbed chunks small. See
//     docs/MappingPositionsAfterNormalization.md for why this
//     per-boundary check is correct.
//
// When the position lands inside a grapheme (or multi-grapheme region)
// that got rewritten, the walker snaps back to the start of that
// region so editor highlighting covers the whole affected text. Runs
// on demand when a position is mapped, off the parsing hot path.
internal static class NormalizedPositionMap
{
    public static int TranslateToOriginal(string original, string normalized, int normalizedIndex, NormalizationForm? form)
    {
        // Fast path: normalization was a no-op and the runtime gave us the
        // same string instance, so positions already match.
        if (ReferenceEquals(original, normalized))
            return normalizedIndex;

        if (normalizedIndex <= 0)
            return 0;
        if (normalizedIndex >= normalized.Length)
            return original.Length;

        if (form == NormalizationForm.FormKC || form == NormalizationForm.FormKD)
            return TranslateViaPerGraphemeNormalize(original, normalized, normalizedIndex, form.Value);

        return TranslateViaLockstep(original, normalized, normalizedIndex);
    }

    // Lockstep walker for canonical forms. Valid because NFC and NFD
    // preserve grapheme boundaries 1:1.
    private static int TranslateViaLockstep(string original, string normalized, int normalizedIndex)
    {
        // Step both sides through the shared per-input grapheme-boundary
        // cache (a bool-array lookup) rather than StringInfo.GetNextTextElement,
        // which allocates a substring for every grapheme just to read its
        // length. The normalized side's index was already built by the lexer
        // during the parse. The original side's is built once here and reused
        // by every later position lookup on the same input. This is the whole
        // reason GraphemeClusterIndex exists (see its header), so a tree walk
        // that reads many Symbol.SourceRange / SourceText values doesn't pay a
        // per-grapheme allocation on every node.
        var normalizedIndexCache = GraphemeClusterIndex.For(normalized);
        var originalIndexCache = GraphemeClusterIndex.For(original);
        int origPos = 0;
        int normPos = 0;
        while (normPos < normalized.Length && origPos < original.Length)
        {
            // LengthAt returns at least one char at any in-range cluster
            // start, and the walk only ever lands on cluster starts (it
            // advances by whole clusters from 0). The Invariant.That calls
            // catch the impossible-zero case that would spin this loop forever.
            int normStep = normalizedIndexCache.LengthAt(normPos);
            Invariant.That(normStep > 0,
                $"GraphemeClusterIndex.LengthAt returned an empty element on the normalized string "
                + $"at position {normPos} (length {normalized.Length}) in TranslateViaLockstep.");

            int origStep = originalIndexCache.LengthAt(origPos);
            Invariant.That(origStep > 0,
                $"GraphemeClusterIndex.LengthAt returned an empty element on the original string "
                + $"at position {origPos} (length {original.Length}) in TranslateViaLockstep.");

            int normNext = normPos + normStep;
            if (normNext > normalizedIndex)
                return origPos;
            if (normNext == normalizedIndex)
                return origPos + origStep;

            normPos = normNext;
            origPos += origStep;
        }

        // Ran off one side before the other. Clamp to original length so
        // callers always get a valid index into the original string.
        return origPos <= original.Length ? origPos : original.Length;
    }

    // Walker for compatibility forms (FormKC, FormKD), where grapheme
    // boundaries can shift under normalization so the lockstep walk
    // doesn't hold. Walks the original grapheme by grapheme and grows a
    // chunk until its normalized form matches the next portion of the
    // normalized string. See the file header and
    // docs/MappingPositionsAfterNormalization.md for why this is correct.
    private static int TranslateViaPerGraphemeNormalize(string original, string normalized, int normalizedIndex, NormalizationForm form)
    {
        int origPos = 0;
        int normPos = 0;
        int lastSafeOrigPos = 0;

        while (origPos < original.Length)
        {
            // GetNextTextElement always returns at least one char at a valid
            // position. The Invariant.That catches the impossible-zero case
            // that would spin this loop forever.
            string grapheme = StringInfo.GetNextTextElement(original, origPos);
            int graphemeLength = grapheme.Length;
            Invariant.That(graphemeLength > 0,
                $"StringInfo.GetNextTextElement returned an empty element on the original string "
                + $"at position {origPos} (length {original.Length}) in TranslateViaPerGraphemeNormalize.");
            origPos += graphemeLength;

            string chunk = original[lastSafeOrigPos..origPos];
            string chunkNormalized = chunk.Normalize(form);

            if (normPos + chunkNormalized.Length <= normalized.Length
                && string.CompareOrdinal(normalized, normPos, chunkNormalized, 0, chunkNormalized.Length) == 0)
            {
                int newNormPos = normPos + chunkNormalized.Length;
                if (newNormPos > normalizedIndex)
                    return lastSafeOrigPos;
                if (newNormPos == normalizedIndex)
                    return origPos;
                normPos = newNormPos;
                lastSafeOrigPos = origPos;
            }
            // else: chunk grows on the next iteration (lastSafeOrigPos unchanged)
        }

        return original.Length;
    }
}
