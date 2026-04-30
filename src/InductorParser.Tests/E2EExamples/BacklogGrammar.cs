using InductorParser;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Background: MergeableBacklog is a VSCode extension that splits a single
// backlog.md into one file per item, so multiple people can edit the
// backlog without git merge conflicts. To migrate an existing backlog.md,
// the extension first has to figure out what format the file is in (H1
// sections, H2 sections, bullet list, etc.). Today that detection lives
// in a handful of regexes in TypeScript.
//
// This file ports those six format-detector regexes to InductorParser
// rules. It's a real-world workload that's small enough to read in
// one sitting but still exercises what you actually hit when replacing
// regex with a parser combinator: anchoring, negative lookahead,
// greediness, and end-of-input handling. The six regexes are:
//
//   H1:         /^#(?!#)\s?(.*)$/        a single # heading line
//   H2:         /^##(?!#)\s?(.*)$/       a ## heading line
//   Bullet:     /^[-*+]\s?(.*)$/         a bullet line ("- foo", "* foo", "+ foo")
//   HrRun:      /^[-*+]{3,}$/            a horizontal rule made of repeated chars ("---", "***")
//   HrSpaced:   /^[-*+]( [-*+]){2,}$/    a horizontal rule with spaces ("- - -", "* * *")
//   Paragraph:  /\n\s*\n/                a blank-line paragraph break, anywhere in the input
//
// The first five are anchored (^...$) so they map to an AllOf(...) ending
// in Eof(). Paragraph is unanchored, meaning it should match anywhere in
// the input. To express that, the rule scans forward with
// AllOf(ZeroOrMore(AllOf(Not(target), AnyToken())), target, ZeroOrMore(AnyToken()))
// and relies on lexer.IsEof for Parse to report success, so the trailing
// ZeroOrMore(AnyToken()) gets used. It's what lets success happen
// after the target fires mid-string.
//
// One subtle point on the paragraph split. The regex \n\s*\n looks like
// it should map straight to a literal \n, ZeroOrMore(whitespace), and
// another literal \n. But \s includes \n itself, and regex backtracking
// lets \s* briefly swallow the terminating \n and then give it back so
// the trailing literal \n can still match. A ZeroOrMore in an
// InductorParser rule can't give back. Once it consumes a \n, it's gone.
// So we restrict the middle to non-newline whitespace instead. The two
// are equivalent for IsMatch because any input where \n\s*\n matches
// contains some adjacent pair \n ...nonNL ws... \n somewhere, which the
// restricted rule form finds.
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
    public static readonly Rule H1Heading = AllOf(
        Grapheme('#'),
        Not(Grapheme('#')),
        OptionalOneWhitespace,
        RestOfLine,
        Eof()
    );

    // ^##(?!#)\s?(.*)$
    public static readonly Rule H2Heading = AllOf(
        Literal("##"),
        Not(Grapheme('#')),
        OptionalOneWhitespace,
        RestOfLine,
        Eof()
    );

    // ^[-*+]\s?(.*)$
    public static readonly Rule Bullet = AllOf(
        OneOf("-*+"),
        OptionalOneWhitespace,
        RestOfLine,
        Eof()
    );

    // ^[-*+]{3,}$
    public static readonly Rule HrRun = AllOf(
        AtLeast(3, OneOf("-*+")),
        Eof()
    );

    // ^[-*+]( [-*+]){2,}$
    public static readonly Rule HrSpaced = AllOf(
        OneOf("-*+"),
        AtLeast(2, AllOf(Grapheme(' '), OneOf("-*+"))),
        Eof()
    );

    // \n\s*\n, unanchored. Middle restricted to non-newline whitespace
    // so the greedy ZeroOrMore can't run past the terminating \n.
    // See the class comment above for why this preserves equivalence.
    //
    // WARNING: LF-only on purpose. The source regex in MergeableBacklog
    // is "\n\s*\n", which in JS regex IS CRLF-agnostic (\s matches \r
    // and \n both), but under the default GraphemeLexer the two Grapheme('\n')
    // anchors below reject CRLF line endings. "\r\n" is one grapheme
    // cluster whose content is "\r\n", not "\n". The fixtures in
    // MergeableBacklog's corpus are all LF-terminated, so the port stays
    // faithful by matching only LF. If this rule ever parses real
    // Windows-authored Markdown, it will need to add Literal("\r\n")
    // anchors alongside the Grapheme('\n') ones. See
    // docs/UnicodeGotchas.md § "CRLF Under GraphemeLexer".
    private static readonly RuneSet NonNewlineWhitespace =
        RuneSet.Whitespace & ~RuneSet.Runes("\n\r");

    private static readonly Rule ParagraphTarget = AllOf(
        Grapheme('\n'),
        ZeroOrMore(OneOf(NonNewlineWhitespace)),
        Grapheme('\n')
    );

    // ScanUntil with a rule-based stopper scans forward peeking
    // ParagraphTarget on each rune. When it matches, the peek rolls
    // back and ScanUntil returns, leaving the target for the outer
    // AllOf to consume. Semantically identical to the manual
    // ZeroOrMore(AllOf(Not(target), AnyToken())) idiom, one rule instead
    // of three.
    public static readonly Rule ParagraphSplit = AllOf(
        ScanUntil(ParagraphTarget),
        ParagraphTarget,
        ZeroOrMore(AnyToken())
    );
}
