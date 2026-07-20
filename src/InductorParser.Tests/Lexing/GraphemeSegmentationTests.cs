using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using InductorParser.Lexing;
using NUnit.Framework;

namespace InductorParser.Tests;

// Tests for the bundled UAX #29 segmenter (GraphemeSegmentation), in two
// layers. The first layer asserts expected cluster shapes directly: CRLF,
// Hangul jamo, ZWJ sequences, regional-indicator flags, keycaps, Thai
// SARA AM, prepends, controls, and lone surrogates. The second layer is
// differential: the segmenter deliberately matches .NET 8's StringInfo
// (same algorithm, same Unicode 15.0 data), so StringInfo is an oracle
// this test project can compare against on every string it can build.
// These tests run on CoreCLR only (Lexing/ doesn't sync to Unity), which
// is exactly where the oracle is valid.
//
// Non-ASCII text in this file comes from the canary-protected
// UnicodeExamples corpus or is built at runtime from code point values
// (FromCodePoints), never written as raw literals, so an editor
// renormalizing the file can't silently change what the tests exercise.
//
// Corpus entries are passed as int[] UTF-16 code units and rebuilt into
// a string inside the test, because NUnit puts test-case arguments into
// the generated test name and a lone surrogate there is invalid XML for
// the result file. The constant's field name is the readable label.
[TestFixture]
public class GraphemeSegmentationTests
{
    // Cluster lengths of the whole string, walked with the bundled
    // segmenter directly (not the dispatching entry point), so these
    // tests exercise the ported state machine no matter which way the
    // process-wide segmenter setting resolves.
    private static List<int> VendoredClusterLengths(string text)
    {
        var lengths = new List<int>();
        int position = 0;
        while (position < text.Length)
        {
            int length = GraphemeSegmentation
                .GetBundledLengthOfFirstExtendedGraphemeCluster(text.AsSpan(position));
            Assert.That(length, Is.GreaterThan(0),
                $"segmenter returned {length} at position {position} of {DumpCodeUnits(text)}");
            lengths.Add(length);
            position += length;
        }
        return lengths;
    }

    // Cluster lengths of the whole string, walked with the runtime's
    // StringInfo (the .NET 8 oracle the vendored segmenter must match).
    private static List<int> RuntimeClusterLengths(string text)
    {
        var lengths = new List<int>();
        int position = 0;
        while (position < text.Length)
        {
            int length = StringInfo.GetNextTextElement(text, position).Length;
            lengths.Add(length);
            position += length;
        }
        return lengths;
    }

    private static string DumpCodeUnits(string text) =>
        string.Join(" ", text.Select(codeUnit => $"U+{(int)codeUnit:X4}"));

    // Build a string from Unicode scalar values. Used instead of raw
    // non-ASCII literals so the file's contents can't be silently
    // changed by an editor renormalizing the source (same reason
    // EndOfLineRuleTests builds CR / LF / CRLF at runtime).
    private static string FromCodePoints(params int[] codePoints)
    {
        var builder = new StringBuilder(codePoints.Length);
        foreach (int codePoint in codePoints)
            builder.Append(char.ConvertFromUtf32(codePoint));
        return builder.ToString();
    }

    private static void AssertSegmentsLikeRuntime(string text, string label)
    {
        Assert.That(VendoredClusterLengths(text), Is.EqualTo(RuntimeClusterLengths(text)),
            $"{label}: {DumpCodeUnits(text)}");
    }

    [Test]
    public void Empty_input_returns_zero()
    {
        Assert.That(GraphemeSegmentation.GetBundledLengthOfFirstExtendedGraphemeCluster(
            ReadOnlySpan<char>.Empty), Is.EqualTo(0));
        Assert.That(GraphemeSegmentation.GetRuntimeLengthOfFirstExtendedGraphemeCluster(
            ReadOnlySpan<char>.Empty), Is.EqualTo(0));
    }

