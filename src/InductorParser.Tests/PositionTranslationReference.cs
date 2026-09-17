using System;
using System.Text;
using NUnit.Framework;
using InductorParser.Lexing;

namespace InductorParser.Tests;

// Brute-force reference for NormalizedPositionMap.TranslateToOriginal,
// shared by the everyday position-translation tests in
// NormalizationTests (Core/, synced to Unity) and the opt-in corpus
// sweep in NormalizationConformanceTests (Lexing/, CoreCLR only). It
// lives at the test project root because that's the one place both of
// those can reach: syncteststounity.sh copies every root-level helper.
//
// The reference walks the original grapheme by grapheme and explicitly
// checks at each boundary whether normalizing the prefix of the original
// up to that boundary produces a prefix of the full normalized string.
// That's the safe-boundary condition
// docs/MappingPositionsAfterNormalization.md spells out. It
// re-normalizes the full prefix on every iteration (no
// chunk-since-last-verified optimization), which makes it obviously
// correct at the cost of being O(N²). It's only a test reference.
//
// The walkers in NormalizedPositionMap are the fast versions. The
// per-grapheme comparison walk for FormKC and FormKD is the
// linear-amortized form of this same check. The lockstep walk for FormC
// and FormD skips the check entirely and pairs the i-th cluster of the
// original with the i-th cluster of the normalized string, relying on
// UAX #29's promise that cluster boundaries don't move under canonical
// equivalence. This reference never assumes that promise, so comparing
// the lockstep walk against it is what checks the promise against the
// segmenter and normalizer actually in use. The doc's Optimization for
// FormC/D section says which assumption that is.
//
// The reference segments and normalizes through the parser's own
// helpers (GraphemeHelpers, NormalizationHelpers), so it and the walker
// under test always compute from the same implementations, on every
// runtime this file syncs to.
public static class PositionTranslationReference
{
    // Iterate every position in the normalized string and compare
    // TranslateToOriginal's result against the reference walk. Fails the
    // test at the first position where they differ.
    public static void AssertTranslatorAgreesWithWholeString(string original, NormalizationForm form)
    {
        Assert.That(FindDisagreement(original, form), Is.Null);
    }

    // Same comparison, but returns a description of the first
    // disagreement instead of asserting, or null when every position
    // agrees. The corpus sweep uses this so one bad line doesn't hide
    // the rest.
    //
    // The normalized form is re-allocated with new string(...) so the
    // ReferenceEquals fast path inside TranslateToOriginal doesn't
    // short-circuit before the walker runs (which would happen for
    // inputs whose normalized form is content-identical to the
    // original).
    public static string? FindDisagreement(string original, NormalizationForm form)
    {
        string normalized = new string(NormalizationHelpers.Normalize(original, form).ToCharArray());
        for (int i = 0; i <= normalized.Length; i++)
        {
            int translatorResult = NormalizedPositionMap.TranslateToOriginal(original, normalized, i, form);
            int referenceResult = WholeStringPositionMap(original, i, form);
            if (translatorResult != referenceResult)
            {
                return $"original {NormalizationExamples.Hex(original)} under {form}: "
                    + $"at normalizedIndex={i} TranslateToOriginal returned {translatorResult}, "
                    + $"the reference walk returned {referenceResult}";
            }
        }
        return null;
    }

    // Return the largest safe original boundary at or before
    // normalizedIndex, where "safe" means the original prefix ending at
    // that boundary normalizes to a prefix of the full normalized string.
    public static int WholeStringPositionMap(string original, int normalizedIndex, NormalizationForm form)
    {
        string normalized = NormalizationHelpers.Normalize(original, form);
        if (normalizedIndex <= 0) return 0;
        if (normalizedIndex >= normalized.Length) return original.Length;

        int bestSafeOriginalPosition = 0;
        int originalPosition = 0;
        while (originalPosition < original.Length)
        {
            int step = GraphemeHelpers.FirstClusterLength(original.AsSpan(originalPosition));
            if (step <= 0) step = 1;
            originalPosition += step;

            string prefixNormalized = NormalizationHelpers.Normalize(original[..originalPosition], form);

            // Safe boundary check: prefixNormalized must be an actual
            // prefix of the full normalized string (not just length-
            // compatible).
            bool isSafe = prefixNormalized.Length <= normalized.Length
                && string.CompareOrdinal(normalized, 0, prefixNormalized, 0, prefixNormalized.Length) == 0;

            if (isSafe)
            {
                if (prefixNormalized.Length > normalizedIndex)
                    return bestSafeOriginalPosition;
                if (prefixNormalized.Length == normalizedIndex)
                    return originalPosition;
                bestSafeOriginalPosition = originalPosition;
            }
        }

        return original.Length;
    }
}
