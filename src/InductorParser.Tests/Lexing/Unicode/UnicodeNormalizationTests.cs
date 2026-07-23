using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using InductorParser.Lexing;
using InductorParser.Lexing.Unicode;
using NUnit.Framework;

namespace InductorParser.Tests;

// Tests for the built-in UAX #15 normalizer (UnicodeNormalization), in
// two layers, mirroring GraphemeSegmentationTests. The first layer
// asserts expected normalization shapes directly, driving the
// NormalizationExamples table plus named edge cases (composition
// exclusions, composite shifts, Hangul arithmetic, the same-instance
// return). The second layer is differential: the built-in normalizer
// deliberately matches .NET 8's string.Normalize and
// string.IsNormalized, so the runtime is an oracle this test project
// can compare against on every string it can build, and every
// differential case checks both entry points (see
// AssertNormalizesLikeRuntime for why Normalize alone isn't enough).
// string.Normalize hands the work to ICU (the Unicode library
// the OS normally supplies), so the test project ships its own ICU
// 72.1 (same Unicode 15.0 data as the built-in tables) and
// GlobalizationOracleFixture refuses to run the suite on anything
// else, keeping the oracle from drifting with the host. These tests
// run on CoreCLR only (Lexing/ doesn't sync to Unity), which is
// exactly where the oracle is valid.
//
// Non-ASCII text in this file comes from the canary-protected
// UnicodeExamples corpus, the NormalizationExamples table, or is built
// at runtime from code point values (FromCodePoints), never written as
// raw literals, so an editor renormalizing the file can't silently
// change what the tests exercise.
[TestFixture]
public class UnicodeNormalizationTests
{
    private static string FromCodePoints(params int[] codePoints)
    {
        var builder = new StringBuilder(codePoints.Length);
        foreach (int codePoint in codePoints)
            builder.Append(char.ConvertFromUtf32(codePoint));
        return builder.ToString();
    }

    private static string DumpCodeUnits(string text) =>
        string.Join(" ", text.Select(codeUnit => $"U+{(int)codeUnit:X4}"));

    // Compare the built-in implementation against the runtime oracle on
    // one input: Normalize output, IsNormalized verdict, and the throw
    // behavior of both. The two entry points are compared together
    // because IsNormalized has a failure mode Normalize can't reveal: a
    // quick-check flag wrongly marked No makes the built-in IsNormalized
    // report false for normalized text, while Normalize takes the full
    // rebuild and still produces identical output, so a Normalize-only
    // comparison would pass. Checking both here puts every differential
    // case in this file on both entry points.
    private static void AssertNormalizesLikeRuntime(string text, NormalizationForm form, string label)
    {
        string? bundledResult = null;
        string? runtimeResult = null;
        ArgumentException? bundledError = null;
        ArgumentException? runtimeError = null;
        try { bundledResult = UnicodeNormalization.NormalizeWithBundledImplementation(text, form); }
        catch (ArgumentException error) { bundledError = error; }
        try { runtimeResult = UnicodeNormalization.NormalizeWithRuntime(text, form); }
        catch (ArgumentException error) { runtimeError = error; }

        if (runtimeError != null || bundledError != null)
        {
            Assert.That(bundledError, Is.Not.Null,
                $"{label} ({form}): runtime threw ArgumentException, built-in returned "
                + $"{(bundledResult == null ? "<null>" : DumpCodeUnits(bundledResult))} "
                + $"for {DumpCodeUnits(text)}");
            Assert.That(runtimeError, Is.Not.Null,
                $"{label} ({form}): built-in threw ArgumentException, runtime returned "
                + $"{(runtimeResult == null ? "<null>" : DumpCodeUnits(runtimeResult))} "
                + $"for {DumpCodeUnits(text)}");
        }
        else if (!string.Equals(bundledResult, runtimeResult, StringComparison.Ordinal))
        {
            Assert.Fail(
                $"{label} ({form}): built-in {DumpCodeUnits(bundledResult!)}, "
                + $"runtime {DumpCodeUnits(runtimeResult!)} for {DumpCodeUnits(text)}");
        }

        bool? bundledVerdict = null;
        bool? runtimeVerdict = null;
        ArgumentException? bundledVerdictError = null;
        ArgumentException? runtimeVerdictError = null;
        try { bundledVerdict = UnicodeNormalization.IsNormalizedWithBundledImplementation(text, form); }
        catch (ArgumentException error) { bundledVerdictError = error; }
        try { runtimeVerdict = UnicodeNormalization.IsNormalizedWithRuntime(text, form); }
        catch (ArgumentException error) { runtimeVerdictError = error; }

        if (runtimeVerdictError != null || bundledVerdictError != null)
        {
            Assert.That(bundledVerdictError, Is.Not.Null,
                $"{label} ({form}): runtime IsNormalized threw ArgumentException, built-in "
                + $"returned {(bundledVerdict == null ? "<null>" : bundledVerdict.ToString())} "
                + $"for {DumpCodeUnits(text)}");
            Assert.That(runtimeVerdictError, Is.Not.Null,
                $"{label} ({form}): built-in IsNormalized threw ArgumentException, runtime "
                + $"returned {(runtimeVerdict == null ? "<null>" : runtimeVerdict.ToString())} "
                + $"for {DumpCodeUnits(text)}");
            return;
        }

        if (bundledVerdict != runtimeVerdict)
        {
            Assert.Fail(
                $"{label} ({form}): built-in IsNormalized {bundledVerdict}, "
                + $"runtime IsNormalized {runtimeVerdict} for {DumpCodeUnits(text)}");
        }
    }

