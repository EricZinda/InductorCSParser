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

// Verification and regeneration for the generated break-property table
// in GraphemeSegmentation.Data.cs. The default tests check the table's
// structure (the binary search silently returns wrong answers if the
// starts ever come out of order). The [Explicit] verification test
// re-derives the table from the pinned Unicode 16.0.0 UCD files, the
// same two files dotnet/runtime's GenUnicodeProp merges, and asserts
// the checked-in data matches. [Explicit] because it hits unicode.org,
// following the XidIdentifierTests precedent. The regeneration tool
// below it has its [Test] attribute commented out so no filter can run
// it by accident. The version is pinned (not "latest") so the tests stay
// stable across Unicode releases: this table deliberately tracks the
// Unicode version .NET 10 ships, and bumping it is a separate decision
// (see the header of GraphemeSegmentation.Data.cs).
[TestFixture]
public class GraphemeSegmentationDataTests
{
    private const string UnicodeVersion = "16.0.0";

    // Property-name-to-value map matching the generator: names are the
    // UCD's, values are GraphemeClusterBreakType's (whose last member is
    // spelled Extended_Pictograph, following dotnet/runtime).
    private static readonly Dictionary<string, byte> BreakTypeValueByName = new()
    {
        ["Other"] = 0,
        ["CR"] = 1,
        ["LF"] = 2,
        ["Control"] = 3,
        ["Extend"] = 4,
        ["ZWJ"] = 5,
        ["Regional_Indicator"] = 6,
        ["Prepend"] = 7,
        ["SpacingMark"] = 8,
        ["L"] = 9,
        ["V"] = 10,
        ["T"] = 11,
        ["LV"] = 12,
        ["LVT"] = 13,
        ["Extended_Pictographic"] = 14,
    };

    [Test]
    public void Table_starts_are_strictly_ascending_from_zero()
    {
        int[] starts = ReadRangeStarts();
        Assert.That(starts.Length, Is.GreaterThan(0));
        Assert.That(starts[0], Is.EqualTo(0), "the first transition must cover code point 0");
        for (int index = 1; index < starts.Length; index++)
        {
            Assert.That(starts[index], Is.GreaterThan(starts[index - 1]),
                $"RangeStarts[{index}] must be greater than RangeStarts[{index - 1}]");
        }
        Assert.That(starts[^1], Is.LessThanOrEqualTo(0x10FFFF));
    }

    [Test]
    public void Every_break_type_the_table_returns_is_a_defined_enum_value()
    {
        // Probe each transition's first code point plus the code point
        // just before it (the previous range's last member), which
        // together touch every entry in the values array.
        int[] starts = ReadRangeStarts();
        foreach (int start in starts)
        {
            Assert.That(Enum.IsDefined(GraphemeSegmentation.GetBreakType(start)), Is.True,
                $"break type at U+{start:X4}");
            if (start > 0)
                Assert.That(Enum.IsDefined(GraphemeSegmentation.GetBreakType(start - 1)), Is.True,
                    $"break type at U+{start - 1:X4}");
        }
    }

    [Test]
    public void Surrogate_code_points_share_break_class_Other_with_the_replacement_char()
    {
        // DecodeRuneAt reads a stray surrogate as U+FFFD. That
        // substitution only preserves boundaries because the table
        // classifies surrogate code points and U+FFFD identically
        // (Other, per UAX #29 rev. 35). This asserts the comment on
        // DecodeRuneAt directly instead of leaving it implied by the
        // segmentation-behavior tests.
        Assert.That(GraphemeSegmentation.GetBreakType(0xFFFD),
            Is.EqualTo(GraphemeClusterBreakType.Other));
        foreach (int codePoint in new[] { 0xD800, 0xDBFF, 0xDC00, 0xDFFF })
        {
            Assert.That(GraphemeSegmentation.GetBreakType(codePoint),
                Is.EqualTo(GraphemeClusterBreakType.Other), $"U+{codePoint:X4}");
        }
    }

    [Test, Explicit("Fetches GraphemeBreakProperty.txt and emoji-data.txt from unicode.org. Run on demand when reviewing the generated table or bumping the Unicode version."), Category("RequiresNetwork")]
    public async Task Table_matches_the_pinned_UCD_files_test()
    {
        byte[] expected = await DeriveTableFromUcdAsync();
        var disagreements = new List<string>();
        for (int codePoint = 0; codePoint <= 0x10FFFF && disagreements.Count < 25; codePoint++)
        {
            byte actual = (byte)GraphemeSegmentation.GetBreakType(codePoint);
            if (actual != expected[codePoint])
            {
                disagreements.Add(
                    $"U+{codePoint:X4}: table says {(GraphemeClusterBreakType)actual}, "
                    + $"UCD says {(GraphemeClusterBreakType)expected[codePoint]}");
            }
        }
        Assert.That(disagreements, Is.Empty,
            "GraphemeSegmentation.Data.cs disagrees with the pinned UCD files. "
            + "Run Emit_regenerated_data_file_test to produce a fresh copy.");
    }

