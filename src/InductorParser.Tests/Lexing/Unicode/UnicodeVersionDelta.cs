using System;
using NUnit.Framework;

namespace InductorParser.Tests;

// The code points Unicode 16.0 added, as sorted inclusive ranges
// hand-transcribed from the Age=V16_0 section of
// https://www.unicode.org/Public/16.0.0/ucd/DerivedAge.txt (47 range
// lines counted by hand, 5,185 code points per the section's own
// "Total code points" footer).
//
// Why the differential tests need this: the built-in normalizer's
// tables are Unicode 16.0, but the runtime oracle the sweeps compare
// against is the app-local ICU 72.1, which is built from Unicode 15.0
// data. ICU 72.1 treats these code points as unassigned and passes
// them through normalization unchanged, so comparing the two
// implementations on them proves nothing and fails wherever 16.0 gave
// one of them real normalization behavior. Unicode's normalization
// stability policy keeps everything assigned through 15.0 normalizing
// identically under both, so the sweeps skip exactly this set and stay
// meaningful everywhere else. The 16.0 behavior of the skipped code
// points is proven by the NormalizationTest-16.0.0.txt conformance
// suite and the [Explicit] UCD re-derivation test instead.
//
// Unicode 15.1 needs no entries: it added CJK Extension I plus five
// ideographic description characters, and none of them have any
// normalization behavior (no decompositions, no combining classes,
// no quick-check flags), so ICU 72.1 and the 16.0 tables already
// agree there.
//
// The file sits under Lexing/Unicode/ because that folder never syncs
// to the Unity test project.
internal static class UnicodeVersionDelta
{
    // Inclusive range bounds, sorted ascending, transcribed in
    // DerivedAge.txt order. Singletons are a range whose start and end
    // are equal.
    internal static readonly int[] NewInUnicode16RangeStarts =
    {
        0x0897, 0x1B4E, 0x1B7F, 0x1C89, 0x2427, 0x31E4, 0xA7CB, 0xA7DA,
        0x105C0, 0x10D40, 0x10D69, 0x10D8E, 0x10EC2, 0x10EFC, 0x11380, 0x1138B,
        0x1138E, 0x11390, 0x113B7, 0x113C2, 0x113C5, 0x113C7, 0x113CC, 0x113D7,
        0x113E1, 0x116D0, 0x11BC0, 0x11BF0, 0x11F5A, 0x13460, 0x16100, 0x16D40,
        0x18CFF, 0x1CC00, 0x1CD00, 0x1E5D0, 0x1E5FF, 0x1F8B2, 0x1F8C0, 0x1FA89,
        0x1FA8F, 0x1FABE, 0x1FAC6, 0x1FADC, 0x1FADF, 0x1FAE9, 0x1FBCB,
    };

    internal static readonly int[] NewInUnicode16RangeEnds =
    {
        0x0897, 0x1B4F, 0x1B7F, 0x1C8A, 0x2429, 0x31E5, 0xA7CD, 0xA7DC,
        0x105F3, 0x10D65, 0x10D85, 0x10D8F, 0x10EC4, 0x10EFC, 0x11389, 0x1138B,
        0x1138E, 0x113B5, 0x113C0, 0x113C2, 0x113C5, 0x113CA, 0x113D5, 0x113D8,
        0x113E2, 0x116E3, 0x11BE1, 0x11BF9, 0x11F5A, 0x143FA, 0x16139, 0x16D79,
        0x18CFF, 0x1CCF9, 0x1CEB3, 0x1E5FA, 0x1E5FF, 0x1F8BB, 0x1F8C1, 0x1FA89,
        0x1FA8F, 0x1FABE, 0x1FAC6, 0x1FADC, 0x1FADF, 0x1FAE9, 0x1FBEF,
    };

    public static bool IsNewInUnicode16(int codePoint)
    {
        int index = Array.BinarySearch(NewInUnicode16RangeStarts, codePoint);
        if (index >= 0)
            return true;
        int previousRange = ~index - 1;
        return previousRange >= 0 && codePoint <= NewInUnicode16RangeEnds[previousRange];
    }
}

// A transcription typo in the ranges above would silently make the
// sweeps skip too much or too little, so check the shape and the
// total against the counts DerivedAge.txt states.
[TestFixture]
public class UnicodeVersionDeltaTests
{
    [Test]
    public void Ranges_are_ascending_disjoint_and_total_the_stated_count()
    {
        int[] starts = UnicodeVersionDelta.NewInUnicode16RangeStarts;
        int[] ends = UnicodeVersionDelta.NewInUnicode16RangeEnds;
        Assert.That(starts.Length, Is.EqualTo(ends.Length));
        Assert.That(starts.Length, Is.EqualTo(47), "DerivedAge.txt lists 47 V16_0 ranges");

        long total = 0;
        for (int index = 0; index < starts.Length; index++)
        {
            Assert.That(starts[index], Is.LessThanOrEqualTo(ends[index]),
                $"range {index} must not end before it starts");
            if (index > 0)
            {
                Assert.That(starts[index], Is.GreaterThan(ends[index - 1]),
                    $"range {index} must start after range {index - 1} ends");
            }
            total += ends[index] - starts[index] + 1;
        }
        Assert.That(total, Is.EqualTo(5185), "DerivedAge.txt states 5185 total code points");
    }

    [Test]
    public void Membership_matches_the_range_bounds()
    {
        // A singleton, both of its neighbors, and the extremes of the
        // first and last ranges.
        Assert.That(UnicodeVersionDelta.IsNewInUnicode16(0x0897), Is.True);
        Assert.That(UnicodeVersionDelta.IsNewInUnicode16(0x0896), Is.False);
        Assert.That(UnicodeVersionDelta.IsNewInUnicode16(0x0898), Is.False);

        // U+113C6 is the unassigned gap between the U+113C5 singleton
        // and the U+113C7..U+113CA range, and must stay out.
        Assert.That(UnicodeVersionDelta.IsNewInUnicode16(0x113C5), Is.True);
        Assert.That(UnicodeVersionDelta.IsNewInUnicode16(0x113C6), Is.False);
        Assert.That(UnicodeVersionDelta.IsNewInUnicode16(0x113C7), Is.True);

        // The Egyptian hieroglyph range is the big one, check both ends.
        Assert.That(UnicodeVersionDelta.IsNewInUnicode16(0x13460), Is.True);
        Assert.That(UnicodeVersionDelta.IsNewInUnicode16(0x1345F), Is.False);
        Assert.That(UnicodeVersionDelta.IsNewInUnicode16(0x143FA), Is.True);
        Assert.That(UnicodeVersionDelta.IsNewInUnicode16(0x143FB), Is.False);

        Assert.That(UnicodeVersionDelta.IsNewInUnicode16(0x1FBEF), Is.True);
        Assert.That(UnicodeVersionDelta.IsNewInUnicode16(0x1FBF0), Is.False);

        Assert.That(UnicodeVersionDelta.IsNewInUnicode16(0x0041), Is.False);
        Assert.That(UnicodeVersionDelta.IsNewInUnicode16(0x10FFFF), Is.False);
    }
}
