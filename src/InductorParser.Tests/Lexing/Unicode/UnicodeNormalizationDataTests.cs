using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using InductorParser.Lexing;
using InductorParser.Lexing.Unicode;
using NUnit.Framework;

namespace InductorParser.Tests;

// Verification and regeneration for the generated normalization tables
// in UnicodeNormalization.Data.cs, mirroring the machinery in
// GraphemeSegmentationDataTests. The default tests check the tables'
// structure (the binary searches silently return wrong answers if the
// keys ever come out of order). The [Explicit] verification test
// re-derives the tables from the pinned Unicode 15.0.0 UCD files and
// asserts the checked-in data matches. [Explicit] because it hits
// unicode.org, following the XidIdentifierTests precedent. The
// regeneration tool below it has its [Test] attribute commented out so
// no filter can run it by accident. The version is pinned (not
// "latest") so the tests stay stable across Unicode releases: these
// tables deliberately match the Unicode version of the bundled
// segmenter's table, and bumping it is a separate decision (see the
// header of UnicodeNormalization.Data.cs).
[TestFixture]
public class UnicodeNormalizationDataTests
{
    private const string UnicodeVersion = "15.0.0";

    // Everything the generator derives from the two UCD files, in raw
    // per-code-point form before table encoding.
    private sealed class DerivedNormalizationData
    {
        public byte[] CanonicalCombiningClasses = new byte[0x110000];
        public Dictionary<int, (int[] Expansion, bool IsCompatibility)> Decompositions = new();
        public SortedSet<int> FullCompositionExclusions = new();
    }

    // The derived composition pair count at Unicode 15.0.0, printed by
    // the emit run and asserted structurally below so a bad regeneration
    // (truncated download, botched merge) can't shrink the pair table
    // unnoticed.
    private const int ExpectedCompositionPairCount = 941;

    [Test]
    public void CombiningClass_table_starts_are_strictly_ascending_from_zero()
    {
        int[] starts = ReadPrivateIntArray("CanonicalCombiningClassRangeStarts");
        Assert.That(starts.Length, Is.GreaterThan(0));
        Assert.That(starts[0], Is.EqualTo(0), "the first transition must cover code point 0");
        for (int index = 1; index < starts.Length; index++)
        {
            Assert.That(starts[index], Is.GreaterThan(starts[index - 1]),
                $"CanonicalCombiningClassRangeStarts[{index}] must be greater than [{index - 1}]");
        }
        Assert.That(starts[^1], Is.LessThanOrEqualTo(0x10FFFF));
    }

    [Test]
    public void CombiningClass_values_stay_in_the_defined_range()
    {
        // 240 (iota subscript) is the highest canonical combining class
        // Unicode defines. Probe each transition's first code point plus
        // the code point just before it, which together touch every
        // entry in the values array.
        int[] starts = ReadPrivateIntArray("CanonicalCombiningClassRangeStarts");
        foreach (int start in starts)
        {
            Assert.That(UnicodeNormalization.GetCanonicalCombiningClass(start),
                Is.LessThanOrEqualTo(240), $"class at U+{start:X4}");
            if (start > 0)
            {
                Assert.That(UnicodeNormalization.GetCanonicalCombiningClass(start - 1),
                    Is.LessThanOrEqualTo(240), $"class at U+{start - 1:X4}");
            }
        }
    }

    [Test]
    public void Decomposition_keys_are_strictly_ascending_with_no_surrogate_or_hangul_keys()
    {
        int[] keys = ReadPrivateIntArray("DecompositionCodePoints");
        Assert.That(keys.Length, Is.GreaterThan(0));
        for (int index = 0; index < keys.Length; index++)
        {
            if (index > 0)
            {
                Assert.That(keys[index], Is.GreaterThan(keys[index - 1]),
                    $"DecompositionCodePoints[{index}] must be greater than [{index - 1}]");
            }
            Assert.That(keys[index] is >= 0xD800 and <= 0xDFFF, Is.False,
                $"surrogate key U+{keys[index]:X4}");
            Assert.That(keys[index] is >= 0xAC00 and <= 0xD7A3, Is.False,
                $"Hangul syllable key U+{keys[index]:X4} (Hangul is arithmetic, not table-driven)");
        }
    }

