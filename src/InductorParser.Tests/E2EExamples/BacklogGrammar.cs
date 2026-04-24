using InductorParser;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Backlog grammars: PEG replacements for the six format-detector regexes
// in MergeableBacklog/src/formats/*.ts. Mirrors these sources:
//
//   H1:         /^#(?!#)\s?(.*)$/        h1HeadingDetector.ts line 89
//   H2:         /^##(?!#)\s?(.*)$/       h2HeadingDetector.ts line 87
//   Bullet:     /^[-*+]\s?(.*)$/         bulletDetector.ts line 96
//   HrRun:      /^[-*+]{3,}$/            bulletDetector.ts line 88
//   HrSpaced:   /^[-*+]( [-*+]){2,}$/    bulletDetector.ts line 88
//   Paragraph:  /\n\s*\n/                paragraphDetector.ts line 90
//
// The first five are anchored (^...$) so they map to an And(...) ending
// in Eof(). Paragraph is unanchored. JS .test() returns true if the
// pattern occurs anywhere. The PEG equivalent scans forward with
// And(ZeroOrMore(And(Not(target), AnyToken())), target, ZeroOrMore(AnyToken()))
// and relies on lexer.IsEof for Parse success, so the trailing
// ZeroOrMore(AnyToken()) isn't decorative. It's what lets success happen
// after the target fires mid-string.
//
// One subtle point on the paragraph split. JS regex \n\s*\n is greedy
// with full backtracking, which lets \s* briefly swallow the terminating
// \n and then give it back. A PEG ZeroOrMore can't give back, so we
// restrict the middle to non-newline whitespace. The two are equivalent
// for IsMatch because any input where \n\s*\n matches contains some
// adjacent pair \n ...nonNL ws... \n somewhere, which the restricted
// PEG form finds.
public static class BacklogGrammar
{
    // .* in the JS regexes matches any char except line terminators.
    // Our inputs are already split on '\n' by the caller in most cases,
    // but the corpus still feeds raw multi-line strings, so be explicit.
    private static readonly Rule RestOfLine =
        ZeroOrMore(NoneOf("\n"));

    // \s? : optional whitespace grapheme. Uses RuneSet.Whitespace so we
    // pick up the same broad class .NET's Regex \s uses.
    private static readonly Rule OptionalOneWhitespace =
        Optional(OneOf(RuneSet.Whitespace));

    // ^#(?!#)\s?(.*)$
    public static readonly Rule H1Heading = And(
        Token('#'),
        Not(Token('#')),
        OptionalOneWhitespace,
        RestOfLine,
        Eof()
    );

    // ^##(?!#)\s?(.*)$
    public static readonly Rule H2Heading = And(
        Literal("##"),
        Not(Token('#')),
        OptionalOneWhitespace,
        RestOfLine,
        Eof()
    );

    // ^[-*+]\s?(.*)$
    public static readonly Rule Bullet = And(
        OneOf("-*+"),
        OptionalOneWhitespace,
        RestOfLine,
        Eof()
    );

    // ^[-*+]{3,}$
    public static readonly Rule HrRun = And(
        AtLeast(3, OneOf("-*+")),
        Eof()
    );

    // ^[-*+]( [-*+]){2,}$
    public static readonly Rule HrSpaced = And(
        OneOf("-*+"),
        AtLeast(2, And(Token(' '), OneOf("-*+"))),
        Eof()
    );

    // \n\s*\n, unanchored. Middle restricted to non-newline whitespace
    // so the greedy PEG ZeroOrMore can't run past the terminating \n.
    // See the class comment above for why this preserves equivalence.
    //
    // WARNING: LF-only on purpose. The source regex in MergeableBacklog
    // is "\n\s*\n", which in JS regex IS CRLF-agnostic (\s matches \r
    // and \n both), but under the default GraphemeLexer the two Token('\n')
    // anchors below reject CRLF line endings. "\r\n" is one grapheme
    // cluster whose content is "\r\n", not "\n". The fixtures in
    // MergeableBacklog's corpus are all LF-terminated, so the port stays
    // faithful by matching only LF. If this rule ever parses real
    // Windows-authored Markdown, it will need to add Literal("\r\n")
    // anchors alongside the Token('\n') ones. See
    // docs/UnicodeGotchas.md § "CRLF Under GraphemeLexer".
    private static readonly RuneSet NonNewlineWhitespace =
        RuneSet.Whitespace & ~RuneSet.Runes("\n\r");

    private static readonly Rule ParagraphTarget = And(
        Token('\n'),
        ZeroOrMore(OneOf(NonNewlineWhitespace)),
        Token('\n')
    );

    // StringBody with a rule-based stopper scans forward peeking
    // ParagraphTarget on each rune. When it matches, the peek rolls
    // back and StringBody returns, leaving the target for the outer
    // And to consume. Semantically identical to the manual
    // ZeroOrMore(And(Not(target), AnyToken())) idiom, one rule instead
    // of three.
    public static readonly Rule ParagraphSplit = And(
        StringBody(ParagraphTarget),
        ParagraphTarget,
        ZeroOrMore(AnyToken())
    );
}