    // ----- Layer one: expected shapes -----

    // Every row of the NormalizationExamples table, against the built-in
    // implementation directly. The lone-surrogate row throws instead of
    // projecting, matching .NET.
    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void Table_row_matches_bundled_implementation(
        NormalizationExamples.NormalizationCase row, NormalizationForm form)
    {
        if (row.Category == NormalizationExamples.NormalizationCategory.LoneSurrogateNotNormalizable)
        {
            Assert.Throws<ArgumentException>(
                () => UnicodeNormalization.NormalizeWithBundledImplementation(row.Source, form));
            return;
        }
        string expected = NormalizationExamples.Project(row, form);
        string actual = UnicodeNormalization.NormalizeWithBundledImplementation(row.Source, form);
        Assert.That(actual, Is.EqualTo(expected),
            $"{row.Description}: expected {NormalizationExamples.Hex(expected)}, "
            + $"got {NormalizationExamples.Hex(actual)}");
    }

    [Test]
    public void Already_normalized_input_comes_back_as_the_same_instance()
    {
        // NormalizedPositionMap's ReferenceEquals fast path depends on
        // this, and the runtime implementation behaves the same way.
        // Both cases land on the quick-check scan's Yes path: ASCII
        // through its per-char shortcut, the precomposed é through the
        // flag table.
        string ascii = "plain ascii";
        string precomposed = UnicodeExamples.LatinEAcutePrecomposedGrapheme;
        foreach (NormalizationForm form in NormalizationExamples.AllForms)
        {
            Assert.That(UnicodeNormalization.NormalizeWithBundledImplementation(ascii, form),
                Is.SameAs(ascii), $"ASCII under {form}");
        }
        Assert.That(
            UnicodeNormalization.NormalizeWithBundledImplementation(
                precomposed, NormalizationForm.FormC),
            Is.SameAs(precomposed), "precomposed e-acute under FormC");
        Assert.That(
            UnicodeNormalization.IsNormalizedWithBundledImplementation(
                precomposed, NormalizationForm.FormC),
            Is.True);
        Assert.That(
            UnicodeNormalization.IsNormalizedWithBundledImplementation(
                precomposed, NormalizationForm.FormD),
            Is.False);
    }