    [Test]
    public void Runtime_backed_length_answers_like_StringInfo()
    {
        // The StringInfo-backed path, called directly so it stays
        // working regardless of which segmenter the process-wide
        // setting picks.
        Assert.That(GraphemeSegmentation.GetRuntimeLengthOfFirstExtendedGraphemeCluster(
            "\r\nx".AsSpan()), Is.EqualTo(2));
        Assert.That(GraphemeSegmentation.GetRuntimeLengthOfFirstExtendedGraphemeCluster(
            (UnicodeExamples.USFlagGrapheme + "x").AsSpan()), Is.EqualTo(4));
    }

    [Test]
    public void Single_ascii_char_is_one_cluster()
    {
        Assert.That(VendoredClusterLengths("a"), Is.EqualTo(new[] { 1 }));
    }

    [Test]
    public void Crlf_is_one_two_char_cluster()
    {
        Assert.That(VendoredClusterLengths("\r\n"), Is.EqualTo(new[] { 2 }));
    }

    [Test]
    public void Lf_then_cr_breaks_after_the_lf()
    {
        // Only CR x LF glues (GB3). The reverse order is two clusters.
        Assert.That(VendoredClusterLengths("\n\r"), Is.EqualTo(new[] { 1, 1 }));
    }

    [Test]
    public void Control_characters_break_on_both_sides()
    {
        // GB4/GB5: a Control never joins its neighbors, so NUL between
        // letters is its own cluster, and a combining mark after a
        // control starts a new cluster instead of attaching.
        Assert.That(VendoredClusterLengths("a\0b"), Is.EqualTo(new[] { 1, 1, 1 }));
        Assert.That(VendoredClusterLengths(
            UnicodeExamples.NullText + UnicodeExamples.CombiningAcuteText),
            Is.EqualTo(new[] { 1, 1 }));
    }

    [Test]
    public void Hangul_jamo_compose_one_syllable_cluster()
    {
        // L + V + T (GB6, GB7, GB8): three jamo, one cluster.
        Assert.That(VendoredClusterLengths(UnicodeExamples.HangulHanDecomposedText),
            Is.EqualTo(new[] { 3 }));
        // Precomposed LV syllable GA (U+AC00) + trailing T jamo also
        // fuses (GB7).
        Assert.That(VendoredClusterLengths(FromCodePoints(0xAC00, 0x11A8)),
            Is.EqualTo(new[] { 2 }));
        // T followed by V breaks: GB8 only allows T x T.
        Assert.That(VendoredClusterLengths(FromCodePoints(0x11A8, 0x1161)),
            Is.EqualTo(new[] { 1, 1 }));
    }

    [Test]
    public void Decomposed_accent_fuses_with_its_base()
    {
        Assert.That(VendoredClusterLengths(UnicodeExamples.LatinEAcuteGrapheme),
            Is.EqualTo(new[] { 2 }));
    }

    [Test]
    public void Thai_kam_is_one_cluster()
    {
        // SARA AM is a SpacingMark, kept with the preceding consonant (GB9a).
        Assert.That(VendoredClusterLengths(UnicodeExamples.ThaiKamGrapheme),
            Is.EqualTo(new[] { 2 }));
    }

    [Test]
    public void Zwj_sequences_are_one_cluster()
    {
        // GB11: extended pictograph + ZWJ + extended pictograph chains.
        Assert.That(VendoredClusterLengths(UnicodeExamples.FamilyManWomanBoyGrapheme),
            Is.EqualTo(new[] { UnicodeExamples.FamilyManWomanBoyGrapheme.Length }));
        Assert.That(VendoredClusterLengths(UnicodeExamples.WomanShruggingGrapheme),
            Is.EqualTo(new[] { UnicodeExamples.WomanShruggingGrapheme.Length }));
        Assert.That(VendoredClusterLengths(UnicodeExamples.WomanOfficeWorkerGrapheme),
            Is.EqualTo(new[] { UnicodeExamples.WomanOfficeWorkerGrapheme.Length }));
    }