    [Test]
    public void Every_packed_decomposition_entry_decodes_in_bounds()
    {
        int[] keys = ReadPrivateIntArray("DecompositionCodePoints");
        int[] entries = ReadPrivateIntArray("DecompositionEntries");
        int[] expansions = ReadPrivateIntArray("DecompositionExpansions");
        Assert.That(entries.Length, Is.EqualTo(keys.Length),
            "there must be exactly one packed entry per decomposition key");
        foreach (int key in keys)
        {
            Assert.That(UnicodeNormalization.TryGetDecomposition(
                key, out ReadOnlySpan<int> expansion, out bool isCompatibility), Is.True,
                $"U+{key:X4} is a table key but TryGetDecomposition missed it");
            if (isCompatibility)
            {
                Assert.That(expansion.Length, Is.InRange(1, 18),
                    $"U+{key:X4}: compatibility expansions top out at 18 (U+FDFA)");
            }
            else
            {
                Assert.That(expansion.Length, Is.InRange(1, 2),
                    $"U+{key:X4}: canonical mappings are one or two scalars by definition");
            }
            foreach (int piece in expansion)
            {
                Assert.That(piece, Is.InRange(0, 0x10FFFF), $"U+{key:X4} expansion out of range");
                Assert.That(piece is >= 0xD800 and <= 0xDFFF, Is.False,
                    $"U+{key:X4} expansion contains a surrogate");
            }
        }
        // The packed offsets must tile the expansion buffer exactly:
        // no gaps, no overlaps, nothing dangling at the end.
        int expectedOffset = 0;
        for (int index = 0; index < keys.Length; index++)
        {
            int packed = entries[index];
            int packedOffset = packed >> 6;
            int packedLength = (packed >> 1) & 0x1F;
            Assert.That(packedOffset, Is.EqualTo(expectedOffset),
                $"U+{keys[index]:X4}: packed expansion offset must immediately follow "
                + "the preceding entry");

            UnicodeNormalization.TryGetDecomposition(
                keys[index], out ReadOnlySpan<int> expansion, out _);
            Assert.That(packedLength, Is.EqualTo(expansion.Length),
                $"U+{keys[index]:X4}: packed length disagrees with the decoded span");
            expectedOffset += packedLength;
        }
        Assert.That(expectedOffset, Is.EqualTo(expansions.Length),
            "expansion buffer length must equal the sum of all expansion lengths");
    }

    [Test]
    public void Full_composition_exclusions_are_sorted_and_all_have_canonical_decompositions()
    {
        int[] exclusions = ReadPrivateIntArray("FullCompositionExclusions");
        Assert.That(exclusions.Length, Is.GreaterThan(0));
        for (int index = 0; index < exclusions.Length; index++)
        {
            if (index > 0)
            {
                Assert.That(exclusions[index], Is.GreaterThan(exclusions[index - 1]),
                    $"FullCompositionExclusions[{index}] must be greater than [{index - 1}]");
            }
            Assert.That(UnicodeNormalization.TryGetDecomposition(
                exclusions[index], out _, out bool isCompatibility) && !isCompatibility, Is.True,
                $"U+{exclusions[index]:X4} is excluded from composition but has no canonical "
                + "decomposition, so the exclusion could never matter");
        }
    }

