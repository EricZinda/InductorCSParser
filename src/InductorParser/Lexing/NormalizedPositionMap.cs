using System;
using System.Globalization;
using System.Text;

namespace InductorParser.Lexing;

// Maps a char index for a normalized string back to a char index for
// the caller's original (un-normalized) string, so ParseResult can
// report failure positions in the coordinate system the caller passed
// in rather than the internal normalized one. See
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
//     portion of the normalized string. When the check fails (Korean
//     compatibility jamo is the known case), the chunk absorbs the
//     next grapheme and the check is tried again.
//
// When the failure position lands inside a grapheme (or multi-grapheme
// region) that got rewritten, the walker snaps back to the start of
// that region so editor highlighting covers the whole offending text.
// Only runs on parse failure, off the hot path.
//
// See docs/MappingPositionsAfterNormalization.md for the full argument and
// the UAX #29 citations.
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
        int origPos = 0;
        int normPos = 0;
        while (normPos < normalized.Length && origPos < original.Length)
        {
            // GetNextTextElement should always return at least one char at a
            // valid in-bounds position. Asserts catch the impossible-zero case
            // so the loop can't spin forever on a step that ever came back 0.
            int normStep = StringInfo.GetNextTextElement(normalized, normPos).Length;
            Invariant.That(normStep > 0,
                $"StringInfo.GetNextTextElement returned an empty element on the normalized string "
                + $"at position {normPos} (length {normalized.Length}) in TranslateViaLockstep.");

            int origStep = StringInfo.GetNextTextElement(original, origPos).Length;
            Invariant.That(origStep > 0,
                $"StringInfo.GetNextTextElement returned an empty element on the original string "
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

    // Walker for compatibility forms (FormKC, FormKD). Walks the
    // original grapheme by grapheme, verifying at each boundary that
    // normalizing the chunk-since-the-last-verified-boundary matches
    // the next portion of the normalized string. When the check
    // fails (Korean compatibility jamo is the known example), the
    // chunk absorbs the next grapheme and the check is tried again.
    // When the failure position lands inside such a region, the
    // walker snaps back to the start of the region.
    //
    // See docs/MappingPositionsAfterNormalization.md for the full argument
    // and the UAX #29 citation.
    private static int TranslateViaPerGraphemeNormalize(string original, string normalized, int normalizedIndex, NormalizationForm form)
    {
        int origPos = 0;
        int normPos = 0;
        int lastSafeOrigPos = 0;

        while (origPos < original.Length)
        {
            // GetNextTextElement should always return at least one char at a
            // valid in-bounds position. Assert catches the impossible-zero
            // case so the loop can't spin forever on a length that ever came
            // back 0.
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
