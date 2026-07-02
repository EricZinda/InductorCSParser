using System.Text;
using NUnit.Framework;
using InductorParser.Lexing;

namespace InductorParser.Tests.DocExamples;

// Verifies the Unicode claims in docs/MappingPositionsAfterNormalization.md.
// The doc is careful to claim only that Korean jamo merge the conversions of
// ADJACENT original graphemes into one grapheme, not that they're the only
// characters that change the grapheme count. Plain count changes are common
// under FormKC and FormKD (the fi ligature, Thai SARA AM, the Arabic
// ligature U+FDFA) and the ordinary per-grapheme walk handles them without
// any absorption. These tests are the counterexamples that rule out the
// stronger count-based claim, plus the walker behavior the doc describes
// for both shapes.
[TestFixture]
public class MappingPositionsAfterNormalizationExamples
{
    // Non-Korean grapheme-count changes under the compatibility forms.
    // Every assertion here changes a count with no Korean jamo anywhere,
    // which is why the doc can't claim "Korean jamo are the only case
    // where the grapheme count changes".
    [Test]
    public void Compatibility_normalization_changes_grapheme_counts_without_Korean_jamo()
    {
        // Thai "กำ" (KO KAI + SARA AM) is one grapheme. Both
        // compatibility forms decompose SARA AM into NIKHAHIT (U+0E4D)
        // + SARA AA (U+0E32), and the converted text is two graphemes.
        Assert.That(GraphemeHelpers.Count(UnicodeExamples.ThaiKamGrapheme), Is.EqualTo(1));
        Assert.That(GraphemeHelpers.Count(UnicodeExamples.ThaiKamGrapheme.Normalize(NormalizationForm.FormKD)), Is.EqualTo(2));
        Assert.That(GraphemeHelpers.Count(UnicodeExamples.ThaiKamGrapheme.Normalize(NormalizationForm.FormKC)), Is.EqualTo(2));

        // The doc's own easy case changes the count too: the fi
        // ligature is one grapheme that converts to the two graphemes
        // "fi".
        Assert.That(GraphemeHelpers.Count(UnicodeExamples.FiLigatureGrapheme), Is.EqualTo(1));
        Assert.That(GraphemeHelpers.Count(UnicodeExamples.FiLigatureGrapheme.Normalize(NormalizationForm.FormKC)), Is.EqualTo(2));

        // The Arabic ligature U+FDFA converts to a whole 18-grapheme
        // phrase.
        Assert.That(GraphemeHelpers.Count(UnicodeExamples.ArabicLigatureSallallahouGrapheme), Is.EqualTo(1));
        Assert.That(GraphemeHelpers.Count(UnicodeExamples.ArabicLigatureSallallahouGrapheme.Normalize(NormalizationForm.FormKC)), Is.EqualTo(18));
    }

    // The merge case isn't limited to the Hangul Compatibility Jamo
    // block the doc's worked example uses. The halfwidth jamo from the
    // Halfwidth and Fullwidth Forms block convert to the same conjoining
    // jamo, so a halfwidth pair also composes into one syllable under
    // FormKC. This is why the doc says "compatibility and halfwidth
    // forms" when it names Korean jamo as the merge trigger.
    [Test]
    public void Halfwidth_jamo_pair_merges_into_one_syllable_under_FormKC()
    {
        string halfwidthPair = UnicodeExamples.HalfwidthHangulKiyeokGrapheme + UnicodeExamples.HalfwidthHangulAGrapheme;

        Assert.That(GraphemeHelpers.Count(halfwidthPair), Is.EqualTo(2));
        Assert.That(halfwidthPair.Normalize(NormalizationForm.FormKC),
            Is.EqualTo(UnicodeExamples.HangulGaPrecomposedGrapheme),
            "halfwidth kiyeok + halfwidth a converts to the single syllable U+AC00");
        Assert.That(GraphemeHelpers.Count(halfwidthPair.Normalize(NormalizationForm.FormKC)), Is.EqualTo(1));
    }

    // The doc's claim that a lone expanding grapheme is handled by the
    // ordinary per-grapheme walk: the boundary after the expanded Thai
    // grapheme maps to the right original offset, and positions inside
    // the expansion snap back to the grapheme start.
    [Test]
    public void Walker_maps_positions_through_Thai_SARA_AM_expansion_under_FormKD()
    {
        string original = UnicodeExamples.ThaiKamGrapheme + "Z";
        string normalized = original.Normalize(NormalizationForm.FormKD);
        Assert.That(normalized.Length, Is.EqualTo(4),
            "ko kai + nikhahit + sara aa + Z");

        // Normalized index 3 is the boundary after the expanded Thai
        // grapheme (the start of Z). In the original, Z sits at index 2.
        Assert.That(NormalizedPositionMap.TranslateToOriginal(original, normalized, 3, NormalizationForm.FormKD),
            Is.EqualTo(2));

        // Indices 1 and 2 sit inside the expansion. The original has no
        // corresponding offsets, so the walker snaps back to the start
        // of the Thai grapheme.
        Assert.That(NormalizedPositionMap.TranslateToOriginal(original, normalized, 1, NormalizationForm.FormKD),
            Is.EqualTo(0));
        Assert.That(NormalizedPositionMap.TranslateToOriginal(original, normalized, 2, NormalizationForm.FormKD),
            Is.EqualTo(0));
    }

    // The merge case through the walker, using the halfwidth block: the
    // same absorb-and-retry behavior the doc describes for the
    // compatibility jamo example.
    [Test]
    public void Walker_maps_position_after_merged_halfwidth_jamo_under_FormKC()
    {
        string original = UnicodeExamples.HalfwidthHangulKiyeokGrapheme + UnicodeExamples.HalfwidthHangulAGrapheme + "Z";
        string normalized = original.Normalize(NormalizationForm.FormKC);
        Assert.That(normalized, Is.EqualTo(UnicodeExamples.HangulGaPrecomposedGrapheme + "Z"));

        // Normalized index 1 is the boundary after the composed
        // syllable. Both halfwidth jamo chars converted into it, so the
        // right original answer is index 2, after both of them.
        Assert.That(NormalizedPositionMap.TranslateToOriginal(original, normalized, 1, NormalizationForm.FormKC),
            Is.EqualTo(2));
    }
}