    [Test]
    public void Quick_check_Maybe_input_still_comes_back_as_the_same_instance()
    {
        // q followed by a combining acute is already NFC (no q-acute
        // exists to compose), but U+0301 is quick-check Maybe, so the
        // scan can't confirm it and the full rebuild runs. The rebuild
        // proves the content identical and must still return the
        // original instance.
        string input = "q" + FromCodePoints(0x0301);
        Assert.That(
            UnicodeNormalization.NormalizeWithBundledImplementation(
                input, NormalizationForm.FormC),
            Is.SameAs(input));
        Assert.That(
            UnicodeNormalization.IsNormalizedWithBundledImplementation(
                input, NormalizationForm.FormC),
            Is.True);
    }

    [Test]
    public void Empty_input_is_identity_under_every_form()
    {
        foreach (NormalizationForm form in NormalizationExamples.AllForms)
        {
            Assert.That(UnicodeNormalization.NormalizeWithBundledImplementation("", form),
                Is.SameAs(""));
        }
    }

    [Test]
    public void Composition_exclusion_stays_decomposed_under_FormC()
    {
        // U+0958 DEVANAGARI LETTER QA is script-specific composition
        // excluded: NFC of the precomposed character is the decomposed
        // pair, and NFC of the pair doesn't compose back.
        string precomposed = FromCodePoints(0x0958);
        string decomposed = FromCodePoints(0x0915, 0x093C);
        Assert.That(
            UnicodeNormalization.NormalizeWithBundledImplementation(
                precomposed, NormalizationForm.FormC),
            Is.EqualTo(decomposed));
        Assert.That(
            UnicodeNormalization.NormalizeWithBundledImplementation(
                decomposed, NormalizationForm.FormC),
            Is.EqualTo(decomposed));
    }

    [Test]
    public void Singleton_decomposition_never_recomposes()
    {
        // U+212B ANGSTROM SIGN canonically decomposes to U+00C5, a
        // singleton mapping. NFC converts to U+00C5 and stays there:
        // singletons are excluded from composition by the pair-table
        // shape itself (only two-scalar mappings form pairs).
        string angstromSign = FromCodePoints(0x212B);
        string letterARing = FromCodePoints(0x00C5);
        Assert.That(
            UnicodeNormalization.NormalizeWithBundledImplementation(
                angstromSign, NormalizationForm.FormC),
            Is.EqualTo(letterARing));
        Assert.That(
            UnicodeNormalization.NormalizeWithBundledImplementation(
                angstromSign, NormalizationForm.FormD),
            Is.EqualTo(FromCodePoints(0x0041, 0x030A)));
    }

    [Test]
    public void Composite_shift_reorders_and_recomposes_across_a_lower_class_mark()
    {
        // U+1E0A (D with dot above) followed by U+0323 (dot below,
        // class 220): NFD decomposes to D + dot above (class 230) + dot
        // below, and canonical ordering moves the class-220 dot below in
        // front of the class-230 dot above. NFC then composes D with the
        // now-adjacent dot below into U+1E0C, and the dot above stays as
        // a combining mark. The classic composite shift: which
        // precomposed letter survives changes.
        string input = FromCodePoints(0x1E0A, 0x0323);
        Assert.That(
            UnicodeNormalization.NormalizeWithBundledImplementation(
                input, NormalizationForm.FormD),
            Is.EqualTo(FromCodePoints(0x0044, 0x0323, 0x0307)));
        Assert.That(
            UnicodeNormalization.NormalizeWithBundledImplementation(
                input, NormalizationForm.FormC),
            Is.EqualTo(FromCodePoints(0x1E0C, 0x0307)));
    }