    [Test]
    public void Skin_toned_wave_is_one_cluster()
    {
        Assert.That(VendoredClusterLengths(UnicodeExamples.SkinTonedWaveGrapheme),
            Is.EqualTo(new[] { UnicodeExamples.SkinTonedWaveGrapheme.Length }));
    }

    [Test]
    public void Keycap_sequence_is_one_cluster()
    {
        Assert.That(VendoredClusterLengths(UnicodeExamples.DigitOneKeycapGrapheme),
            Is.EqualTo(new[] { UnicodeExamples.DigitOneKeycapGrapheme.Length }));
    }

    [Test]
    public void Regional_indicator_pair_is_one_cluster_and_a_quad_is_two()
    {
        string flag = UnicodeExamples.USFlagGrapheme;
        Assert.That(VendoredClusterLengths(flag), Is.EqualTo(new[] { 4 }));
        // GB12/GB13 pair RIs left to right, so four RIs are two flags,
        // never a three-plus-one split.
        Assert.That(VendoredClusterLengths(flag + flag), Is.EqualTo(new[] { 4, 4 }));
    }

    [Test]
    public void Prepend_glues_to_the_following_character()
    {
        string arabicNumberSign = UnicodeExamples.ArabicNumberSignText;
        // U+0600 ARABIC NUMBER SIGN is Prepend (GB9b): it joins the
        // character after it, and a second prepend chains.
        Assert.That(VendoredClusterLengths(arabicNumberSign + "1"), Is.EqualTo(new[] { 2 }));
        Assert.That(VendoredClusterLengths(arabicNumberSign + arabicNumberSign + "1"),
            Is.EqualTo(new[] { 3 }));
        // But not before a control (GB5): the prepend stands alone.
        Assert.That(VendoredClusterLengths(arabicNumberSign + "\n"), Is.EqualTo(new[] { 1, 1 }));
    }

    [Test]
    public void Lone_surrogate_is_a_one_char_cluster()
    {
        Assert.That(VendoredClusterLengths(UnicodeExamples.HighSurrogateMinText),
            Is.EqualTo(new[] { 1 }));
        Assert.That(VendoredClusterLengths("a" + UnicodeExamples.LowSurrogateMinText + "b"),
            Is.EqualTo(new[] { 1, 1, 1 }));
    }

    [Test]
    public void Lone_surrogate_fuses_with_a_following_combining_mark()
    {
        // The stray decodes as U+FFFD (break type Other), and GB9 keeps
        // a following Extend attached, so the ill-formed pair is one
        // two-char cluster. Same answer StringInfo gives.
        string strayThenMark =
            UnicodeExamples.HighSurrogateMinText + UnicodeExamples.CombiningAcuteText;
        Assert.That(VendoredClusterLengths(strayThenMark), Is.EqualTo(new[] { 2 }));
        AssertSegmentsLikeRuntime(strayThenMark, "stray high surrogate + combining acute");
    }

    // The whole UnicodeExamples corpus, each constant checked against the
    // StringInfo oracle. UnicodeExamples.AllStringConstants does the
    // reflection, so a constant added to the corpus automatically becomes
    // a differential case here.
    private static IEnumerable<TestCaseData> UnicodeStringCatalog() =>
        UnicodeExamples.AllStringConstants()
            .Select(constant => new TestCaseData(
                constant.Name, constant.Value.Select(codeUnit => (int)codeUnit).ToArray()));

    [TestCaseSource(nameof(UnicodeStringCatalog))]
    public void Corpus_constant_segments_like_runtime_StringInfo(string label, int[] codeUnits)
    {
        AssertSegmentsLikeRuntime(BuildString(codeUnits), label);
    }

    [Test]
    public void Corpus_pairwise_concatenations_segment_like_runtime_StringInfo()
    {
        // Every ordered pair of corpus constants, concatenated. The seams
        // are where segmentation rules fire (a combining mark constant
        // after an emoji constant, an RI after an RI, a ZWJ chain meeting
        // a control), so the pairs cover interactions no single constant
        // holds.
        var constants = UnicodeExamples.AllStringConstants().ToArray();
        foreach (var (firstName, firstValue) in constants)
        {
            foreach (var (secondName, secondValue) in constants)
            {
                AssertSegmentsLikeRuntime(firstValue + secondValue, $"{firstName} + {secondName}");
            }
        }
    }

