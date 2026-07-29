using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using InductorParser;
using InductorParser.Lexing;
using InductorParser.Lexing.Unicode;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests.Lexing.UnicodeConformance;

// UAX #15 normalization conformance against the official
// NormalizationTest.txt suite from unicode.org, run against the built-in
// normalizer (UnicodeNormalization). Each data line holds five columns
// of hex code point sequences, c1 through c5 (source, NFC, NFD, NFKC,
// NFKD), and the file's header states the invariants every conformant
// implementation must satisfy:
//
//   c2 == toNFC(c1) == toNFC(c2) == toNFC(c3), c4 == toNFC(c4) == toNFC(c5)
//   c3 == toNFD(c1) == toNFD(c2) == toNFD(c3), c5 == toNFD(c4) == toNFD(c5)
//   c4 == toNFKC(c1..c5)
//   c5 == toNFKD(c1..c5)
//
// Plus the Part1 corollary: every code point that doesn't occur in
// column one of Part1 normalizes to itself under all four forms.
//
// The conformance file is parsed by an InductorParser grammar
// (LineGrammar below), not by ad-hoc string-splitting, following the
// GraphemeBreakConformanceTests dogfooding pattern. Unlike that suite,
// the assertions run inside a few looping tests rather than one NUnit
// case per line: the 16.0.0 file has 19,965 data lines, and per-line
// TestCaseData at that scale slows test discovery for every dotnet
// test run, opted-in or not. A failure still names the line number and
// the disagreeing form.
//
// The data file version matches the generated tables exactly (both
// 16.0.0), so there is no known-skip set here. When Unicode publishes
// a new version, drop in the new NormalizationTest-X.Y.Z.txt, update
// the path below, regenerate UnicodeNormalization.Data.cs, and rerun.
[TestFixture]
[Explicit("UAX #15 conformance suite. ~20,000 lines; opt in via dotnet test --filter TestCategory=UnicodeConformance.")]
[Category("UnicodeConformance")]
public class NormalizationConformanceTests
{
    private const string TestDataRelativePath =
        "Lexing/UnicodeConformance/NormalizationTest-16.0.0.txt";

    // ============================================================
    // Layer 1: per-line grammar, parsed with InductorParser itself.
    // ============================================================

    // Captures one hex code point as a contiguous sequence of ASCII hex
    // digits, kept as a named node so the tree walk can int.Parse it.
    private static readonly Rule HexCodepointRule =
        OneOrMore(OneOf(TokenSet.Ascii.HexDigits)).As("codepoint");

    // Column separators are named so the walk can tell "column ended"
    // from "code point inside a column" without inspecting text.
    private static readonly Rule ColumnSeparatorRule = Token(';').As("separator");

    private static readonly Rule InlineWhitespace =
        OneOrMore(OneOf(TokenSet.Ascii.InlineWhitespace));

    // One data line. Shape: five columns, each a nonempty
    // whitespace-separated hex code point list closed by a semicolon,
    // then an optional # comment.
    private static readonly Rule LineGrammar = BuildLineGrammar();

    private static Rule BuildLineGrammar()
    {
        var cell = And(Optional(InlineWhitespace), HexCodepointRule, Optional(InlineWhitespace));
        var column = And(OneOrMore(cell), ColumnSeparatorRule);
        var comment = And(Optional(InlineWhitespace), Token('#'), ScanUntilEof());
        var rule = And(column, column, column, column, column, Optional(comment), Eof());
        rule.Compile();
        return rule;
    }