    [Test]
    public void Nonstarter_decomposition_never_comes_back()
    {
        // U+0344 COMBINING GREEK DIALYTIKA TONOS decomposes to U+0308 +
        // U+0301 and is composition excluded (a non-starter
        // decomposition), so U+0344 itself never survives any form. Its
        // pieces still participate in composition with the preceding
        // base: under the composing forms the a and the expanded U+0308
        // make U+00E4, and the U+0301 stays as a combining mark.
        string input = "a" + FromCodePoints(0x0344);
        string decomposed = "a" + FromCodePoints(0x0308, 0x0301);
        string composed = FromCodePoints(0x00E4, 0x0301);
        Assert.That(
            UnicodeNormalization.NormalizeWithBundledImplementation(
                input, NormalizationForm.FormD),
            Is.EqualTo(decomposed));
        Assert.That(
            UnicodeNormalization.NormalizeWithBundledImplementation(
                input, NormalizationForm.FormKD),
            Is.EqualTo(decomposed));
        Assert.That(
            UnicodeNormalization.NormalizeWithBundledImplementation(
                input, NormalizationForm.FormC),
            Is.EqualTo(composed));
        Assert.That(
            UnicodeNormalization.NormalizeWithBundledImplementation(
                input, NormalizationForm.FormKC),
            Is.EqualTo(composed));
    }

    [Test]
    public void Long_s_maps_to_plain_s_only_under_the_K_forms()
    {
        // U+1E9B (long s with dot above) canonically decomposes to
        // U+017F + U+0307, and U+017F has a compatibility mapping to
        // plain s. The K forms must re-check canonical pieces for
        // compatibility mappings of their own.
        string input = FromCodePoints(0x1E9B);
        Assert.That(
            UnicodeNormalization.NormalizeWithBundledImplementation(
                input, NormalizationForm.FormD),
            Is.EqualTo(FromCodePoints(0x017F, 0x0307)));
        Assert.That(
            UnicodeNormalization.NormalizeWithBundledImplementation(
                input, NormalizationForm.FormKD),
            Is.EqualTo(FromCodePoints(0x0073, 0x0307)));
        Assert.That(
            UnicodeNormalization.NormalizeWithBundledImplementation(
                input, NormalizationForm.FormKC),
            Is.EqualTo(FromCodePoints(0x1E61)));
    }

    [Test]
    public void Hangul_syllable_round_trips_through_jamo()
    {
        // U+D4DB is the syllable the UAX #15 spec itself uses to
        // illustrate the arithmetic: it decomposes to three jamo and
        // composes back through the LV intermediate.
        string syllable = FromCodePoints(0xD4DB);
        string jamo = FromCodePoints(0x1111, 0x1171, 0x11B6);
        Assert.That(
            UnicodeNormalization.NormalizeWithBundledImplementation(
                syllable, NormalizationForm.FormD),
            Is.EqualTo(jamo));
        Assert.That(
            UnicodeNormalization.NormalizeWithBundledImplementation(
                jamo, NormalizationForm.FormC),
            Is.EqualTo(syllable));
    }

    [Test]
    public void Undefined_normalization_form_values_throw()
    {
        // .NET's string.Normalize rejects values outside the four
        // defined forms. The dispatcher validates before either
        // implementation runs, so the built-in pipeline can't quietly
        // treat an unknown value as FormD. The defined values are 1, 2,
        // 5, and 6, so 0, 3, 4, and 7 sit inside and around them.
        foreach (int formValue in new[] { 0, 3, 4, 7, -1, 999 })
        {
            Assert.Throws<ArgumentException>(
                () => UnicodeNormalization.Normalize("abc", (NormalizationForm)formValue),
                $"Normalize with form {formValue}");
            Assert.Throws<ArgumentException>(
                () => UnicodeNormalization.IsNormalized("abc", (NormalizationForm)formValue),
                $"IsNormalized with form {formValue}");
        }
    }

