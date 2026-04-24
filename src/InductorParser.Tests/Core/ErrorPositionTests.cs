using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;
using static InductorParser.Tests.UnicodeExamples;

namespace InductorParser.Tests;

// Tests for the derived error-position properties on ParseResult:
// ErrorLine, ErrorColumn, ErrorRuneIndex, ErrorGraphemeIndex. The
// underlying ErrorCharIndex is covered by the per-rule test fixtures.
// This file exercises the char-index -> (line, column, rune, grapheme)
// conversions specifically.
//
// ------------------------------------------------------------------
// LSP position rules the (line, column) cases below encode
// ------------------------------------------------------------------
// LSP is the Language Server Protocol, the JSON-RPC protocol VS Code,
// Neovim, JetBrains, and essentially every modern editor use to talk
// to language tooling for diagnostics, completion, go-to-definition,
// etc. ErrorLine / ErrorColumn on ParseResult follow LSP's position
// conventions end-to-end so a caller forwarding a parse error into an
// editor diagnostic does no arithmetic at the boundary. The expected
// values in this file may look off until you remember:
//
//   * Both line and column are 0-BASED. The first line is 0, not 1.
//     The first character of a line is column 0.
//
//   * Column counts UTF-16 CODE UNITS, not runes and not graphemes.
//     A supplementary-plane rune like the guitar emoji contributes 2
//     to the column count because it occupies two UTF-16 chars.
//
//   * "\n", "\r", and "\r\n" are all line terminators. "\r\n" is ONE
//     break, not two.
//
//   * A terminator char COUNTS as a column on the line it ends. In
//     "aa\nX", the '\n' is column 2 on line 0 (third character of
//     that line). The column does not skip over it.
//
//   * After the terminator, the next line starts at column 0. So the
//     'X' in "aa\nX" is (line 1, column 0), NOT column 3 of some
//     flat counter. Column is line-relative, not absolute.
//
//   * LSP positions cannot fall between the '\r' and '\n' of a "\r\n"
//     pair. A natural parse under the default GraphemeLexer never
//     leaves the cursor there (the pair is one grapheme token), but
//     if it ever does happen (e.g. RuneLexer), we attribute the '\n'
//     to the prior line so the column stays non-negative.
//
// See docs/ProgrammingModel.md "LSP Position Semantics" for the full
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
        And(ZeroOrMore(OneOf(RuneSet.Runes("a\r\n"))), Eof());

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
        Assert.That(result.ErrorRuneIndex, Is.EqualTo(0));
        Assert.That(result.ErrorGraphemeIndex, Is.EqualTo(0));
    }

    [Test]
    public void Failure_at_offset_zero_reports_line_zero_column_zero()
    {
        // "X...": fails at the very first character.
        var result = ParseAtFailure("X");

        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorLine, Is.EqualTo(0));
        Assert.That(result.ErrorColumn, Is.EqualTo(0));
    }

    [Test]
    public void Failure_mid_line_reports_matching_char_and_column()
    {
        // "aaaX": fails at offset 3.
        var result = ParseAtFailure("aaaX");

        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(result.ErrorLine, Is.EqualTo(0));
        Assert.That(result.ErrorColumn, Is.EqualTo(3));
    }

    [Test]
    public void Failure_just_after_lone_newline_is_line_one_column_zero()
    {
        // "aa\nX": fails at offset 3 (the 'X').
        var result = ParseAtFailure("aa\nX");

        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(result.ErrorLine, Is.EqualTo(1));
        Assert.That(result.ErrorColumn, Is.EqualTo(0));
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
    }

    [Test]
    public void CRLF_is_one_break_next_line_starts_after_LF()
    {
        // Under the default GraphemeLexer "\r\n" is one token, so the
        // helper grammar's OneOf (which fails on multi-rune tokens)
        // won't consume it. Use RuneLexer so \r and \n are separate
        // tokens: grammar consumes a,a,\r,\n then fails on 'X' at
        // offset 4. The \r\n pair is one logical break so 'X' is on
        // line 1 column 0.
        var rule = And(ZeroOrMore(OneOf(RuneSet.Runes("a\r\n"))), Eof());
        var result = rule.Parse("aa\r\nX", new ParseOptions { InputUnit = InputUnit.Rune });

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(4));
        Assert.That(result.ErrorLine, Is.EqualTo(1));
        Assert.That(result.ErrorColumn, Is.EqualTo(0));
    }

    [Test]
    public void Index_on_LF_half_of_CRLF_reports_prior_line()
    {
        // Under the default GraphemeLexer "\r\n" tokenizes as ONE
        // grapheme, so a natural parse never leaves the cursor between
        // the two halves. Use RuneLexer to split the pair: grammar
        // consumes "aa\r" (three runes) and the trailing Eof then fails
        // at offset 3 on the '\n'.
        //
        // LSP says positions can't fall inside a line terminator. We
        // attribute the '\n' to the prior line so the caller gets line 0
        // column 3 rather than some negative-column nonsense.
        var rule = And(
            Token('a'),
            Token('a'),
            Token('\r'),
            Eof());
        var result = rule.Parse("aa\r\n", new ParseOptions { InputUnit = InputUnit.Rune });

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(result.ErrorLine, Is.EqualTo(0));
        Assert.That(result.ErrorColumn, Is.EqualTo(3));
    }

    [Test]
    public void Lone_CR_is_a_line_break()
    {
        // "aa\rX": old-Mac line ending. Fails at offset 3 ('X').
        var result = ParseAtFailure("aa\rX");

        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(result.ErrorLine, Is.EqualTo(1));
        Assert.That(result.ErrorColumn, Is.EqualTo(0));
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
    }

    [Test]
    public void Rune_index_counts_BMP_chars_one_each()
    {
        // "abcX": three BMP chars before the failure at offset 3.
        var result = ParseAtFailure("aaaX");

        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(result.ErrorRuneIndex, Is.EqualTo(3));
    }

    [Test]
    public void Rune_index_collapses_surrogate_pair_to_one_rune()
    {
        // Guitar emoji (one rune, two UTF-16 chars) then 'X'. The grammar
        // accepts OneOrMore(Token(guitar)) followed by Eof. Fails on 'X'
        // at char offset 2 (past the two UTF-16 halves of the guitar),
        // which is one rune in.
        var rule = And(OneOrMore(Token(GuitarGrapheme)), Eof());
        var result = rule.Parse(GuitarGrapheme + "X");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
        Assert.That(result.ErrorRuneIndex, Is.EqualTo(1));
    }

    [Test]
    public void Grapheme_index_counts_BMP_chars_one_each()
    {
        // ASCII input has one grapheme per char on every runtime.
        var result = ParseAtFailure("aaaX");

        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(result.ErrorGraphemeIndex, Is.EqualTo(3));
    }

    [Test]
    public void Grapheme_index_collapses_supplementary_rune_to_one_grapheme()
    {
        // Guitar emoji is one grapheme and two UTF-16 chars.
        var rule = And(OneOrMore(Token(GuitarGrapheme)), Eof());
        var result = rule.Parse(GuitarGrapheme + "X");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
        Assert.That(result.ErrorGraphemeIndex, Is.EqualTo(1));
    }

    [Test]
    public void Multi_rune_single_grapheme_distinguishes_rune_and_grapheme_counts()
    {
        // LatinEAcuteGrapheme is e + combining acute: ONE grapheme, TWO
        // runes, TWO UTF-16 chars. This works on every runtime including
        // legacy StringInfo. Grammar matches the whole grapheme as one
        // token (under default GraphemeLexer) then fails on 'X'.
        //
        // NormalizeInput = null so the decomposed input survives to the
        // lexer. The default NFC would compose to a one-char grapheme and
        // the rune/grapheme counts the test is demonstrating wouldn't
        // diverge anymore.
        var rule = And(OneOrMore(Token(LatinEAcuteGrapheme)), Eof());
        var result = rule.Parse(LatinEAcuteGrapheme + "X",
            new ParseOptions { NormalizeInput = null });

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
        Assert.That(result.ErrorRuneIndex, Is.EqualTo(2));
        Assert.That(result.ErrorGraphemeIndex, Is.EqualTo(1));
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
        Assert.That(result.ErrorRuneIndex, Is.EqualTo(0));
        Assert.That(result.ErrorGraphemeIndex, Is.EqualTo(0));
    }
}
