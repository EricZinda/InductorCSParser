using System.Text;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.UnicodeExamples;

using static InductorParser.Tests.CanaryHelper;
namespace InductorParser.Tests;

// Framework-level / cross-cutting tests for Symbol.SourceRange and
// SourcePosition.
//
// Rule-shape-specific SourceRange behavior (And-composite span,
// Literal range, Token range, ScanUntil zero-width body, Optional
// zero-match, Peek/Not/Eof zero-width anchor, OneOrMore iteration
// span, etc.) lives in each rule's own fixture under Rules/, per the
// TestArchitecture.md "Per-rule SourceText / SourceRange behavior
// tests live in each rule's own test file." convention.
//
// This fixture covers what doesn't fit any one rule's file:
//   - SourcePosition unit derivation: how Line / Column / TokenIndex /
//     CharIndex relate as a match spans multi-line input, CRLF, lone
//     CR, multi-rune graphemes, surrogate pairs, combining marks.
//   - FormC / FormKC translator edge cases: composite endpoint
//     translation, original-input Column under normalized parseInput,
//     FormKC ligature expansion mapping multiple parseInput leaves
//     back into one original cluster.
//   - ParseResult.ErrorPosition shape: returns null on success,
//     returns a SourcePosition with all four units on failure.
[TestFixture]
public class SymbolPositionTests
{
    [Test]
    public void Range_reports_zero_based_line_and_column_on_multi_line_input()
    {
        // Match a literal on line 2 (zero-based). Verifies that
        // SourcePosition.From walks newlines correctly to produce the
        // line/column derivation at both endpoints of the range.
        var literal = Literal("target").As("target").Preserve();
        var rule = And(
            Literal("first\n"),
            Literal("second\n"),
            literal,
            Literal(" trailing"));
        var result = rule.Parse("first\nsecond\ntarget trailing");

        var symbol = result.Tree!.Find(literal)!;
        var range = symbol.SourceRange!.Value;

        // "first\n" = 6 chars, "second\n" = 7 chars. target starts at
        // char 13, line 2, column 0.
        Assert.That(range.Start.CharIndex, Is.EqualTo(13));
        Assert.That(range.Start.Line, Is.EqualTo(2));
        Assert.That(range.Start.Column, Is.EqualTo(0));

        // End: char 19 (13 + 6), still on line 2, column 6.
        Assert.That(range.End.CharIndex, Is.EqualTo(19));
        Assert.That(range.End.Line, Is.EqualTo(2));
        Assert.That(range.End.Column, Is.EqualTo(6));
    }

    [Test]
    public void Range_starts_on_later_line_when_named_rule_begins_after_newline()
    {
        // The named rule's first preserved leaf sits on line 2. Verify
        // Start carries the right line and a line-relative column even
        // though the outer parse started at line 0.
        var target = Literal("hi").As("target").Preserve();
        var rule = And(Literal("first\nsecond\n  "), target);
        var result = rule.Parse("first\nsecond\n  hi");

        var range = result.Tree!.Find(target)!.SourceRange!.Value;
        // "first\n" = 6, "second\n" = 7, "  " = 2. Target at char 15.
        Assert.That(range.Start.CharIndex, Is.EqualTo(15));
        Assert.That(range.Start.Line, Is.EqualTo(2));
        Assert.That(range.Start.Column, Is.EqualTo(2));
        Assert.That(range.End.Line, Is.EqualTo(2));
        Assert.That(range.End.Column, Is.EqualTo(4));
    }