    [Test]
    public void Mark_stretches_around_the_sort_threshold_normalize_like_runtime()
    {
        // Stretch lengths straddling the canonical-ordering sort's
        // short-stretch limit exercise the exchange sort, the counting
        // sort, and the switch between them, differentially against the
        // runtime oracle. Alternating class-230 acutes and class-220
        // dots below force a reorder at every pair.
        string acute = FromCodePoints(0x0301);
        string dotBelow = FromCodePoints(0x0323);
        foreach (int markCount in new[] { 31, 32, 33, 64, 1000 })
        {
            var builder = new StringBuilder("a");
            for (int mark = 0; mark < markCount; mark++)
                builder.Append(mark % 2 == 0 ? acute : dotBelow);
            string text = builder.ToString();
            foreach (NormalizationForm form in NormalizationExamples.AllForms)
                AssertNormalizesLikeRuntime(text, form, $"{markCount} alternating marks");
        }
    }

    [Test]
    public void Long_mark_stretch_sorts_stably_within_equal_classes()
    {
        // A stretch far past the short-stretch limit, built from two
        // class-230 marks and two class-220 marks in a repeating
        // pattern. Canonical order under FormD is every class-220 mark
        // first, then every class-230 mark, each group keeping its
        // original left-to-right order. The distinct code points within
        // each class are what make a stability bug visible. The runtime
        // oracle is left out on purpose: a stretch this long is the
        // counting sort's own path, and the expected string is spelled
        // out so a failure names the exact reordering that went wrong.
        const int blockCount = 4000;
        var codePoints = new List<int> { 'a' };
        for (int block = 0; block < blockCount; block++)
        {
            codePoints.Add(0x0301);  // combining acute, class 230
            codePoints.Add(0x0323);  // combining dot below, class 220
            codePoints.Add(0x0300);  // combining grave, class 230
            codePoints.Add(0x0316);  // combining grave below, class 220
        }

        var expected = new List<int> { 'a' };
        for (int block = 0; block < blockCount; block++)
        {
            expected.Add(0x0323);
            expected.Add(0x0316);
        }
        for (int block = 0; block < blockCount; block++)
        {
            expected.Add(0x0301);
            expected.Add(0x0300);
        }

        string actual = UnicodeNormalization.NormalizeWithBundledImplementation(
            FromCodePoints(codePoints.ToArray()), NormalizationForm.FormD);
        Assert.That(actual, Is.EqualTo(FromCodePoints(expected.ToArray())));
    }

    [Test]
    public void Ill_formed_text_throws_from_both_implementations()
    {
        // Lone surrogate halves and a reversed pair. The built-in
        // implementation matches .NET's throw behavior so the backstop
        // catch in Rule.ParseRecursive means the same thing under
        // both.
        string[] illFormed =
        {
            UnicodeExamples.HighSurrogateMinText,
            UnicodeExamples.LowSurrogateMaxText,
            UnicodeExamples.ReversedSurrogatePairText,
            "a" + UnicodeExamples.HighSurrogateMinText + "b",
            // The precomposed é has NfdQuickCheckNo set. A definite No
            // before invalid UTF-16 mustn't hide the later exception.
            UnicodeExamples.LatinEAcutePrecomposedGrapheme
                + UnicodeExamples.HighSurrogateMinText,
        };
        foreach (string text in illFormed)
        {
            foreach (NormalizationForm form in NormalizationExamples.AllForms)
            {
                Assert.Throws<ArgumentException>(
                    () => UnicodeNormalization.NormalizeWithBundledImplementation(text, form),
                    $"built-in accepted {DumpCodeUnits(text)} under {form}");
                Assert.Throws<ArgumentException>(
                    () => UnicodeNormalization.NormalizeWithRuntime(text, form),
                    $"runtime accepted {DumpCodeUnits(text)} under {form}");
                Assert.Throws<ArgumentException>(
                    () => UnicodeNormalization.IsNormalizedWithBundledImplementation(text, form),
                    $"built-in IsNormalized accepted {DumpCodeUnits(text)} under {form}");
                Assert.Throws<ArgumentException>(
                    () => UnicodeNormalization.IsNormalizedWithRuntime(text, form),
                    $"runtime IsNormalized accepted {DumpCodeUnits(text)} under {form}");
            }
        }
    }

