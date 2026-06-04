using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using InductorParser;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

using static InductorParser.Tests.CanaryHelper;
namespace InductorParser.Tests.Lexing.UnicodeConformance;

// UAX #29 grapheme cluster boundary conformance against the official
// GraphemeBreakTest.txt suite from unicode.org. Each line of the file
// is one test case: a sequence of hex code points separated by ÷
// (break opportunity) or × (no break), with optional trailing comment.
//
// Two layers of dogfooding here:
//
//   1. The conformance file is parsed by an InductorParser grammar
//      (LineGrammar below), not by ad-hoc string-splitting. That
//      exercises the parser's grammar composition (And / Or /
//      OneOrMore / Optional / OneOf / Token / Literal) on a real text
//      file format before the per-line assertions run.
//
//   2. Each parsed line then drives several per-test-case fixtures:
//      AnyToken loops, Token(string) per cluster, Literal over the
//      whole input, and OneOf / NoneOf with every TokenSet
//      construction overload (Single(char|int|Rune), Range, Runes,
//      |, &, ~, Universe). The point is to verify each leaf rule and
//      each TokenSet API path agrees with UAX #29 across the full
//      conformance corpus, not just the hand-picked cases in
//      GraphemeClusterIndexTests / ValidUnicodeTests / Unexpected-
//      UnicodeTests.
//
// Marked [Explicit] because the suite expands to ~1180 cases per
// leaf-rule fixture (around fifteen fixtures total). Default `dotnet
// test` runs skip the whole fixture. Two opt-in patterns:
//
//   # all conformance fixtures (category filter is "explicit
//   # selection" enough to clear the [Explicit] gate):
//   dotnet test --filter "TestCategory=UnicodeConformance"
//
//   # one specific fixture by method name:
//   dotnet test --filter "Name~GraphemeClusterIndex_boundaries_match_spec"
//
// NUnit's [Explicit] requires the test to be picked out specifically
// by Name or Category. A class-level FullyQualifiedName filter that
// doesn't name a method gets discovered as "0 tests run." The
// [Category("UnicodeConformance")] on the fixture below is what makes
// the category-filter opt-in path work.
//
// When a vendored UAX #29 implementation replaces StringInfo, this
// test runs against the new implementation unchanged. When Unicode
// publishes a new version, drop in the new GraphemeBreakTest-X.Y.Z.txt,
// update the path below, and rerun. Any newly-failing cases either
// reflect a runtime gap (Unicode versions ahead of .NET's bundled ICU)
// or a regression in whatever implementation is hosting the suite.
[TestFixture]
[Explicit("UAX #29 conformance suite. ~10,000 cases total; opt in via dotnet test --filter TestCategory=UnicodeConformance.")]
[Category("UnicodeConformance")]
public class GraphemeBreakConformanceTests
{
    private const string TestDataRelativePath =
        "Lexing/UnicodeConformance/GraphemeBreakTest-15.1.0.txt";

    // Lines in GraphemeBreakTest-15.1.0.txt that test UAX #29 rule GB9c
    // (Indic Conjunct Cluster), introduced in revision 39 alongside
    // Unicode 15.1. .NET 8's StringInfo enumerator implements an
    // earlier revision and breaks these clusters differently than the
    // spec expects. A future vendored UAX #29 implementation can drop
    // this skip set and the conformance test will start asserting
    // these lines for real.
    private static readonly HashSet<int> KnownRuntimeSkips = new()
    {
        1202, 1203, 1204, 1205, 1206, 1207, 1211,
    };

    // ============================================================
    // Layer 1: per-line grammar, parsed with InductorParser itself.
    // ============================================================

    // Captures one hex code point as a contiguous run of ASCII hex
    // digits. Preserve()d so the parsed Symbol survives flattening and
    // its raw text is available for int.Parse.
    private static readonly Rule HexCodepointRule =
        OneOrMore(OneOf(TokenSet.Ascii.HexDigits)).As("codepoint");

    // The two break markers in the spec format. Distinguished by name
    // so the tree walk can tell "boundary here" from "no boundary here"
    // without inspecting the matched text.
    private static readonly Rule BreakRule = Token('\u00F7').As("break");
    private static readonly Rule NoBreakRule = Token('\u00D7').As("nobreak");
    private static readonly Rule MarkerRule = Or(BreakRule, NoBreakRule);
    private static readonly Rule InlineWhitespace =
        OneOrMore(OneOf(TokenSet.Ascii.InlineWhitespace));