    [Test]
    public void Checked_in_data_file_matches_what_the_emitter_produces()
    {
        // Round-trip drift check, no network: rebuild the emitter's
        // input from the checked-in table itself (GetBreakType per code
        // point, run-length encoded exactly the way the regeneration
        // tool does), run the emitter, and require the result to equal
        // the checked-in file line for line. This catches a hand-edit
        // to the table or the header prose that the emitter wouldn't
        // produce, and an emitter change nobody copied into the
        // checked-in file. The [Explicit] UCD test above answers a
        // different question (does the table match unicode.org), this
        // one answers whether the file matches the tool that claims to
        // produce it.
        var starts = new List<int>();
        var values = new List<byte>();
        byte previous = 0;
        for (int codePoint = 0; codePoint <= 0x10FFFF; codePoint++)
        {
            byte value = (byte)GraphemeSegmentation.GetBreakType(codePoint);
            if (codePoint == 0 || value != previous)
            {
                starts.Add(codePoint);
                values.Add(value);
            }
            previous = value;
        }

        AssertEmitterReproducesCheckedInFile(
            EmitDataFile(starts, values), "GraphemeSegmentation.Data.cs");
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

    // Regeneration tool, deliberately not discoverable as a test: even
    // an [Explicit] test runs when a name or category filter selects
    // it, and this one overwrites nothing itself but shouldn't burn
    // network and disk by accident. To regenerate
    // GraphemeSegmentation.Data.cs, restore the attribute below, run
    // this method, and copy the emitted file over
    // src/InductorParser/Lexing/Unicode/GraphemeSegmentation.Data.cs.
    // The body
    // stays compiled so it can't rot.
    // [Test, Explicit("Fetches the UCD files and writes a regenerated GraphemeSegmentation.Data.cs to the temp directory."), Category("RequiresNetwork")]
    public async Task Emit_regenerated_data_file_test()
    {
        byte[] table = await DeriveTableFromUcdAsync();

        var starts = new List<int>();
        var values = new List<byte>();
        for (int codePoint = 0; codePoint <= 0x10FFFF; codePoint++)
        {
            if (codePoint == 0 || table[codePoint] != table[codePoint - 1])
            {
                starts.Add(codePoint);
                values.Add(table[codePoint]);
            }
        }

        string outputPath = Path.Combine(Path.GetTempPath(), "GraphemeSegmentation.Data.cs");
        File.WriteAllText(outputPath, EmitDataFile(starts, values));
        TestContext.Out.WriteLine($"Wrote {outputPath} ({starts.Count} transitions).");
    }

    // Downloads and merges the two UCD files exactly the way the
    // generator (and dotnet/runtime's GenUnicodeProp) does: every line of
    // GraphemeBreakProperty.txt must name a known break class, only
    // Extended_Pictographic is taken from emoji-data.txt, no code point
    // may be assigned twice, and everything unassigned is Other.
    private static async Task<byte[]> DeriveTableFromUcdAsync()
    {
        byte[] table = new byte[0x110000];
        bool[] assigned = new bool[0x110000];

        ApplyUcdFile(await FetchUcdFileAsync("auxiliary/GraphemeBreakProperty.txt"),
            table, assigned, extendedPictographicOnly: false);
        ApplyUcdFile(await FetchUcdFileAsync("emoji/emoji-data.txt"),
            table, assigned, extendedPictographicOnly: true);
        return table;
    }

    private static async Task<string> FetchUcdFileAsync(string relativePath)
    {
        string url = $"https://www.unicode.org/Public/{UnicodeVersion}/ucd/{relativePath}";
        using var http = new HttpClient();
        return await http.GetStringAsync(url);
    }

    private static void ApplyUcdFile(
        string content, byte[] table, bool[] assigned, bool extendedPictographicOnly)
    {
        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine;
            int commentIndex = line.IndexOf('#');
            if (commentIndex >= 0) line = line.Substring(0, commentIndex);
            line = line.Trim();
            if (line.Length == 0) continue;

            string[] parts = line.Split(';');
            Assert.That(parts.Length, Is.EqualTo(2), $"unexpected UCD line shape: {rawLine}");
            string rangeText = parts[0].Trim();
            string propertyName = parts[1].Trim();

            if (extendedPictographicOnly)
            {
                if (propertyName != "Extended_Pictographic") continue;
            }
            else
            {
                Assert.That(BreakTypeValueByName.ContainsKey(propertyName), Is.True,
                    $"unknown break property '{propertyName}' in: {rawLine}");
            }
            byte value = BreakTypeValueByName[propertyName];

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
            {
                Assert.That(assigned[codePoint], Is.False,
                    $"U+{codePoint:X4} assigned twice (second assignment: {rawLine})");
                assigned[codePoint] = true;
                table[codePoint] = value;
            }
        }
    }

    // The emitted file's header text, stored without the comment marker.
    // The marker is prepended at emit time (and built with new string()
    // rather than written literally) so no line of THIS file puts prose
    // and statement punctuation after a comment marker.
    private static readonly string CommentMarker = new string('/', 2);