    [Test]
    public void Noncharacter_FFFE_throws_from_both_as_a_deliberate_runtime_quirk()
    {
        // U+FFFE is a noncharacter but still well-formed Unicode: a
        // valid scalar value that pure UAX #15 normalizes to itself
        // (Corrigendum #9 says noncharacters don't make text
        // ill-formed). .NET's string.Normalize rejects exactly it and
        // no other noncharacter, and the built-in implementation
        // mirrors that quirk deliberately so behavior is identical on
        // every runtime. This test records a compatibility choice, not
        // a claim that U+FFFE is ill-formed.
        foreach (NormalizationForm form in NormalizationExamples.AllForms)
        {
            Assert.Throws<ArgumentException>(
                () => UnicodeNormalization.NormalizeWithBundledImplementation(
                    UnicodeExamples.NoncharacterFFFEText, form),
                $"built-in accepted U+FFFE under {form}");
            Assert.Throws<ArgumentException>(
                () => UnicodeNormalization.NormalizeWithRuntime(
                    UnicodeExamples.NoncharacterFFFEText, form),
                $"runtime accepted U+FFFE under {form}");
        }
    }

    // ----- Layer two: differential against the runtime oracle -----

    // The whole UnicodeExamples corpus, each constant checked against
    // the runtime oracle under all four forms.
    // UnicodeExamples.AllStringConstants does the reflection, so a
    // constant added to the corpus automatically becomes a differential
    // case here.
    private static IEnumerable<TestCaseData> UnicodeStringCatalog() =>
        UnicodeExamples.AllStringConstants()
            .Select(constant => new TestCaseData(
                constant.Name, constant.Value.Select(codeUnit => (int)codeUnit).ToArray()));

    [TestCaseSource(nameof(UnicodeStringCatalog))]
    public void Corpus_constant_normalizes_like_runtime(string label, int[] codeUnits)
    {
        string text = BuildString(codeUnits);
        foreach (NormalizationForm form in NormalizationExamples.AllForms)
            AssertNormalizesLikeRuntime(text, form, label);
    }

    [Test]
    public void Corpus_pairwise_concatenations_normalize_like_runtime()
    {
        // Every ordered pair of corpus constants, concatenated. The
        // seams are where normalization interactions happen (a combining
        // mark constant after a precomposed letter, jamo after a
        // syllable), so the pairs cover compositions no single constant
        // holds.
        var constants = UnicodeExamples.AllStringConstants().ToArray();
        foreach (var (firstName, firstValue) in constants)
        {
            foreach (var (secondName, secondValue) in constants)
            {
                string text = firstValue + secondValue;
                foreach (NormalizationForm form in NormalizationExamples.AllForms)
                    AssertNormalizesLikeRuntime(text, form, $"{firstName} + {secondName}");
            }
        }
    }

    [Test]
    public void Every_scalar_normalizes_like_runtime_under_the_canonical_forms()
    {
        // Sweep the entire code point space under FormC and FormD (the
        // forms the parser defaults to and the position-mapping lockstep
        // walker assumes). Surrogate code units ride along as
        // lone-surrogate strings and must throw from both
        // implementations. The full four-form sweep with trailing
        // combining marks is the [Explicit] test below.
        SweepEveryScalar(
            new[] { NormalizationForm.FormC, NormalizationForm.FormD },
            withTrailingMarks: false);
    }