    [Test]
    public void Every_scalar_alone_and_with_combining_acute_segments_like_runtime_StringInfo()
    {
        // Sweep the entire code point space (surrogate code units
        // included, as lone-surrogate strings). Each code point is
        // checked standing alone and followed by U+0301, which
        // behaviorally separates the break classes that refuse trailing
        // marks (CR, LF, Control) from everything else. The [Explicit]
        // UCD re-derivation test in GraphemeSegmentationDataTests checks
        // the full break-type table per code point directly.
        string combiningAcute = UnicodeExamples.CombiningAcuteText;
        for (int codePoint = 0; codePoint <= 0x10FFFF; codePoint++)
        {
            string alone = codePoint <= 0xFFFF
                ? ((char)codePoint).ToString()
                : char.ConvertFromUtf32(codePoint);
            AssertFirstClusterMatchesRuntime(alone, codePoint);
            AssertFirstClusterMatchesRuntime(alone + combiningAcute, codePoint);
        }
    }

    private static void AssertFirstClusterMatchesRuntime(string text, int codePoint)
    {
        int vendored = GraphemeSegmentation
            .GetBundledLengthOfFirstExtendedGraphemeCluster(text.AsSpan());
        int runtime = StringInfo.GetNextTextElement(text, 0).Length;
        if (vendored != runtime)
        {
            Assert.Fail(
                $"U+{codePoint:X4}: vendored first cluster {vendored}, StringInfo {runtime} "
                + $"on {DumpCodeUnits(text)}");
        }
    }

    // Random sequences compared boundary-for-boundary against
    // StringInfo. The alphabet is every UnicodeExamples corpus constant
    // (via AllStringConstants, so a constant added to the corpus flows
    // in automatically) plus the break-class pieces the corpus has no
    // standalone constant for. Any failure prints the seed and the code
    // units, which reproduce it exactly.
    [Test, Explicit("Randomized differential sweep against StringInfo. Run on demand when changing the segmenter or its table.")]
    public void Random_sequences_segment_like_runtime_StringInfo_test()
    {
        string[] extraPieces =
        {
            "a", "1", " ", "\r", "\n", "\r\n", "\0", "\t",
            FromCodePoints(0x0E33),   // Thai sara am (SpacingMark)
            FromCodePoints(0x1100),   // Hangul choseong kiyeok (L)
            FromCodePoints(0x1161),   // Hangul jungseong a (V)
            FromCodePoints(0x11A8),   // Hangul jongseong kiyeok (T)
            FromCodePoints(0xAC00),   // Hangul syllable ga (LV)
            FromCodePoints(0xAC01),   // Hangul syllable gag (LVT)
            FromCodePoints(0x1F1F8),  // regional indicator S, pairs with the corpus's RI U
        };
        string[] alphabet = UnicodeExamples.AllStringConstants()
            .Select(constant => constant.Value)
            .Concat(extraPieces)
            .ToArray();

        for (int seed = 0; seed < 200; seed++)
        {
            var random = new Random(seed);
            for (int iteration = 0; iteration < 500; iteration++)
            {
                var builder = new StringBuilder();
                int pieces = random.Next(1, 17);
                for (int piece = 0; piece < pieces; piece++)
                    builder.Append(alphabet[random.Next(alphabet.Length)]);
                string text = builder.ToString();

                Assert.That(VendoredClusterLengths(text), Is.EqualTo(RuntimeClusterLengths(text)),
                    $"seed {seed}, iteration {iteration}: {DumpCodeUnits(text)}");
            }
        }
    }

    private static string BuildString(int[] codeUnits)
    {
        var builder = new StringBuilder(codeUnits.Length);
        foreach (int codeUnit in codeUnits)
            builder.Append((char)codeUnit);
        return builder.ToString();
    }
}