    // One test line. Shape: ÷ (cp marker)+ comment?
    // The leading ÷ marks the always-present start-of-text break; the
    // trailing marker for each cp captures whether the boundary AFTER
    // that cp is a break (÷) or not (×). The optional comment starts
    // with # and runs to end of line.
    private static readonly Rule LineGrammar = BuildLineGrammar();

    private static Rule BuildLineGrammar()
    {
        var cell = And(InlineWhitespace, HexCodepointRule, InlineWhitespace, MarkerRule);
        var comment = And(Optional(InlineWhitespace), Token('#'), ScanUntilEof());
        var rule = And(BreakRule, OneOrMore(cell), Optional(comment), Eof());
        rule.Compile();
        return rule;
    }

    // Parses a single test line via LineGrammar and walks the resulting
    // Symbol tree to extract the input string and the spec-expected
    // break offsets (UTF-16 char indices, including 0 and input.Length).
    private static bool TryParseLine(string line, out string input, out int[] expectedBreaks, out string error)
    {
        input = "";
        expectedBreaks = Array.Empty<int>();
        error = "";

        var result = LineGrammar.Parse(line);
        if (!result.Success)
        {
            error = $"grammar rejected line at char {result.ErrorCharIndex}: {result.ErrorMessage}";
            return false;
        }

        var inputBuilder = new StringBuilder();
        var breaks = new List<int>();
        Symbol? pendingCodepoint = null;
        // Top-level root is FlattenType.Flatten by default, so its
        // children bubble up to result.Symbols rather than into a
        // single result.Tree. EnumerateLeaves walks each top-level
        // Symbol and yields the named-Preserve leaves underneath.
        foreach (var node in result.Symbols.SelectMany(EnumerateLeaves))
        {
            if (node.Is(HexCodepointRule))
            {
                pendingCodepoint = node;
            }
            else if (node.Is(BreakRule))
            {
                if (pendingCodepoint != null)
                {
                    int codepoint = int.Parse(pendingCodepoint.ToString(),
                                              NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    inputBuilder.Append(char.ConvertFromUtf32(codepoint));
                    pendingCodepoint = null;
                }
                breaks.Add(inputBuilder.Length);
            }
            else if (node.Is(NoBreakRule))
            {
                if (pendingCodepoint == null)
                {
                    error = $"{UnicodeExamples.MultiplicationSignGrapheme} marker with no preceding code point";
                    return false;
                }
                int codepoint = int.Parse(pendingCodepoint.ToString(),
                                          NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                inputBuilder.Append(char.ConvertFromUtf32(codepoint));
                pendingCodepoint = null;
            }
        }

        input = inputBuilder.ToString();
        expectedBreaks = breaks.ToArray();
        return true;
    }

    private static IEnumerable<Symbol> EnumerateLeaves(Symbol root)
    {
        if (root.Children.Count == 0)
        {
            yield return root;
            yield break;
        }
        // Yield the parent itself too so callers can match against
        // Preserve()d composite nodes by SymbolId, plus the leaf chars
        // underneath them. Most callers filter by .Is(rule) so this
        // doesn't double-count.
        yield return root;
        foreach (var child in root.Children)
            foreach (var leaf in EnumerateLeaves(child))
                yield return leaf;
    }

    // ============================================================
    // Layer 2: per-line test cases, generated once and reused across
    // every fixture below.
    // ============================================================

    public sealed class ConformanceCase
    {
        public int LineNumber { get; init; }
        public string RawLine { get; init; } = "";
        public string Input { get; init; } = "";
        public int[] ExpectedBreaks { get; init; } = Array.Empty<int>();

        // First UTF-16 char offset that is a non-zero break, i.e. the
        // end of the first cluster. Used by tests that need to isolate
        // "the first cluster" for OneOf / NoneOf / TokenSet probes.
        public int FirstClusterEnd => ExpectedBreaks.Length >= 2 ? ExpectedBreaks[1] : Input.Length;

        public string FirstCluster => Input.Substring(0, FirstClusterEnd);

        // Code point of the first cluster IFF that cluster is a single
        // rune (one or two UTF-16 chars representing one scalar value).
        // Returns -1 for multi-rune clusters (the woman-shrugging,
        // family-ZWJ, keycap shapes). Tests that only make sense on
        // single-rune clusters use this to skip otherwise.
        public int FirstClusterSingleRune
        {
            get
            {
                string cluster = FirstCluster;
                if (cluster.Length == 1 && !char.IsSurrogate(cluster[0]))
                    return cluster[0];
                if (cluster.Length == 2 && RuneHelpers.IsSurrogatePairAt(cluster, 0))
                    return char.ConvertToUtf32(cluster[0], cluster[1]);
                return -1;
            }
        }

        public override string ToString() =>
            $"line {LineNumber}: {Truncate(RawLine.Trim(), 90)}";
    }

    public static IEnumerable<TestCaseData> Cases()
    {
        string path = Path.Combine(TestContext.CurrentContext.TestDirectory, TestDataRelativePath);
        if (!File.Exists(path))
        {
            yield return new TestCaseData((ConformanceCase?)null)
                .SetName("GraphemeBreakTest.txt is missing from the test output directory");
            yield break;
        }

        int lineNumber = 0;
        foreach (string rawLine in File.ReadLines(path, Encoding.UTF8))
        {
            lineNumber++;
            string trimmed = rawLine.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;

            if (!TryParseLine(rawLine, out string input, out int[] expectedBreaks, out string error))
            {
                yield return new TestCaseData((ConformanceCase?)null)
                    .SetName($"line {lineNumber}: malformed ({error})");
                continue;
            }

            var conformanceCase = new ConformanceCase
            {
                LineNumber = lineNumber,
                RawLine = rawLine,
                Input = input,
                ExpectedBreaks = expectedBreaks,
            };
            yield return new TestCaseData(conformanceCase).SetName(conformanceCase.ToString());
        }
    }

    private static bool SkipIfKnownRuntimeDivergence(ConformanceCase? testCase)
    {
        if (testCase == null)
        {
            Assert.Fail("Test case wasn't constructed; see fixture setup for the parse error.");
            return true;
        }
        if (KnownRuntimeSkips.Contains(testCase.LineNumber))
        {
            Assert.Ignore(
                $"UAX #29 GB9c (Indic Conjunct Cluster) case not implemented by .NET 8 StringInfo. " +
                $"Will pass once a vendored UAX #29 implementation replaces StringInfo. " +
                $"Source: {testCase.RawLine}");
            return true;
        }
        return false;
    }

    // ============================================================
    // Fixture A: GraphemeClusterIndex itself.
    //   The lowest-level check: did the index pick up the same boundary
    //   offsets the spec gives? Every other fixture below depends on
    //   this being right; isolating it makes debugging easier when
    //   something fails.
    // ============================================================
    [TestCaseSource(nameof(Cases))]
    public void GraphemeClusterIndex_boundaries_match_spec(ConformanceCase? testCase)
    {
        if (SkipIfKnownRuntimeDivergence(testCase)) return;

        var index = GraphemeClusterIndex.For(testCase!.Input);
        var actualBreaks = new List<int>();
        for (int i = 0; i <= testCase.Input.Length; i++)
            if (index.IsClusterStart(i)) actualBreaks.Add(i);

        Assert.That(actualBreaks, Is.EqualTo(testCase.ExpectedBreaks),
            $"GraphemeClusterIndex boundaries diverge from UAX #29 spec.\n" +
            $"Source: {testCase.RawLine}");
    }

    // ============================================================
    // Fixture B: lexer + AnyToken loop.
    //   Drives the input through the lexer's token-by-token path and
    //   confirms OneOrMore(AnyToken()) consumes exactly one token per
    //   cluster, with the parse succeeding at exactly the expected
    //   total cluster count.
    // ============================================================
    [TestCaseSource(nameof(Cases))]
    public void AnyToken_loop_consumes_one_per_cluster(ConformanceCase? testCase)
    {
        if (SkipIfKnownRuntimeDivergence(testCase)) return;
        if (testCase!.Input.Length == 0) return;

        var rule = And(OneOrMore(AnyToken().Preserve()), Eof()).Compile();
        var result = rule.Parse(testCase.Input);
        Assert.That(result.Success, Is.True,
            $"AnyToken+Eof should consume the full input. Source: {testCase.RawLine}");

        int expectedClusterCount = testCase.ExpectedBreaks.Length - 1;
        var leaves = result.Symbols.SelectMany(EnumerateLeaves)
            .Where(s => s.Children.Count == 0).ToList();
        Assert.That(leaves.Count, Is.EqualTo(expectedClusterCount),
            $"AnyToken should produce one Symbol per UAX #29 cluster.\n" +
            $"Source: {testCase.RawLine}");
    }

    // ============================================================
    // Fixture C: Token(string) per cluster.
    //   For each cluster between consecutive break offsets, asserts
    //   Token(clusterText) matches at the corresponding position.
    //   Exercises GraphemeRule's multi-rune literal path against every
    //   cluster shape the spec enumerates.
    // ============================================================
    [TestCaseSource(nameof(Cases))]
    public void Token_string_matches_each_cluster_in_sequence(ConformanceCase? testCase)
    {
        if (SkipIfKnownRuntimeDivergence(testCase)) return;
        if (testCase!.Input.Length == 0) return;

        var clusterTokens = new List<Rule>();
        for (int i = 0; i < testCase.ExpectedBreaks.Length - 1; i++)
        {
            int start = testCase.ExpectedBreaks[i];
            int end = testCase.ExpectedBreaks[i + 1];
            string clusterText = testCase.Input.Substring(start, end - start);
            clusterTokens.Add(Token(clusterText));
        }
        clusterTokens.Add(Eof());
        var rule = And(clusterTokens.ToArray()).Compile(null);
        var result = rule.Parse(testCase.Input);

        Assert.That(result.Success, Is.True,
            $"Token(clusterText) sequence should match the input cluster-for-cluster.\n" +
            $"Source: {testCase.RawLine}\n" +
            $"Error at char {result.ErrorCharIndex}: {result.ErrorMessage}");
    }

    // ============================================================
    // Fixture D: Literal(string) over the whole input.
    //   One Literal matching the entire input. Exercises the literal-
    //   side grapheme-cluster walk inside LiteralRule against every
    //   cluster shape, and the cross-cluster matching path in one
    //   shot.
    // ============================================================
    [TestCaseSource(nameof(Cases))]
    public void Literal_matches_whole_input(ConformanceCase? testCase)
    {
        if (SkipIfKnownRuntimeDivergence(testCase)) return;
        if (testCase!.Input.Length == 0) return;

        var rule = And(Literal(testCase.Input), Eof()).Compile(null);
        var result = rule.Parse(testCase.Input);

        Assert.That(result.Success, Is.True,
            $"Literal over the whole input should match end-to-end.\n" +
            $"Source: {testCase.RawLine}");
    }

    // ============================================================
    // Fixtures E1..E6: TokenSet construction variations.
    //   Each fixture builds a TokenSet that includes the rune of the
    //   FIRST cluster via a different API path, then asserts
    //   OneOf(set) matches that first cluster. The point is to verify
    //   every TokenSet construction overload accepts code points from
    //   the spec's full conformance corpus, not just hand-picked
    //   ASCII cases.
    //
    //   Multi-rune clusters (woman shrugging, ZWJ sequences, keycap)
    //   have no single RuneValue; OneOf doesn't match them. Those
    //   lines call Assert.Ignore via the SingleRune shortcut.
    // ============================================================

    [TestCaseSource(nameof(Cases))]
    public void OneOf_via_Single_int_matches_single_rune_first_cluster(ConformanceCase? testCase)
    {
        AssertOneOfMatchesFirstClusterUsingRune(testCase, rune => TokenSet.Single(rune));
    }

    [TestCaseSource(nameof(Cases))]
    public void OneOf_via_Single_Rune_matches_single_rune_first_cluster(ConformanceCase? testCase)
    {
        AssertOneOfMatchesFirstClusterUsingRune(testCase, rune => TokenSet.Single(new Rune(rune)));
    }

    [TestCaseSource(nameof(Cases))]
    public void OneOf_via_Single_char_matches_BMP_first_cluster(ConformanceCase? testCase)
    {
        if (SkipIfKnownRuntimeDivergence(testCase)) return;
        int rune = testCase!.FirstClusterSingleRune;
        if (rune < 0) Assert.Ignore("Multi-rune first cluster; TokenSet doesn't apply.");
        if (rune > 0xFFFF) Assert.Ignore("Supplementary-plane rune; Single(char) only covers the BMP.");

        AssertOneOfMatchesFirstCluster(testCase, TokenSet.Single((char)rune));
    }

    [TestCaseSource(nameof(Cases))]
    public void OneOf_via_Range_singleton_matches_single_rune_first_cluster(ConformanceCase? testCase)
    {
        AssertOneOfMatchesFirstClusterUsingRune(testCase, rune => TokenSet.Range(rune, rune));
    }

    [TestCaseSource(nameof(Cases))]
    public void OneOf_via_Runes_string_matches_single_rune_first_cluster(ConformanceCase? testCase)
    {
        AssertOneOfMatchesFirstClusterUsingRune(testCase, rune => TokenSet.Runes(char.ConvertFromUtf32(rune)));
    }

    [TestCaseSource(nameof(Cases))]
    public void OneOf_via_Universe_matches_single_rune_first_cluster(ConformanceCase? testCase)
    {
        AssertOneOfMatchesFirstClusterUsingRune(testCase, _ => TokenSet.Universe);
    }

    [TestCaseSource(nameof(Cases))]
    public void OneOf_via_double_complement_matches_single_rune_first_cluster(ConformanceCase? testCase)
    {
        // ~(~S) round-trips back to S. Exercises the complement
        // operator twice on a real code point.
        AssertOneOfMatchesFirstClusterUsingRune(testCase, rune => ~(~TokenSet.Single(rune)));
    }

    [TestCaseSource(nameof(Cases))]
    public void OneOf_via_union_with_empty_matches_single_rune_first_cluster(ConformanceCase? testCase)
    {
        AssertOneOfMatchesFirstClusterUsingRune(testCase, rune => TokenSet.Single(rune) | default(TokenSet));
    }

    [TestCaseSource(nameof(Cases))]
    public void OneOf_via_intersection_with_universe_matches_single_rune_first_cluster(ConformanceCase? testCase)
    {
        AssertOneOfMatchesFirstClusterUsingRune(testCase, rune => TokenSet.Single(rune) & TokenSet.Universe);
    }

    [TestCaseSource(nameof(Cases))]
    public void NoneOf_with_excluding_set_matches_single_rune_first_cluster(ConformanceCase? testCase)
    {
        if (SkipIfKnownRuntimeDivergence(testCase)) return;
        int rune = testCase!.FirstClusterSingleRune;
        if (rune < 0) Assert.Ignore("Multi-rune first cluster; TokenSet doesn't apply.");

        // The complement set explicitly excludes the rune, so NoneOf
        // (which inverts membership) accepts it. Round-trips through
        // both ~ and the NoneOf rule's internal complement.
        var excludingSet = ~TokenSet.Single(rune);
        AssertRuleMatchesFirstCluster(testCase, NoneOf(excludingSet));
    }

    [TestCaseSource(nameof(Cases))]
    public void NoneOf_with_including_set_rejects_single_rune_first_cluster(ConformanceCase? testCase)
    {
        if (SkipIfKnownRuntimeDivergence(testCase)) return;
        int rune = testCase!.FirstClusterSingleRune;
        if (rune < 0) Assert.Ignore("Multi-rune first cluster; TokenSet doesn't apply.");

        // Mirror case: a set that DOES include the rune should make
        // NoneOf reject. Negative coverage on the same code-point pool
        // so both directions of the OneOf / NoneOf membership check
        // get exercised on every spec code point.
        var includingSet = TokenSet.Single(rune);
        var rule = NoneOf(includingSet).Compile(null);
        var result = rule.Parse(testCase.Input);
        Assert.That(result.Success, Is.False,
            $"NoneOf(set including rune) should reject the first cluster.\n" +
            $"Source: {testCase.RawLine}");
    }

    // Helper: build a TokenSet from the first cluster's rune via the
    // caller-supplied factory, then assert OneOf(set) matches the
    // first cluster of the input. Skips multi-rune first clusters.
    private static void AssertOneOfMatchesFirstClusterUsingRune(
        ConformanceCase? testCase, Func<int, TokenSet> factory)
    {
        if (SkipIfKnownRuntimeDivergence(testCase)) return;
        int rune = testCase!.FirstClusterSingleRune;
        if (rune < 0) Assert.Ignore("Multi-rune first cluster; TokenSet doesn't apply.");

        AssertOneOfMatchesFirstCluster(testCase, factory(rune));
    }

    private static void AssertOneOfMatchesFirstCluster(ConformanceCase testCase, TokenSet set)
    {
        AssertRuleMatchesFirstCluster(testCase, OneOf(set));
    }

    private static void AssertRuleMatchesFirstCluster(ConformanceCase testCase, Rule leafRule)
    {
        var compiled = leafRule.Compile(null);
        var result = compiled.Parse(testCase.FirstCluster);
        Assert.That(result.Success, Is.True,
            $"Rule should match first cluster {Format(testCase.FirstCluster)} (rune U+{testCase.FirstClusterSingleRune:X4}).\n" +
            $"Source: {testCase.RawLine}");
    }

    private static string Format(string text)
    {
        var sb = new StringBuilder();
        sb.Append('"');
        foreach (char c in text)
        {
            if (c >= 0x20 && c < 0x7F) sb.Append(c);
            else sb.Append("\\u" + ((int)c).ToString("X4"));
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Length <= maxLength) return value;
        return value.Substring(0, maxLength - 3) + "...";
    }
}