    private static readonly string[] DataFileHeaderLines =
    {
        "Generated file. The grapheme cluster break property of every Unicode",
        "code point, at Unicode 16.0.0, the version .NET 10 ships. The values",
        "come from the Unicode Character Database (the \"UCD\", Unicode's",
        "machine-readable property data, https://www.unicode.org/ucd/) and are",
        "copyright Unicode, Inc., used under the Unicode License v3",
        "(LICENSE-UNICODE.txt next to this file). Two UCD files feed the table:",
        "",
        "  https://www.unicode.org/Public/16.0.0/ucd/auxiliary/GraphemeBreakProperty.txt",
        "  https://www.unicode.org/Public/16.0.0/ucd/emoji/emoji-data.txt (Extended_Pictographic only)",
        "",
        "The GraphemeBreakProperty.txt half of that pairing is what UAX #29",
        "itself prescribes: its property-values section says the",
        "Grapheme_Cluster_Break assignments \"are explicitly listed in the",
        "corresponding data file\" and that \"the values in that file are the",
        "normative property values\"",
        "(https://www.unicode.org/reports/tr29/tr29-41.html#Grapheme_Cluster_Break_Property_Values).",
        "The Extended_Pictographic property rule GB11 uses is defined by the",
        "emoji data files instead: emoji-data.txt, whose own header labels it",
        "\"Emoji Data for UTS #51\" (tr29 references the property only from",
        "its rule tables).",
        "Everything neither file lists defaults to Other, per the @missing",
        "declaration in GraphemeBreakProperty.txt's own header (it assigns",
        "Other to the whole code point range up front, and the listed",
        "entries override it). The two files assign disjoint code point sets (the",
        "generator throws if that ever stops holding), and the same merge is",
        "how dotnet/runtime's GenUnicodeProp tool builds the equivalent table",
        "behind CharUnicodeInfo.",
        "",
        "The table is stored as transition arrays. Entry i covers code points",
        "from RangeStarts[i] up to but not including RangeStarts[i + 1], all",
        "with break type RangeValues[i]. The last entry runs through U+10FFFF.",
        "GraphemeSegmentation.GetBreakType does the binary search.",
        "",
        "To verify against the UCD files, run the [Explicit] test in",
        "InductorParser.Tests/Lexing/Unicode/GraphemeSegmentationDataTests.cs.",
        "To regenerate, restore the commented-out [Test] attribute on",
        "Emit_regenerated_data_file_test there and run it.",
        "The one rule gap is deliberate: GB9c (Indic Conjunct Break, arrived",
        "in Unicode 15.1) isn't implemented because .NET 10's StringInfo",
        "doesn't implement it either (dotnet/runtime#111546), so the",
        "segmenter and StringInfo agree on every input and the GB9c lines of",
        "GraphemeBreakTest are skipped. Upgrading the Unicode version again",
        "means regenerating this file from the newer UCD, swapping in the",
        "newer GraphemeBreakTest data file, and re-checking the rule set",
        "against the .NET runtime the differential tests run on. Until then",
        "this table deliberately matches .NET 10.",
    };

    private static string EmitDataFile(List<int> starts, List<byte> values)
    {
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
        builder.AppendLine("internal static partial class GraphemeSegmentation");
        builder.AppendLine("{");
        builder.AppendLine($"    {CommentMarker} {starts.Count} transitions covering U+0000..U+10FFFF.");
        builder.AppendLine("    private static readonly int[] RangeStarts =");
        builder.AppendLine("    {");
        for (int index = 0; index < starts.Count; index += 8)
        {
            var line = new StringBuilder("        ");
            for (int column = index; column < Math.Min(index + 8, starts.Count); column++)
                line.Append($"0x{starts[column]:X6}, ");
            builder.AppendLine(line.ToString().TrimEnd());
        }
        builder.AppendLine("    };");
        builder.AppendLine();
        builder.AppendLine($"    {CommentMarker} Values are GraphemeClusterBreakType, stored as bytes.");
        builder.AppendLine("    private static ReadOnlySpan<byte> RangeValues => new byte[]");
        builder.AppendLine("    {");
        for (int index = 0; index < values.Count; index += 16)
        {
            var line = new StringBuilder("        ");
            for (int column = index; column < Math.Min(index + 16, values.Count); column++)
                line.Append($"{values[column]}, ");
            builder.AppendLine(line.ToString().TrimEnd());
        }
        builder.AppendLine("    };");
        builder.AppendLine("}");
        return builder.ToString();
    }

    // The starts array is private to GraphemeSegmentation, read by
    // reflection for the structural checks, following the
    // ReadPrivateRangeTable precedent in XidIdentifierTests.
    private static int[] ReadRangeStarts()
    {
        FieldInfo? field = typeof(GraphemeSegmentation).GetField(
            "RangeStarts", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(field, Is.Not.Null, "GraphemeSegmentation.RangeStarts field not found");
        return (int[])field!.GetValue(null)!;
    }
}