    [Test]
    public void Quick_check_Yes_path_does_not_allocate_for_nonAscii_input()
    {
        string input = string.Concat(Enumerable.Repeat(
            UnicodeExamples.LatinEAcutePrecomposedGrapheme, 1000));
        UnicodeNormalization.NormalizeWithBundledImplementation(
            input, NormalizationForm.FormC);

        bool allSame = true;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 100; iteration++)
            allSame &= ReferenceEquals(
                UnicodeNormalization.NormalizeWithBundledImplementation(
                    input, NormalizationForm.FormC),
                input);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(allSame, Is.True);
        Assert.That(allocated, Is.LessThan(1024));
    }

    [Test, Explicit("Full four-form sweep of every code point, alone and with trailing combining marks. Run on demand when changing the normalizer or its tables."), Category("DeepCampaign")]
    public void Every_scalar_normalizes_like_runtime_under_all_forms_test()
    {
        SweepEveryScalar(NormalizationExamples.AllForms, withTrailingMarks: true);
    }

    private static void SweepEveryScalar(NormalizationForm[] forms, bool withTrailingMarks)
    {
        // U+0301 (class 230) exercises composition against every base,
        // U+0323 (class 220) exercises canonical ordering against it.
        string combiningAcute = UnicodeExamples.CombiningAcuteText;
        string combiningDotBelow = FromCodePoints(0x0323);
        for (int codePoint = 0; codePoint <= 0x10FFFF; codePoint++)
        {
            string alone = codePoint <= 0xFFFF
                ? ((char)codePoint).ToString()
                : char.ConvertFromUtf32(codePoint);
            foreach (NormalizationForm form in forms)
            {
                AssertNormalizesLikeRuntime(alone, form, $"U+{codePoint:X4}");
                if (withTrailingMarks)
                {
                    AssertNormalizesLikeRuntime(
                        alone + combiningAcute, form, $"U+{codePoint:X4} + U+0301");
                    AssertNormalizesLikeRuntime(
                        alone + combiningDotBelow + combiningAcute, form,
                        $"U+{codePoint:X4} + U+0323 + U+0301");
                }
            }
        }
    }

    // Random sequences compared against the runtime oracle. The
    // alphabet is every UnicodeExamples corpus constant (via
    // AllStringConstants, so a constant added to the corpus flows in
    // automatically) plus normalization-relevant pieces the corpus has
    // no standalone constant for: jamo, marks of distinct combining
    // classes, composition-excluded characters, and compatibility
    // sources. Any failure prints the code units, which reproduce it
    // exactly.
    [Test, Explicit("Randomized differential sweep against the runtime's string.Normalize. Run on demand when changing the normalizer or its tables."), Category("DeepCampaign")]
    public void Random_sequences_normalize_like_runtime_test()
    {
        string[] extraPieces =
        {
            "a", "1", " ",
            FromCodePoints(0x0301),   // combining acute, class 230
            FromCodePoints(0x0323),   // combining dot below, class 220
            FromCodePoints(0x0316),   // combining grave below, class 220
            FromCodePoints(0x05B0),   // Hebrew sheva, class 10
            FromCodePoints(0x0F71),   // Tibetan vowel aa, class 129
            FromCodePoints(0x1100),   // Hangul leading jamo kiyeok
            FromCodePoints(0x1161),   // Hangul vowel jamo a
            FromCodePoints(0x11A8),   // Hangul trailing jamo kiyeok
            FromCodePoints(0xAC00),   // Hangul syllable ga (LV)
            FromCodePoints(0xAC01),   // Hangul syllable gag (LVT)
            FromCodePoints(0x0958),   // composition-excluded qa
            FromCodePoints(0x0915, 0x093C),  // its decomposed pair
            FromCodePoints(0x212B),   // Angstrom sign, singleton
            FromCodePoints(0x1E0A),   // D with dot above, composite shift
            FromCodePoints(0x0344),   // non-starter decomposition
            FromCodePoints(0xFF76),   // halfwidth katakana ka
            FromCodePoints(0x3070),   // hiragana ba (composed voiced)
            FromCodePoints(0x304F, 0x3099),  // hiragana gu, decomposed voiced
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

                foreach (NormalizationForm form in NormalizationExamples.AllForms)
                    AssertNormalizesLikeRuntime(text, form, $"seed {seed}, iteration {iteration}");
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