    [Test]
    public void Derived_composition_pair_count_matches_the_pinned_constant()
    {
        // Forces the lazy pair build (which runs the starter and
        // no-duplicate invariants) and checks the count against the
        // constant printed by the emit run.
        var field = typeof(UnicodeNormalization).GetField(
            "CompositionPairs", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(field, Is.Not.Null, "UnicodeNormalization.CompositionPairs field not found");
        var pairs = (Lazy<Dictionary<long, int>>)field!.GetValue(null)!;
        Assert.That(pairs.Value.Count, Is.EqualTo(ExpectedCompositionPairCount));
    }

    [Test]
    public void Checked_in_data_file_matches_what_the_emitter_produces()
    {
        // Round-trip drift check, no network: rebuild the generator's
        // input from the checked-in tables themselves, run the emitter,
        // and require the result to equal the checked-in file line for
        // line. This catches a hand-edit to the data arrays or the
        // header prose that the emitter wouldn't produce, and an
        // emitter change nobody copied into the checked-in file. The
        // [Explicit] UCD test below answers a different question (do
        // the tables match unicode.org), this one answers whether the
        // file matches the tool that claims to produce it.
        var derived = new DerivedNormalizationData();
        for (int codePoint = 0; codePoint <= 0x10FFFF; codePoint++)
        {
            derived.CanonicalCombiningClasses[codePoint] =
                UnicodeNormalization.GetCanonicalCombiningClass(codePoint);
        }
        foreach (int key in ReadPrivateIntArray("DecompositionCodePoints"))
        {
            UnicodeNormalization.TryGetDecomposition(
                key, out ReadOnlySpan<int> expansion, out bool isCompatibility);
            derived.Decompositions.Add(key, (expansion.ToArray(), isCompatibility));
        }
        foreach (int excluded in ReadPrivateIntArray("FullCompositionExclusions"))
            derived.FullCompositionExclusions.Add(excluded);

        AssertEmitterReproducesCheckedInFile(
            EmitDataFile(derived), "UnicodeNormalization.Data.cs");
    }

    // Compares the emitter's in-memory output against the checked-in
    // file, copied to the test output directory by the csproj. Line
    // splitting sidesteps line-ending differences between the emit
    // (Environment.NewLine) and however git checked the file out.
    private static void AssertEmitterReproducesCheckedInFile(string emitted, string fileName)
    {
        string path = Path.Combine(
            TestContext.CurrentContext.TestDirectory, "CheckedInData", fileName);
        Assert.That(File.Exists(path), Is.True,
            $"{fileName} was not copied to the test output directory (see the csproj)");

        string[] emittedLines = emitted
            .Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        string[] checkedInLines = File.ReadAllLines(path);

        int shared = Math.Min(emittedLines.Length, checkedInLines.Length);
        for (int index = 0; index < shared; index++)
        {
            if (emittedLines[index] != checkedInLines[index])
            {
                Assert.Fail(
                    $"{fileName} drifted from the emitter at line {index + 1}.\n"
                    + $"  checked in: {checkedInLines[index]}\n"
                    + $"  emitter:    {emittedLines[index]}\n"
                    + "Run Emit_regenerated_data_file_test and copy the result over the "
                    + "checked-in file, or update the emitter to match a deliberate edit.");
            }
        }
        Assert.That(emittedLines.Length, Is.EqualTo(checkedInLines.Length),
            $"{fileName}: line count differs from the emitter's output");
    }

    [Test, Explicit("Fetches UnicodeData.txt and DerivedNormalizationProps.txt from unicode.org. Run on demand when reviewing the generated tables or bumping the Unicode version.")]
    public async Task Table_matches_the_pinned_UCD_files_test()
    {
        DerivedNormalizationData expected = await DeriveFromUcdAsync();
        var disagreements = new List<string>();
        for (int codePoint = 0; codePoint <= 0x10FFFF && disagreements.Count < 25; codePoint++)
        {
            byte actualClass = UnicodeNormalization.GetCanonicalCombiningClass(codePoint);
            if (actualClass != expected.CanonicalCombiningClasses[codePoint])
            {
                disagreements.Add(
                    $"U+{codePoint:X4}: table class {actualClass}, "
                    + $"UCD class {expected.CanonicalCombiningClasses[codePoint]}");
            }

            bool actualHasMapping = UnicodeNormalization.TryGetDecomposition(
                codePoint, out ReadOnlySpan<int> actualExpansion, out bool actualCompatibility);
            bool expectedHasMapping = expected.Decompositions.TryGetValue(
                codePoint, out (int[] Expansion, bool IsCompatibility) expectedMapping);
            if (actualHasMapping != expectedHasMapping
                || (actualHasMapping
                    && (actualCompatibility != expectedMapping.IsCompatibility
                        || !actualExpansion.SequenceEqual(expectedMapping.Expansion))))
            {
                disagreements.Add($"U+{codePoint:X4}: decomposition mapping disagrees with the UCD");
            }

            if (UnicodeNormalization.IsFullCompositionExclusion(codePoint)
                != expected.FullCompositionExclusions.Contains(codePoint))
            {
                disagreements.Add($"U+{codePoint:X4}: Full_Composition_Exclusion disagrees with the UCD");
            }
        }
        Assert.That(disagreements, Is.Empty,
            "UnicodeNormalization.Data.cs disagrees with the pinned UCD files. "
            + "Run Emit_regenerated_data_file_test to produce a fresh copy.");
    }

    // The tables are private to UnicodeNormalization, read by reflection
    // for the structural checks, following the ReadRangeStarts precedent
    // in GraphemeSegmentationDataTests.
    private static int[] ReadPrivateIntArray(string fieldName)
    {
        FieldInfo? field = typeof(UnicodeNormalization).GetField(
            fieldName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(field, Is.Not.Null, $"UnicodeNormalization.{fieldName} field not found");
        return (int[])field!.GetValue(null)!;
    }

    // Regeneration tool, deliberately not discoverable as a test: even
    // an [Explicit] test runs when a name or category filter selects
    // it, and this one overwrites nothing itself but shouldn't burn
    // network and disk by accident. To regenerate
    // UnicodeNormalization.Data.cs, restore the attribute below, run
    // this method, and copy the emitted file over
    // src/InductorParser/Lexing/Unicode/UnicodeNormalization.Data.cs.
    // The body
    // stays compiled so it can't rot.
    // [Test, Explicit("Fetches the UCD files and writes a regenerated UnicodeNormalization.Data.cs to the temp directory.")]
    public async Task Emit_regenerated_data_file_test()
    {
        DerivedNormalizationData derived = await DeriveFromUcdAsync();

        string outputPath = Path.Combine(Path.GetTempPath(), "UnicodeNormalization.Data.cs");
        File.WriteAllText(outputPath, EmitDataFile(derived));

        int canonicalPairCount = derived.Decompositions.Count(entry =>
            !entry.Value.IsCompatibility
            && entry.Value.Expansion.Length == 2
            && !derived.FullCompositionExclusions.Contains(entry.Key));
        TestContext.Out.WriteLine($"Wrote {outputPath}.");
        TestContext.Out.WriteLine($"Decomposition entries: {derived.Decompositions.Count}.");
        TestContext.Out.WriteLine(
            $"Expansion buffer ints: {derived.Decompositions.Values.Sum(v => v.Expansion.Length)}.");
        TestContext.Out.WriteLine($"Full composition exclusions: {derived.FullCompositionExclusions.Count}.");
        TestContext.Out.WriteLine($"Derivable composition pairs: {canonicalPairCount}.");
    }

    // Downloads and merges the two UCD files exactly the way the
    // generator does: UnicodeData.txt contributes field 3 (canonical
    // combining class) and field 5 (decomposition type and mapping),
    // DerivedNormalizationProps.txt contributes Full_Composition_Exclusion
    // and nothing else. First/Last range-shorthand lines (the CJK, Hangul,
    // surrogate, and private-use blocks) must have combining class 0 and
    // no decomposition, so the ranges they abbreviate need no table rows.
    private static async Task<DerivedNormalizationData> DeriveFromUcdAsync()
    {
        var derived = new DerivedNormalizationData();
        ApplyUnicodeDataFile(await FetchUcdFileAsync("UnicodeData.txt"), derived);
        ApplyDerivedNormalizationPropsFile(
            await FetchUcdFileAsync("DerivedNormalizationProps.txt"), derived);
        return derived;
    }

    private static async Task<string> FetchUcdFileAsync(string relativePath)
    {
        string url = $"https://www.unicode.org/Public/{UnicodeVersion}/ucd/{relativePath}";
        using var http = new HttpClient();
        return await http.GetStringAsync(url);
    }

    private static void ApplyUnicodeDataFile(string content, DerivedNormalizationData derived)
    {
        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;

            string[] fields = line.Split(';');
            Assert.That(fields.Length, Is.EqualTo(15), $"unexpected UnicodeData line shape: {rawLine}");

            int codePoint = Convert.ToInt32(fields[0], 16);
            string name = fields[1];
            byte combiningClass = byte.Parse(fields[3]);
            string decompositionField = fields[5].Trim();

            if (name.EndsWith(", First>", StringComparison.Ordinal)
                || name.EndsWith(", Last>", StringComparison.Ordinal))
            {
                // Range shorthand. Everything inside the range shares this
                // line's values, so the values must be the defaults or the
                // tables would need rows the generator doesn't emit.
                Assert.That(combiningClass, Is.EqualTo(0),
                    $"range-shorthand line with nonzero combining class: {rawLine}");
                Assert.That(decompositionField, Is.Empty,
                    $"range-shorthand line with a decomposition: {rawLine}");
                continue;
            }

            derived.CanonicalCombiningClasses[codePoint] = combiningClass;

            if (decompositionField.Length == 0) continue;

            bool isCompatibility = decompositionField.StartsWith("<", StringComparison.Ordinal);
            if (isCompatibility)
            {
                int tagEnd = decompositionField.IndexOf('>');
                Assert.That(tagEnd, Is.GreaterThan(0), $"unterminated decomposition tag: {rawLine}");
                decompositionField = decompositionField.Substring(tagEnd + 1).Trim();
            }

            int[] expansion = decompositionField
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(hex => Convert.ToInt32(hex, 16))
                .ToArray();

            Assert.That(expansion.Length, Is.GreaterThan(0), $"empty decomposition mapping: {rawLine}");
            if (!isCompatibility)
            {
                Assert.That(expansion.Length, Is.LessThanOrEqualTo(2),
                    $"canonical decomposition longer than two: {rawLine}");
            }
            foreach (int scalar in expansion)
            {
                Assert.That(scalar, Is.InRange(0, 0x10FFFF), $"expansion out of range: {rawLine}");
                Assert.That(scalar is < 0xD800 or > 0xDFFF, Is.True,
                    $"expansion contains a surrogate code point: {rawLine}");
            }

            derived.Decompositions.Add(codePoint, (expansion, isCompatibility));
        }
    }

    private static void ApplyDerivedNormalizationPropsFile(
        string content, DerivedNormalizationData derived)
    {
        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine;
            int commentIndex = line.IndexOf('#');
            if (commentIndex >= 0) line = line.Substring(0, commentIndex);
            line = line.Trim();
            if (line.Length == 0) continue;

            string[] parts = line.Split(';');
            if (parts[1].Trim() != "Full_Composition_Exclusion") continue;

            string rangeText = parts[0].Trim();
            int first, last;
            int dotsIndex = rangeText.IndexOf("..", StringComparison.Ordinal);
            if (dotsIndex >= 0)
            {
                first = Convert.ToInt32(rangeText.Substring(0, dotsIndex), 16);
                last = Convert.ToInt32(rangeText.Substring(dotsIndex + 2), 16);
            }
            else
            {
                first = last = Convert.ToInt32(rangeText, 16);
            }

            for (int codePoint = first; codePoint <= last; codePoint++)
                derived.FullCompositionExclusions.Add(codePoint);
        }
    }

