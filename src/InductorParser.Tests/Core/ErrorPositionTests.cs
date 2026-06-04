using System.Linq;
using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;
using static InductorParser.Tests.UnicodeExamples;

namespace InductorParser.Tests;

// Tests for the derived error-position properties on ParseResult:
// ErrorLine, ErrorColumn, ErrorTokenIndex. The underlying
// ErrorCharIndex is covered by the per-rule test fixtures. This file
// exercises the char-index -> (line, column, grapheme) conversions
// specifically.
//
// ------------------------------------------------------------------
// LSP position rules the (line, column) cases below encode
// ------------------------------------------------------------------
// LSP is the Language Server Protocol, the JSON-RPC protocol VS Code,
// Neovim, JetBrains, and essentially every modern editor use to talk
// to language tooling for diagnostics, completion, go-to-definition,
// etc. ErrorLine / ErrorColumn on ParseResult follow LSP's position
// conventions end-to-end so a caller forwarding a parse error into an
// editor diagnostic can just use the value. The expected
// values in this file may look off until you remember:
//
//   * Both line and column are 0-BASED. The first line is 0, not 1.
//     The first character of a line is column 0.
//
//   * Column counts UTF-16 CODE UNITS, not graphemes. A supplementary-
//     plane rune like the guitar emoji contributes 2 to the column
//     count because it occupies two UTF-16 chars.
//
//   * "\n", "\r", and "\r\n" are all line terminators. "\r\n" is ONE
//     break, not two.
//
//   * A terminator char COUNTS as a column on the line it ends. In
//     "aa\nX", the '\n' is column 2 on line 0 (third character of
//     that line). The column doesn't skip over it.
//
//   * After the terminator, the next line starts at column 0. So the
//     'X' in "aa\nX" is (line 1, column 0), NOT column 3 of some
//     flat counter. Column is line-relative, not absolute.
//
// See docs/InductorParserDesignDecisions.md "LSP Position Semantics" for the full
// rationale.
// ------------------------------------------------------------------
[TestFixture]
public class ErrorPositionTests
{
    // Helper grammar that consumes any 'a', '\r', or '\n' (the prefix
    // chars used by these tests), then demands Eof. On inputs of the form
    // "<prefix>X..." the repetition eats the whole prefix including
    // newlines and the outer Eof fails at the offset of the first 'X',
    // which is where the tests below want the deepest failure recorded.
    //
    // Library doesn't yet ship NoneOf / AnyToken, so the set is spelled
    // out explicitly. Tests that need a non-'a' prefix char inline their
    // own grammar.
    private static Rule AtFailureRule() =>
        And(ZeroOrMore(OneOf(TokenSet.Single('a') | TokenSet.Single('\r') | TokenSet.Single('\n'))), Eof());

    private static ParseResult ParseAtFailure(string input)
    {
        var result = AtFailureRule().Parse(input);
        Assert.That(result.Success, Is.False, "test setup expects failure");
        return result;
    }