    [Test]
    public void Range_treats_CRLF_as_one_line_break()
    {
        // CRLF is a single LSP line terminator and a single UAX #29
        // grapheme. End.Line bumps by exactly 1; End.TokenIndex is 5
        // (a, b, \r\n, c, d).
        var rule = Literal("ab\r\ncd").Preserve();
        var result = rule.Parse("ab\r\ncd");

        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.End.CharIndex, Is.EqualTo(6));
        Assert.That(range.End.Line, Is.EqualTo(1));
        Assert.That(range.End.Column, Is.EqualTo(2));
        Assert.That(range.End.TokenIndex, Is.EqualTo(5));
    }

    [Test]
    public void Range_End_at_input_end_after_lone_CR_reports_next_line()
    {
        // Trailing lone "\r" (not followed by "\n") is its own line
        // terminator. End at input.Length after the \r is reported as
        // line 1 column 0.
        var rule = Literal("ab\r").Preserve();
        var result = rule.Parse("ab\r");

        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.End.CharIndex, Is.EqualTo(3));
        Assert.That(range.End.Line, Is.EqualTo(1));
        Assert.That(range.End.Column, Is.EqualTo(0));
    }

    [Test]
    public void Range_indices_diverge_for_multi_rune_grapheme_input()
    {
        // Input: family emoji (8 chars / 5 runes / 1 grapheme) then
        // "ab". The "ab" literal's indices reflect the family emoji
        // preceding it. Verifies CharIndex vs TokenIndex (grapheme
        // index) divergence on multi-rune graphemes.
        // Man + ZWJ + Woman + ZWJ + Girl. 8 chars, 5 runes, 1 grapheme.
        string FamilyEmoji = UnicodeExamples.FamilyManWomanGirlGrapheme;
        var target = Literal("ab").As("target").Preserve();
        var rule = And(Literal(FamilyEmoji), target);
        var result = rule.Parse(FamilyEmoji + "ab");

        var range = result.Tree!.Find(target)!.SourceRange!.Value;
        // Start: family emoji = 8 chars / 1 grapheme.
        Assert.That(range.Start.CharIndex, Is.EqualTo(8));
        Assert.That(range.Start.TokenIndex, Is.EqualTo(1));
        Assert.That(range.Start.Line, Is.EqualTo(0));
        Assert.That(range.Start.Column, Is.EqualTo(8));

        // End: family emoji + "ab" = 10 chars / 3 graphemes.
        Assert.That(range.End.CharIndex, Is.EqualTo(10));
        Assert.That(range.End.TokenIndex, Is.EqualTo(3));
    }

    [Test]
    public void Range_for_two_supplementary_emojis_diverges_char_from_TokenIndex()
    {
        // Two waving-hand emojis (each 2 chars / 1 grapheme). End at
        // char 4, grapheme 2. Catches a bug where the End walks treat
        // surrogate pairs as two graphemes.
        string Input = WavingHandGrapheme + WavingHandGrapheme;
        var rule = Literal(Input).Preserve();
        var result = rule.Parse(Input);

        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.End.CharIndex, Is.EqualTo(4));
        Assert.That(range.End.TokenIndex, Is.EqualTo(2));
    }

    [Test]
    public void Identifier_range_diverges_grapheme_from_rune_for_combining_mark()
    {
        // "réx" is r + (e + combining acute) + x — three graphemes,
        // four runes, four UTF-16 chars. With normalization disabled
        // (default NFC would compose to "e-acute" and collapse to 3
        // chars), Identifier consumes all of it via WithinToken
        // leaves. The composite range reports End.CharIndex = 4 but
        // End.TokenIndex = 3.
        string Input = "re" + CombiningAcuteText + "x";
        var rule = Identifier();
        rule.Compile(null);
        var result = rule.Parse(Input);

        Assert.That(result.Success, Is.True);
        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(4));
        Assert.That(range.End.TokenIndex, Is.EqualTo(3));
    }

    [Test]
    public void ErrorPosition_returns_null_on_success()
    {
        var rule = Token('a').Preserve();
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.True);
        Assert.That(result.ErrorPosition, Is.Null);
    }

    [Test]
    public void ErrorPosition_returns_struct_with_all_units_on_failure()
    {
        // "ab" with grammar expecting just 'a' followed by Eof fails
        // at offset 1.
        var rule = And(Token('a'), Eof());
        var result = rule.Parse("ab");

        Assert.That(result.Success, Is.False);
        var position = result.ErrorPosition;
        Assert.That(position, Is.Not.Null);
        Assert.That(position!.Value.CharIndex, Is.EqualTo(1));
        Assert.That(position.Value.TokenIndex, Is.EqualTo(1));
        Assert.That(position.Value.Line, Is.EqualTo(0));
        Assert.That(position.Value.Column, Is.EqualTo(1));
    }

    // -------------------------------------------------------------
    // Cross-cutting SourceRange-under-normalization tests.
    //
    // Per-rule SourceRange-matrix tests (one [TestCaseSource]
    // parameterized over NormalizationExamples.RowFormPairs per leaf
    // rule) live in each rule's own file under Rules/. See
    // TestArchitecture.md.
    //
    // The tests below exercise the position-translator's machinery
    // without focusing on any one rule type — composite endpoint
    // translation, Column / TokenIndex translation, and the FormKC
    // per-grapheme-expansion edge case where one source cluster
    // spawns multiple parseInput leaves. Keeping them centralized
    // here means a regression in the translator surfaces in one place
    // rather than scattered across the leaf-rule files.
    // -------------------------------------------------------------

    // Decomposed "café" is 5 chars (c, a, f, e, U+0301). Under the
    // default FormC compile, it normalizes to precomposed "café"
    // (4 chars). Anything after the prefix sits one char further along
    // in the original input than in parseInput.
    //
    // The combining acute is spelled with an explicit ́ escape
    // rather than a literal "é" so the source file's text-encoding
    // doesn't decide whether the constant is decomposed or precomposed.
    // Many editors / save pipelines silently normalize source bytes
    // to NFC.
    private static readonly string DecomposedCafePrefix = $"cafe{UnicodeExamples.CombiningAcuteText}";

    [Test]
    public void SourceRange_for_composite_under_FormC_translates_both_endpoints()
    {
        // Both endpoints sit after the rewrite. A fix that translated
        // only one of (Start, End) would slip past the per-leaf tests
        // but break here.
        string input = DecomposedCafePrefix + "XYZ";
        var first = Token('X').As("first").Preserve();
        var last = Token('Z').As("last").Preserve();
        var composite = And(first, Token('Y').Preserve(), last).As("composite").Preserve();
        var rule = And(Literal(UnicodeExamples.CafePrecomposedGrapheme), composite);
        var result = rule.Parse(input);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        var range = result.Tree!.Find(composite)!.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(5));
        Assert.That(range.End.CharIndex, Is.EqualTo(8));
    }

    [Test]
    public void SourceRange_under_FormC_Column_uses_original_input_coords()
    {
        // Column for the FormC + decomposed case differs by one
        // (parseInput is one char shorter than original on the same
        // line). TokenIndex differs only when graphemes are ADDED or
        // REMOVED — covered by the FormKC ligature test below. Line
        // never differs.
        string input = DecomposedCafePrefix + "X";
        var target = Token('X').As("x").Preserve();
        var rule = And(Literal(UnicodeExamples.CafePrecomposedGrapheme), target);
        var result = rule.Parse(input);

        var range = result.Tree!.Find(target)!.SourceRange!.Value;
        Assert.That(range.Start.Column, Is.EqualTo(5),
            "Column at the X should reflect original-input position (5), not parseInput position (4).");
        Assert.That(range.End.Column, Is.EqualTo(6));
    }

    [Test]
    public void SourceRange_under_FormKC_ligature_expansion_maps_two_leaves_into_original_cluster()
    {
        // The ligature U+FB01 is one grapheme in original (1 char) but
        // expands to "fi" (2 chars / 2 graphemes) under FormKC. Two
        // parse-time leaves come from one source cluster — both should
        // map back into the original ligature span [0, 1).
        // NormalizedPositionMap's per-grapheme walker maps any
        // parseInput offset INSIDE the rewritten run back to the start
        // of the original cluster, and the offset just PAST the run to
        // one past it. So leaf 'f' lands at [0, 0) (zero-width inside
        // the cluster) and leaf 'i' at [0, 1) (full cluster span). 'X'
        // lands cleanly one cluster past the ligature.
        string input = Canary("ﬁX", "latin small ligature fi + latin capital letter x", 0xFB01, 0x0058);
        var fLeaf = Token('f').As("f").Preserve();
        var iLeaf = Token('i').As("i").Preserve();
        var xLeaf = Token('X').As("x").Preserve();
        var rule = And(fLeaf, iLeaf, xLeaf).As("root").Preserve();
        rule.Compile(NormalizationForm.FormKC);
        var result = rule.Parse(input);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        var fRange = result.Tree!.Find(fLeaf)!.SourceRange!.Value;
        var iRange = result.Tree!.Find(iLeaf)!.SourceRange!.Value;
        var xRange = result.Tree!.Find(xLeaf)!.SourceRange!.Value;

        // Both ligature pieces map inside the original ligature cluster.
        Assert.That(fRange.Start.CharIndex, Is.GreaterThanOrEqualTo(0));
        Assert.That(fRange.End.CharIndex, Is.LessThanOrEqualTo(1));
        Assert.That(iRange.Start.CharIndex, Is.GreaterThanOrEqualTo(0));
        Assert.That(iRange.End.CharIndex, Is.LessThanOrEqualTo(1));

        // X follows at original offset 1.
        Assert.That(xRange.Start.CharIndex, Is.EqualTo(1));
        Assert.That(xRange.End.CharIndex, Is.EqualTo(2));
        Assert.That(xRange.Start.TokenIndex, Is.EqualTo(1),
            $"X TokenIndex should be 1 (1 original grapheme before it: {UnicodeExamples.FiLigatureGrapheme}), not 2 (2 parseInput graphemes before it: f, i).");
        Assert.That(xRange.End.TokenIndex, Is.EqualTo(2));
    }
}