    // The emitted file's header text, stored without the comment marker.
    // The marker is prepended at emit time (and built with new string()
    // rather than written literally) so no line of THIS file puts prose
    // and statement punctuation after a comment marker.
    private static readonly string CommentMarker = new string('/', 2);

    private static readonly string[] DataFileHeaderLines =
    {
        "Generated file. The Unicode normalization data behind the bundled",
        "UAX #15 normalizer, at Unicode 15.0.0, the same version as the",
        "bundled segmenter's break-property table. The values come from the",
        "Unicode Character Database (the \"UCD\", Unicode's machine-readable",
        "property data, https://www.unicode.org/ucd/) and are copyright",
        "Unicode, Inc., used under the Unicode License v3",
        "(LICENSE-UNICODE.txt next to this file). Two UCD files feed the",
        "tables:",
        "",
        "  https://www.unicode.org/Public/15.0.0/ucd/UnicodeData.txt (field 3 canonical combining class, field 5 decomposition)",
        "  https://www.unicode.org/Public/15.0.0/ucd/DerivedNormalizationProps.txt (Full_Composition_Exclusion only)",
        "",
        "Three tables live here. The combining classes are transition",
        "arrays: entry i covers code points from",
        "CanonicalCombiningClassRangeStarts[i] up to but not including",
        "CanonicalCombiningClassRangeStarts[i + 1], all with class",
        "CanonicalCombiningClassRangeValues[i], and the last entry runs",
        "through U+10FFFF. GetCanonicalCombiningClass does the binary",
        "search.",
        "",
        "The decomposition mappings are stored single-level, exactly as",
        "UnicodeData.txt states them, and recursive expansion happens at",
        "runtime. Three arrays hold them. DecompositionCodePoints lists",
        "every code point that has a mapping, sorted ascending for binary",
        "search. The replacement code points of all the mappings sit back",
        "to back in one shared buffer, DecompositionExpansions.",
        "DecompositionEntries lines up with DecompositionCodePoints and",
        "describes each mapping with three facts packed into one int:",
        "where its replacement starts in the buffer, how many code points",
        "the replacement is, and whether the mapping is compatibility",
        "(applied only by the K forms) or canonical. Packing keeps the",
        "generated file to one array here instead of three more. The bit",
        "layout is (expansionOffset << 6) | (expansionLength << 1) |",
        "compatibilityBit, and TryGetDecomposition is the only code that",
        "reads it.",
        "",
        "FullCompositionExclusions holds the code points UAX #15 excludes",
        "from canonical composition, sorted. The composition pair table",
        "is derived from these tables once at static init rather than",
        "stored, so it can't drift from the decomposition data.",
        "",
        "Hangul is the one script whose decompositions are deliberately",
        "missing. Every precomposed Hangul syllable (U+AC00..U+D7A3, all",
        "11,172 of them) decomposes into two or three jamo letters, but",
        "no table is needed: the syllables are laid out in the block in",
        "a regular pattern, so a syllable's offset from U+AC00 encodes",
        "exactly which jamo built it and a little arithmetic recovers",
        "them (Unicode chapter 3.12). UnicodeData.txt makes the same",
        "choice. The file is normally one line per code point, but for",
        "this block it has just two lines, one marking the first syllable",
        "and one the last, with no decomposition data on either. So there",
        "are no per-syllable rows for the generator to read, and none are",
        "needed. The arithmetic lives in UnicodeNormalization:",
        "AppendHangulDecomposition takes a syllable apart into jamo, and",
        "TryComposePair puts jamo back together.",
        "",
        "To verify against the UCD files, run the [Explicit] test in",
        "InductorParser.Tests/Lexing/Unicode/UnicodeNormalizationDataTests.cs.",
        "To regenerate, restore the commented-out [Test] attribute on",
        "Emit_regenerated_data_file_test there and run it. Upgrading the",
        "Unicode version means regenerating this file from the newer UCD",
        "and swapping in the newer NormalizationTest data file. Unicode's",
        "normalization stability policy keeps assigned characters'",
        "normalization fixed, so a newer version adds mappings for newly",
        "assigned characters rather than changing existing ones.",
    };