    // Parses a single data line via LineGrammar into the five column
    // strings.
    private static bool TryParseLine(string line, out string[] columns, out string error)
    {
        columns = Array.Empty<string>();
        error = "";

        var result = LineGrammar.Parse(line);
        if (!result.Success)
        {
            error = $"grammar rejected line at char {result.ErrorCharIndex}: {result.ErrorMessage}";
            return false;
        }

        var parsedColumns = new List<string>();
        var currentColumn = new StringBuilder();
        foreach (var node in result.Symbols.SelectMany(EnumerateLeaves))
        {
            if (node.Is(HexCodepointRule))
            {
                int codepoint = int.Parse(node.ToString(),
                    NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                currentColumn.Append(char.ConvertFromUtf32(codepoint));
            }
            else if (node.Is(ColumnSeparatorRule))
            {
                parsedColumns.Add(currentColumn.ToString());
                currentColumn.Clear();
            }
        }

        if (parsedColumns.Count != 5)
        {
            error = $"expected 5 columns, parsed {parsedColumns.Count}";
            return false;
        }
        columns = parsedColumns.ToArray();
        return true;
    }

    private static IEnumerable<Symbol> EnumerateLeaves(Symbol root)
    {
        if (root.Children.Count == 0)
        {
            yield return root;
            yield break;
        }
        yield return root;
        foreach (var child in root.Children)
            foreach (var leaf in EnumerateLeaves(child))
                yield return leaf;
    }

    // ============================================================
    // Layer 2: the parsed file.
    // ============================================================

    private sealed class ConformanceLine
    {
        public int LineNumber { get; init; }
        public int Part { get; init; }
        public string[] Columns { get; init; } = Array.Empty<string>();
    }

    private static List<ConformanceLine> LoadLines()
    {
        string path = Path.Combine(TestContext.CurrentContext.TestDirectory, TestDataRelativePath);
        Assert.That(File.Exists(path), Is.True,
            $"{TestDataRelativePath} is missing from the test output directory");

        var lines = new List<ConformanceLine>();
        int lineNumber = 0;
        int currentPart = -1;
        foreach (string rawLine in File.ReadLines(path, Encoding.UTF8))
        {
            lineNumber++;
            string trimmed = rawLine.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;
            if (trimmed[0] == '@')
            {
                // Section header, e.g. "@Part1 # Character by character test".
                currentPart = int.Parse(trimmed.Substring(5, 1), CultureInfo.InvariantCulture);
                continue;
            }

            Assert.That(TryParseLine(rawLine, out string[] columns, out string error), Is.True,
                $"line {lineNumber}: {error}");
            lines.Add(new ConformanceLine
            {
                LineNumber = lineNumber,
                Part = currentPart,
                Columns = columns,
            });
        }

        Assert.That(lines.Count, Is.GreaterThan(18000),
            "the 16.0.0 conformance file holds 19,965 data lines; a short read means a truncated file");
        return lines;
    }

    private static string ToForm(string text, NormalizationForm form) =>
        UnicodeNormalization.NormalizeWithBundledImplementation(text, form);

    private static string Dump(string text) =>
        string.Join(" ", text.Select(codeUnit => $"U+{(int)codeUnit:X4}"));

    // ============================================================
    // The invariant tests.
    // ============================================================

    [Test]
    public void Every_line_satisfies_the_normalization_invariants()
    {
        var disagreements = new List<string>();
        foreach (ConformanceLine line in LoadLines())
        {
            if (disagreements.Count >= 25) break;
            string c1 = line.Columns[0];
            string c2 = line.Columns[1];
            string c3 = line.Columns[2];
            string c4 = line.Columns[3];
            string c5 = line.Columns[4];

            CheckInvariant(line, disagreements, "NFC", NormalizationForm.FormC,
                (c2, c1), (c2, c2), (c2, c3), (c4, c4), (c4, c5));
            CheckInvariant(line, disagreements, "NFD", NormalizationForm.FormD,
                (c3, c1), (c3, c2), (c3, c3), (c5, c4), (c5, c5));
            CheckInvariant(line, disagreements, "NFKC", NormalizationForm.FormKC,
                (c4, c1), (c4, c2), (c4, c3), (c4, c4), (c4, c5));
            CheckInvariant(line, disagreements, "NFKD", NormalizationForm.FormKD,
                (c5, c1), (c5, c2), (c5, c3), (c5, c4), (c5, c5));
        }
        Assert.That(disagreements, Is.Empty,
            "the built-in normalizer disagrees with NormalizationTest.txt");
    }

    private static void CheckInvariant(
        ConformanceLine line, List<string> disagreements, string formName,
        NormalizationForm form, params (string Expected, string Source)[] checks)
    {
        foreach ((string expected, string source) in checks)
        {
            string actual = ToForm(source, form);
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                disagreements.Add(
                    $"line {line.LineNumber}: to{formName}({Dump(source)}) "
                    + $"gave {Dump(actual)}, expected {Dump(expected)}");
                return;
            }
        }
    }

    [Test]
    public void Code_points_not_listed_in_Part1_normalize_to_themselves()
    {
        // The file's Part1 corollary. Surrogate code points are outside
        // it: they aren't scalar values, and both the built-in and
        // runtime implementations throw on them (covered in
        // UnicodeNormalizationTests). U+FFFE is skipped for the same
        // reason: pure UAX #15 normalizes it to itself, but .NET's
        // string.Normalize rejects it, and the built-in implementation
        // deliberately matches .NET (the throw-parity differential in
        // UnicodeNormalizationTests locks the two together).
        var listedInPart1 = new HashSet<int>();
        foreach (ConformanceLine line in LoadLines())
        {
            if (line.Part != 1) continue;
            string source = line.Columns[0];
            listedInPart1.Add(char.ConvertToUtf32(source, 0));
        }
        Assert.That(listedInPart1.Count, Is.GreaterThan(0), "Part1 parsed empty");

        var disagreements = new List<string>();
        for (int codePoint = 0; codePoint <= 0x10FFFF && disagreements.Count < 25; codePoint++)
        {
            if (codePoint is >= 0xD800 and <= 0xDFFF) continue;
            if (codePoint == 0xFFFE) continue;
            if (listedInPart1.Contains(codePoint)) continue;
            string text = char.ConvertFromUtf32(codePoint);
            foreach (NormalizationForm form in NormalizationExamples.AllForms)
            {
                if (!ReferenceEquals(ToForm(text, form), text))
                {
                    disagreements.Add(
                        $"U+{codePoint:X4} under {form}: expected identity, "
                        + $"got {Dump(ToForm(text, form))}");
                }
            }
        }
        Assert.That(disagreements, Is.Empty,
            "code points outside Part1 must normalize to themselves");
    }
}
