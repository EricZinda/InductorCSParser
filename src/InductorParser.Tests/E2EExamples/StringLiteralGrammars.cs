using System.Text;
using InductorParser;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// End-to-end string-literal grammars that exercise every StringChars
// shape — RuneSet stoppers, Rule stoppers, single-rune escape starts,
// no-escape forms. These mirror the sketches in
// StringCharsRule.cs's header comment, built out as runnable grammars
// so the tests can feed real inputs through them.
//
// Each grammar parses ONE complete string literal (opening delimiter,
// body, closing delimiter) and nothing else. Callers that want to
// embed these inside a larger grammar can wrap them the usual way.
//
// Tree shape: the quote delimiters are FlattenType.Delete (TokenRule's
// default) and drop out of the parsed tree at parse time, so
// Tree.ToString() on a successful parse returns just the body text.
// That keeps the tests readable: no Find calls, just direct string
// comparison against the expected body.
public static class StringLiteralGrammars
{
    // ========== JSON (RFC 8259) ==========
    //
    // "..."  Body is any rune except the closing quote, backslash
    // (escape start), and control chars U+0000..U+001F (which the RFC
    // requires be escaped). Escape set: \" \\ \/ \b \f \n \r \t and
    // \uXXXX.
    public static readonly Rule Json = BuildJson();

    // ========== Python single-line ==========
    //
    // "..."  Body is any rune except the closing quote, backslash
    // (escape start), and literal newline U+000A (Python forbids raw
    // newlines in single-line strings). Escape set is Python's full
    // set: C-style simple escapes, octal \NNN, hex \xNN, \uXXXX,
    // \UXXXXXXXX, and \N{name}. A single-quote variant is identical
    // with ' substituted for " in the delimiters and stopper set.
    public static readonly Rule PythonSingleLine = BuildPythonSingleLine();

    // ========== Python triple-quote ==========
    //
    // """...""" Body is any rune including raw newlines and
    // individual " chars. Only three consecutive " terminates.
    // Escape processing matches single-line. Uses the Rule-stopper
    // overload since the boundary is multi-rune.
    public static readonly Rule PythonTripleQuote = BuildPythonTripleQuote();

    // ========== Python raw single-line ==========
    //
    // r"..."  No escape processing. Any rune except the closing
    // quote is body (backslashes are literal). CPython's tokenizer
    // has a quirk where a raw string can't end in an odd number of
    // backslashes. That quirk is lexer-level and isn't modeled
    // here.
    public static readonly Rule PythonRawSingleLine = BuildPythonRawSingleLine();

    static StringLiteralGrammars()
    {
        Json.Compile();
        PythonSingleLine.Compile();
        PythonTripleQuote.Compile();
        PythonRawSingleLine.Compile();
    }

    private static Rule BuildJson()
    {
        var hexDigit = RuneIn(RuneSet.Ascii.Digits
                              | RuneSet.Range('a', 'f')
                              | RuneSet.Range('A', 'F'));
        var simpleEscape = RuneIn(RuneSet.Runes("\"\\/bfnrt"));
        var unicodeEscape = And(Token('u'), hexDigit, hexDigit, hexDigit, hexDigit);
        var escapeEnd = Or(simpleEscape, unicodeEscape);

        // Stoppers: the closing quote plus every C0 control char.
        // Range(0x00, 0x1F) covers U+0000..U+001F inclusive. Those
        // include TAB (0x09), LF (0x0A), and CR (0x0D) — all of
        // which JSON requires be escaped rather than embedded raw.
        var stoppers = RuneSet.Runes("\"") | RuneSet.Range(0x00, 0x1F);
        var body = StringChars(stoppers, new Rune('\\'), escapeEnd);

        return And(Token('"'), body, Token('"')).As("jsonString");
    }

    private static Rule BuildPythonSingleLine()
    {
        var body = StringChars(
            RuneSet.Runes("\"") | RuneSet.Single(0x0A),
            new Rune('\\'),
            BuildPythonEscapeEnd());

        return And(Token('"'), body, Token('"')).As("pyLineString");
    }

    private static Rule BuildPythonTripleQuote()
    {
        // Stopper is a multi-rune sequence — use the Rule-stopper
        // overload. The stopper rule runs in a peek transaction that
        // always rolls back, so the closing """ is NOT consumed by
        // the body scan. The outer And's trailing Literal matches
        // it.
        var body = StringChars(
            Literal("\"\"\""),
            new Rune('\\'),
            BuildPythonEscapeEnd());

        return And(Literal("\"\"\""), body, Literal("\"\"\"")).As("pyTripleString");
    }

    private static Rule BuildPythonRawSingleLine()
    {
        // No escape start. The literal-only StringChars overload.
        // Backslashes inside the body are just body content.
        var body = StringChars(RuneSet.Runes("\""));
        return And(Token('r'), Token('"'), body, Token('"')).As("pyRawString");
    }

    // Python's escape end: try more-specific shapes before
    // simple, so \u, \U, \x, \N, and octal all get their own
    // dedicated branch before the catch-all single-char escape.
    //
    // Ordering notes:
    //   * octal first among numeric forms: greedily consumes 1-3
    //     octal digits after the backslash. PEG is first-match-
    //     wins, but BetweenInclusive is greedy inside the branch,
    //     which matches Python's "up to 3 digits" rule.
    //   * simple last: catches everything else in the allowed set.
    //     \0 isn't in simple because octal already handles it.
    private static Rule BuildPythonEscapeEnd()
    {
        var hexDigit = RuneIn(RuneSet.Ascii.Digits
                              | RuneSet.Range('a', 'f')
                              | RuneSet.Range('A', 'F'));
        var octalDigit = RuneIn(RuneSet.Range('0', '7'));

        // \NNN  one to three octal digits
        var octalEscape = BetweenInclusive(octalDigit, 1, 3);

        // \xNN  exactly two hex digits
        var hexEscape = And(Token('x'), hexDigit, hexDigit);

        // \uNNNN  exactly four hex digits
        var unicode4 = And(Token('u'), hexDigit, hexDigit, hexDigit, hexDigit);

        // \UNNNNNNNN  exactly eight hex digits
        var unicode8 = And(Token('U'),
                           hexDigit, hexDigit, hexDigit, hexDigit,
                           hexDigit, hexDigit, hexDigit, hexDigit);

        // \N{name}  Unicode character name. Real Python restricts
        // name content to a printable-ASCII subset. We accept any
        // non-} rune as a simplifying sketch. Matches the shape,
        // not the validation.
        var nameChar = RuneIn(~RuneSet.Runes("}"));
        var namedEscape = And(Token('N'), Token('{'), OneOrMore(nameChar), Token('}'));

        // Simple single-char escapes. \0 is covered by octalEscape
        // so it isn't listed here.
        var simpleEscape = RuneIn(RuneSet.Runes("\\'\"abfnrtv"));

        return Or(octalEscape, hexEscape, unicode4, unicode8, namedEscape, simpleEscape);
    }
}