    private static string EmitDataFile(DerivedNormalizationData derived)
    {
        // Run-length encode the combining classes into transition arrays.
        var combiningClassStarts = new List<int>();
        var combiningClassValues = new List<byte>();
        for (int codePoint = 0; codePoint <= 0x10FFFF; codePoint++)
        {
            byte combiningClass = derived.CanonicalCombiningClasses[codePoint];
            if (codePoint == 0 || combiningClass != derived.CanonicalCombiningClasses[codePoint - 1])
            {
                combiningClassStarts.Add(codePoint);
                combiningClassValues.Add(combiningClass);
            }
        }

        // Flatten the decomposition mappings in code point order.
        int[] decompositionCodePoints = derived.Decompositions.Keys.OrderBy(k => k).ToArray();
        var decompositionEntries = new List<int>();
        var decompositionExpansions = new List<int>();
        foreach (int codePoint in decompositionCodePoints)
        {
            (int[] expansion, bool isCompatibility) = derived.Decompositions[codePoint];
            int packed = (decompositionExpansions.Count << 6)
                | (expansion.Length << 1)
                | (isCompatibility ? 1 : 0);
            decompositionEntries.Add(packed);
            decompositionExpansions.AddRange(expansion);
        }

        var builder = new StringBuilder();
        foreach (string headerLine in DataFileHeaderLines)
        {
            builder.AppendLine(
                headerLine.Length == 0 ? CommentMarker : $"{CommentMarker} {headerLine}");
        }
        builder.AppendLine();
        builder.AppendLine("using System;");
        builder.AppendLine();
        builder.AppendLine("namespace InductorParser.Lexing.Unicode;");
        builder.AppendLine();
        builder.AppendLine("internal static partial class UnicodeNormalization");
        builder.AppendLine("{");
        builder.AppendLine($"    {CommentMarker} {combiningClassStarts.Count} transitions covering U+0000..U+10FFFF.");
        builder.AppendLine("    private static readonly int[] CanonicalCombiningClassRangeStarts =");
        builder.AppendLine("    {");
        AppendHexIntRows(builder, combiningClassStarts, perLine: 8);
        builder.AppendLine("    };");
        builder.AppendLine();
        builder.AppendLine($"    {CommentMarker} Values are canonical combining classes, stored as bytes.");
        builder.AppendLine("    private static ReadOnlySpan<byte> CanonicalCombiningClassRangeValues => new byte[]");
        builder.AppendLine("    {");
        for (int index = 0; index < combiningClassValues.Count; index += 16)
        {
            var line = new StringBuilder("        ");
            for (int column = index; column < Math.Min(index + 16, combiningClassValues.Count); column++)
                line.Append($"{combiningClassValues[column]}, ");
            builder.AppendLine(line.ToString().TrimEnd());
        }
        builder.AppendLine("    };");
        builder.AppendLine();
        builder.AppendLine($"    {CommentMarker} {decompositionCodePoints.Length} code points with a decomposition mapping, sorted ascending.");
        builder.AppendLine("    private static readonly int[] DecompositionCodePoints =");
        builder.AppendLine("    {");
        AppendHexIntRows(builder, decompositionCodePoints.ToList(), perLine: 8);
        builder.AppendLine("    };");
        builder.AppendLine();
        builder.AppendLine($"    {CommentMarker} One packed entry per code point above:");
        builder.AppendLine($"    {CommentMarker} (expansionOffset << 6) | (expansionLength << 1) | compatibilityBit.");
        builder.AppendLine("    private static readonly int[] DecompositionEntries =");
        builder.AppendLine("    {");
        AppendHexIntRows(builder, decompositionEntries, perLine: 8);
        builder.AppendLine("    };");
        builder.AppendLine();
        builder.AppendLine($"    {CommentMarker} The mappings' code points, all {decompositionExpansions.Count} back to back.");
        builder.AppendLine("    private static readonly int[] DecompositionExpansions =");
        builder.AppendLine("    {");
        AppendHexIntRows(builder, decompositionExpansions, perLine: 8);
        builder.AppendLine("    };");
        builder.AppendLine();
        builder.AppendLine($"    {CommentMarker} {derived.FullCompositionExclusions.Count} code points with Full_Composition_Exclusion, sorted ascending.");
        builder.AppendLine("    private static readonly int[] FullCompositionExclusions =");
        builder.AppendLine("    {");
        AppendHexIntRows(builder, derived.FullCompositionExclusions.ToList(), perLine: 8);
        builder.AppendLine("    };");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void AppendHexIntRows(StringBuilder builder, List<int> values, int perLine)
    {
        for (int index = 0; index < values.Count; index += perLine)
        {
            var line = new StringBuilder("        ");
            for (int column = index; column < Math.Min(index + perLine, values.Count); column++)
                line.Append($"0x{values[column]:X6}, ");
            builder.AppendLine(line.ToString().TrimEnd());
        }
    }
}
