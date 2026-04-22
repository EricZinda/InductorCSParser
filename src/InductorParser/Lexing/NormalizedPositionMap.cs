using System.Globalization;
using System.Text;

namespace InductorParser.Lexing;

// Maps a char index for a normalized string back to a char index for
// the caller's original (un-normalized) string, so ParseResult can report
// failure positions in the coordinate system the caller passed in rather
// than the internal normalized one. See ParseOptions.NormalizeInput for
// the wider picture.
//
// Cost: zero in the common case (input already in the target normalization
// form, so String.Normalize returned the same reference and translation is
// a no-op reference check). When normalization actually rewrote the input,
// one O(normalizedIndex) walk per parse failure. Not paid on the success
// path.
//
// Two walker shapes, one picked by the form:
//
//   * Canonical forms (FormC, FormD) don't change how many visible
//     characters a string has. They may swap one representation of "é"
//     (two UTF-16 chars: "e" plus a combining accent) for another (one
//     UTF-16 char: precomposed "é"), but either way it still counts as
//     one visible character. Walking both strings in lockstep — one
//     visible character per step on each side — stays in sync.
//     Cheapest path.
//
//   * Compatibility forms (FormKC, FormKD) CAN change the visible-
//     character count: the "fi" ligature is one visible character
//     that becomes two ("f" + "i") after normalization. Similarly
//     "①" → "1", fullwidth "Ａ" → "A". The lockstep walk would drift
//     out of sync every time that happens, because one step on the
//     original corresponds to a different number of steps on the
//     normalized side. Instead we walk the original one visible
//     character at a time, and for each one we call String.Normalize
//     to see how many characters it covers on the normalized side,
//     summing as we go. More expensive (one allocation per step)
//     but correct when rewrites change character counts.
//
// Semantics: when the failure lands inside a character sequence that got
// rewritten (a combining sequence composed, or a ligature folded), the
// returned position is the start of that sequence in the original string.
// Editors want to highlight the whole bad grapheme or ligature anyway, so
// this matches what a diagnostic consumer expects to see.
//
// Grapheme segmentation tracks whatever the .NET runtime the parser is
// compiled on provides: UAX #29 compliant on .NET 5 and later,
// slightly-off on legacy runtimes (a handful of real grapheme clusters
// segment incorrectly). The translator uses the same primitive the
// grapheme lexer does, so whatever the lexer saw, the translator sees
// too.
internal static class NormalizedPositionMap
{
    public static int TranslateToOriginal(string original, string normalized, int normalizedIndex, NormalizationForm? form)
    {
        // Fast path: normalization was a no-op, so positions already match.
        // String.Normalize returns the same instance when the input is
        // already in the target form, which is essentially all typed and
        // web-sourced text.
        if (ReferenceEquals(original, normalized))
            return normalizedIndex;

        if (normalizedIndex <= 0)
            return 0;
        if (normalizedIndex >= normalized.Length)
            return original.Length;

        if (form == NormalizationForm.FormKC || form == NormalizationForm.FormKD)
            return TranslateViaPerGraphemeNormalize(original, normalizedIndex, form.Value);

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
            int normStep = StringInfo.GetNextTextElement(normalized, normPos).Length;
            if (normStep <= 0) normStep = 1;

            int origStep = StringInfo.GetNextTextElement(original, origPos).Length;
            if (origStep <= 0) origStep = 1;

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

    // Per-grapheme walker for compatibility forms. Normalize each original
    // grapheme on its own and advance the normalized side by however many
    // chars that grapheme turned into. One original grapheme can cover
    // multiple normalized chars (ligature "ﬁ" turns into "f" + "i"), and
    // we move a hit anywhere inside that range back to the start of the
    // original grapheme.
    //
    // Why this isn't perfect and why it still works:
    //
    // Normalization does two things. Step one is a fixed lookup: each
    // rune gets swapped for its decomposed form from UnicodeData.txt.
    // That step doesn't care about context. Step two sorts adjacent
    // combining marks into a canonical order, and that step IS
    // context-sensitive: two marks next to each other might swap based on
    // their combining classes. Because of step two, Unicode says
    // normalization is "not closed under concatenation" (UAX #15 section
    // 1.4, "accents are canonically ordered, and may rearrange around
    // the point where the strings are joined"). You cannot just split
    // a string at an arbitrary point, normalize the pieces separately,
    // and stitch them back together and trust the result.
    //
    // The spec's "safe to split here" positions have a name: stable
    // code points (UAX #15 section 9.1). Grapheme cluster boundaries
    // from UAX #29 are not the same thing. So splitting by grapheme is
    // an engineering shortcut, not the spec-blessed operation.
    //
    // The shortcut is safe for real text because UAX #29 rule GB9 keeps
    // combining marks glued to their base character inside the same
    // grapheme cluster. The step-two reordering problem needs combining
    // marks on both sides of the split to bite. GB9 says there are never
    // any on the "next grapheme" side. So for any text that follows the
    // normal convention of combining marks following their base,
    // per-grapheme normalization gives the same answer as whole-string
    // normalization.
    //
    // The edge case that falls outside this argument: a combining mark
    // sitting on its own with no preceding base (at the very start of the
    // input, or immediately after a control character). The mark forms
    // its own "defective" grapheme, and per-grapheme normalization can
    // differ from whole-string normalization by one grapheme's worth of
    // char offset. ErrorCharIndex stays a valid index into the original
    // input; it just lands at an adjacent grapheme boundary instead of
    // the exact one. No editor highlight will notice the difference.
    private static int TranslateViaPerGraphemeNormalize(string original, int normalizedIndex, NormalizationForm form)
    {
        int origPos = 0;
        int normPos = 0;
        while (origPos < original.Length)
        {
            string origGrapheme = StringInfo.GetNextTextElement(original, origPos);
            int origStep = origGrapheme.Length;
            if (origStep <= 0) origStep = 1;

            int normStep = origGrapheme.Normalize(form).Length;
            int normNext = normPos + normStep;

            if (normNext > normalizedIndex)
                return origPos;
            if (normNext == normalizedIndex)
                return origPos + origStep;

            origPos += origStep;
            normPos = normNext;
        }

        return original.Length;
    }
}