    [Test]
    public void Success_has_zero_positions_and_line_zero_column_zero()
    {
        var rule = And(Token('a'), Eof());
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.True);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorLine, Is.EqualTo(0));
        Assert.That(result.ErrorColumn, Is.EqualTo(0));
        Assert.That(result.ErrorTokenIndex, Is.EqualTo(0));
    }

    [Test]
    public void Failure_at_offset_zero_reports_line_zero_column_zero()
    {
        // "X...": fails at the very first character.
        var result = ParseAtFailure("X");

        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorLine, Is.EqualTo(0));
        Assert.That(result.ErrorColumn, Is.EqualTo(0));
        Assert.That(result.ErrorTokenIndex, Is.EqualTo(0));
    }

    [Test]
    public void Failure_mid_line_reports_matching_char_and_column()
    {
        // "aaaX": fails at offset 3.
        var result = ParseAtFailure("aaaX");

        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(result.ErrorLine, Is.EqualTo(0));
        Assert.That(result.ErrorColumn, Is.EqualTo(3));
        Assert.That(result.ErrorTokenIndex, Is.EqualTo(3));
    }

    [Test]
    public void Failure_just_after_lone_newline_is_line_one_column_zero()
    {
        // "aa\nX": fails at offset 3 (the 'X').
        var result = ParseAtFailure("aa\nX");

        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(result.ErrorLine, Is.EqualTo(1));
        Assert.That(result.ErrorColumn, Is.EqualTo(0));
        Assert.That(result.ErrorTokenIndex, Is.EqualTo(3));
    }

    [Test]
    public void Failure_on_lone_newline_itself_is_line_zero()
    {
        // Grammar consumes 'a's then demands Eof, so the failure position
        // lands on the first non-'a' char. Input "aa\n" fails at offset 2
        // (the '\n' itself). '\n' is the line terminator. The index that
        // lands ON it reports the line that just ended.
        var rule = And(OneOrMore(Token('a')), Eof());
        var result = rule.Parse("aa\n");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
        Assert.That(result.ErrorLine, Is.EqualTo(0));
        Assert.That(result.ErrorColumn, Is.EqualTo(2));
        Assert.That(result.ErrorTokenIndex, Is.EqualTo(2));
    }

    [Test]
    public void Lone_CR_is_a_line_break()
    {
        // "aa\rX": old-Mac line ending. Fails at offset 3 ('X').
        var result = ParseAtFailure("aa\rX");

        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(result.ErrorLine, Is.EqualTo(1));
        Assert.That(result.ErrorColumn, Is.EqualTo(0));
        // \r not followed by \n is its own grapheme cluster.
        Assert.That(result.ErrorTokenIndex, Is.EqualTo(3));
    }

    [Test]
    public void Failure_on_final_line_tracks_column_from_last_break()
    {
        // "aa\naaaX": fails at offset 6 ('X'), second line (line 1),
        // column 3.
        var result = ParseAtFailure("aa\naaaX");

        Assert.That(result.ErrorCharIndex, Is.EqualTo(6));
        Assert.That(result.ErrorLine, Is.EqualTo(1));
        Assert.That(result.ErrorColumn, Is.EqualTo(3));
        Assert.That(result.ErrorTokenIndex, Is.EqualTo(6));
    }

    [Test]
    public void Multiple_newlines_count_each_as_a_break()
    {
        // "a\n\n\nX": three line breaks between 'a' and 'X'. Fails at
        // offset 4 on line 3 (0-based), column 0.
        var result = ParseAtFailure("a\n\n\nX");

        Assert.That(result.ErrorCharIndex, Is.EqualTo(4));
        Assert.That(result.ErrorLine, Is.EqualTo(3));
        Assert.That(result.ErrorColumn, Is.EqualTo(0));
        Assert.That(result.ErrorTokenIndex, Is.EqualTo(4));
    }

    [Test]
    public void Grapheme_index_counts_BMP_chars_one_each()
    {
        // ASCII input has one grapheme per char on every runtime.
        var result = ParseAtFailure("aaaX");

        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(result.ErrorLine, Is.EqualTo(0));
        Assert.That(result.ErrorColumn, Is.EqualTo(3));
        Assert.That(result.ErrorTokenIndex, Is.EqualTo(3));
    }

    [Test]
    public void Grapheme_index_collapses_supplementary_rune_to_one_Token()
    {
        // Guitar emoji is one grapheme and two UTF-16 chars.
        var rule = And(OneOrMore(Token(GuitarGrapheme)), Eof());
        var result = rule.Parse(GuitarGrapheme + "X");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
        Assert.That(result.ErrorLine, Is.EqualTo(0));
        Assert.That(result.ErrorColumn, Is.EqualTo(2));
        Assert.That(result.ErrorTokenIndex, Is.EqualTo(1));
    }

    [Test]
    public void Multi_rune_single_grapheme_distinguishes_char_and_grapheme_counts()
    {
        // LatinEAcuteGrapheme is e + combining acute: ONE grapheme, TWO
        // UTF-16 chars. This works on every runtime including legacy
        // StringInfo. Grammar matches the whole grapheme as one token
        // then fails on the trailing letter.
        //
        // NormalizeInput = null so the decomposed input survives to the
        // lexer. The default NFC would compose to a one-char grapheme and
        // the char/grapheme counts the test is demonstrating wouldn't
        // diverge anymore.
        var rule = And(OneOrMore(Token(LatinEAcuteGrapheme)), Eof());
        rule.Compile(null);
        var result = rule.Parse(LatinEAcuteGrapheme + "X");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
        Assert.That(result.ErrorLine, Is.EqualTo(0));
        Assert.That(result.ErrorColumn, Is.EqualTo(2));
        Assert.That(result.ErrorTokenIndex, Is.EqualTo(1));
    }

    [Test]
    public void EOF_failure_reports_line_and_column_of_virtual_position()
    {
        // Grammar consumes five specific chars then requires a sixth.
        // Input "aa\naa" is five chars, so the Token(';') at the end hits
        // EOF at position 5: on line 1 ("aa"), column 2 (one past the
        // last 'a' in 0-based terms).
        var rule = And(
            Token('a'), Token('a'), Token('\n'),
            Token('a'), Token('a'),
            Token(';'));
        var result = rule.Parse("aa\naa");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(5));
        Assert.That(result.ErrorLine, Is.EqualTo(1));
        Assert.That(result.ErrorColumn, Is.EqualTo(2));
        Assert.That(result.ErrorTokenIndex, Is.EqualTo(5));
    }

    [Test]
    public void Default_struct_does_not_throw_on_derived_property_read()
    {
        // A default-constructed ParseResult has a null input reference.
        // The derived properties should coalesce rather than NRE. Not a
        // path production code hits, but cheap to guarantee.
        var result = default(ParseResult);

        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorLine, Is.EqualTo(0));
        Assert.That(result.ErrorColumn, Is.EqualTo(0));
        Assert.That(result.ErrorTokenIndex, Is.EqualTo(0));
    }

    [Test]
    public void Default_struct_does_not_report_success()
    {
        // ParseOutcome.Success is the enum's zero value, so a zeroed
        // ParseResult has Outcome == Success even though no parse produced
        // it. Without the _grammar check in ParseResult.Success, a default
        // struct would claim success while carrying a null Tree and empty
        // Symbols, so the idiomatic `if (result.Success) Use(result.Tree)`
        // would treat a never-run parse as a successful one and then NRE on
        // Tree (or silently process empty data). These are the everyday ways
        // a default ParseResult shows up: a value never assigned, an array
        // element, a LINQ default, a dictionary miss.
        Assert.That(default(ParseResult).Success, Is.False,
            "a default-constructed ParseResult must not report success");
        Assert.That((new ParseResult[1])[0].Success, Is.False,
            "an uninitialized ParseResult array element must not report success");
        Assert.That(new System.Collections.Generic.List<ParseResult>().FirstOrDefault().Success, Is.False,
            "FirstOrDefault() on an empty List<ParseResult> must not report success");

        // The companion fields stay consistent with "not a success": no
        // tree, no symbols, empty rendered text.
        Assert.That(default(ParseResult).Tree, Is.Null);
        Assert.That(default(ParseResult).Symbols, Is.Empty);
        Assert.That(default(ParseResult).ToString(), Is.EqualTo(string.Empty));
    }

    // Char(codepoint) returns a string holding one Unicode scalar value
    // for the UAX #18 line-terminator tests below. Embedding control runes
    // (NEL U+0085, LS U+2028, PS U+2029, VT U+000B, FF U+000C) as literal
    // characters in the source would either get stripped by editors or
    // break the C# compiler's line scanner (LS / PS terminate logical lines
    // in C# source). Built from char.ConvertFromUtf32 so this stays robust.
    private static string Char(int codepoint) => char.ConvertFromUtf32(codepoint);

    [Test]
    public void NEL_consumed_by_EndOfLine_bumps_ErrorLine()
    {
        // Rules.EndOfLine() accepts NEL (U+0085) per UAX #18 Annex C as a
        // line terminator. The line/column counter has to recognize the same
        // terminator set or the reported position drifts off-by-one-line for
        // any grammar that uses EndOfLine() on non-LF/CR input. Pre-fix,
        // ToLineColumn only counted LF, CRLF, and lone CR. A NEL consumed
        // by EndOfLine left ErrorLine on the prior line.
        var rule = And(EndOfLine(), Token('X'), Eof());
        var result = rule.Parse(Char(0x0085) + "Y");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
        Assert.That(result.ErrorLine, Is.EqualTo(1),
            "after EndOfLine consumes NEL, the next position is on line 1");
        Assert.That(result.ErrorColumn, Is.EqualTo(0));
    }

    [Test]
    public void Line_separator_consumed_by_EndOfLine_bumps_ErrorLine()
    {
        // LINE SEPARATOR (U+2028) is in UAX #18 Annex C and matched by
        // EndOfLine(). Same alignment requirement as NEL.
        var rule = And(EndOfLine(), Token('X'), Eof());
        var result = rule.Parse(Char(0x2028) + "Y");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
        Assert.That(result.ErrorLine, Is.EqualTo(1));
        Assert.That(result.ErrorColumn, Is.EqualTo(0));
    }

    [Test]
    public void Paragraph_separator_consumed_by_EndOfLine_bumps_ErrorLine()
    {
        // PARAGRAPH SEPARATOR (U+2029) is in UAX #18 Annex C and matched by
        // EndOfLine(). Same alignment requirement.
        var rule = And(EndOfLine(), Token('X'), Eof());
        var result = rule.Parse(Char(0x2029) + "Y");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
        Assert.That(result.ErrorLine, Is.EqualTo(1));
        Assert.That(result.ErrorColumn, Is.EqualTo(0));
    }

    [Test]
    public void Vertical_tab_consumed_by_EndOfLine_bumps_ErrorLine()
    {
        // VT (U+000B) is in UAX #18 Annex C and matched by EndOfLine().
        var rule = And(EndOfLine(), Token('X'), Eof());
        var result = rule.Parse(Char(0x000B) + "Y");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
        Assert.That(result.ErrorLine, Is.EqualTo(1));
        Assert.That(result.ErrorColumn, Is.EqualTo(0));
    }

    [Test]
    public void Form_feed_consumed_by_EndOfLine_bumps_ErrorLine()
    {
        // FF (U+000C) is in UAX #18 Annex C and matched by EndOfLine().
        var rule = And(EndOfLine(), Token('X'), Eof());
        var result = rule.Parse(Char(0x000C) + "Y");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
        Assert.That(result.ErrorLine, Is.EqualTo(1));
        Assert.That(result.ErrorColumn, Is.EqualTo(0));
    }

    [Test]
    public void ErrorCharIndex_factory_rejects_out_of_range_values()
    {
        // ErrorCharIndex must be in [0, input.Length]; out-of-range
        // throws, input.Length is the inclusive upper bound.
        var grammar = Literal("hi");
        grammar.Compile();
        var input = "ab";

        Assert.Throws<System.ArgumentOutOfRangeException>(() =>
            ParseResult.Failed(errorCharIndex: 999, message: "x", input: input, grammar: grammar));
        Assert.Throws<System.ArgumentOutOfRangeException>(() =>
            ParseResult.Aborted(ParseOutcome.Timeout, errorCharIndex: -5, message: "x", input: input, grammar: grammar));

        var atBoundary = ParseResult.Failed(errorCharIndex: input.Length, message: "x", input: input, grammar: grammar);
        Assert.That(atBoundary.ErrorCharIndex, Is.EqualTo(input.Length));
        Assert.That(atBoundary.ErrorCharIndex, Is.EqualTo(atBoundary.ErrorPosition!.Value.CharIndex));
    }
}
