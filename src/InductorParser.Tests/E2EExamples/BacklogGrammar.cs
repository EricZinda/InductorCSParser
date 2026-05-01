using InductorParser;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Port of MergeableBacklog's six format-detector regexes (a VSCode
// extension that splits a backlog.md into per-item files so multiple
// people can edit the backlog without git merge conflicts).
//
//   H1:         /^#(?!#)\s?(.*)$/        single # heading
//   H2:         /^##(?!#)\s?(.*)$/       ## heading
//   BulletHeading:  /^[-*+]\s?(.*)$/     bullet line (per-item delimiter)
//   HrRun:      /^[-*+]{3,}$/            "---" / "***"
//   HrSpaced:   /^[-*+]( [-*+]){2,}$/    "- - -" / "* * *"
//   Paragraph:  /\n\s*\n/                blank-line break, anywhere
//
// Anchored rules end in Eof(). Paragraph is unanchored, so it uses
// ScanUntil to find the target anywhere in the input.
//
// Paragraph: middle is InlineWhitespace so it stops at the trailing
// \n. AnyWhitespace would eat the \n and leave nothing for the
// trailing anchor.
//
// LF-only: the corpus is all LF-terminated.
public static class BacklogGrammar
{
    // .* in JS, stops at \n.
    private static readonly Rule RestOfLine =
        ZeroOrMore(NoneOf("\n"));

    // \s?
    private static readonly Rule OptionalOneWhitespace =
        Optional(OneOf(TokenSet.InlineWhitespace));

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
    public static readonly Rule BulletHeading = AllOf(
        OneOf("-*+"),
        OptionalOneWhitespace,
        RestOfLine,
        Eof()
    );

    // Horizontal rule from 3+ repeated chars: ---, ***, +++.
    // ^[-*+]{3,}$
    public static readonly Rule HrRun = AllOf(
        AtLeast(3, OneOf("-*+")),
        Eof()
    );

    // Horizontal rule from 3+ chars separated by single spaces: - - -, * * *.
    // ^[-*+]( [-*+]){2,}$
    public static readonly Rule HrSpaced = AllOf(
        OneOf("-*+"),
        AtLeast(2, AllOf(Grapheme(' '), OneOf("-*+"))),
        Eof()
    );

    // LF, zero-or-more intra-line whitespace, LF. LF-only.
    private static readonly Rule ParagraphTarget = AllOf(
        Grapheme('\n'),
        ZeroOrMore(OneOf(TokenSet.InlineWhitespace)),
        Grapheme('\n')
    );

    // Scan up to a ParagraphTarget, consume it, then the rest.
    // ScanUntil with a rule-based stopper replaces the manual
    // ZeroOrMore(AllOf(Not(target), AnyToken())) idiom.
    public static readonly Rule ParagraphSplit = AllOf(
        ScanUntil(ParagraphTarget),
        ParagraphTarget,
        ZeroOrMore(AnyToken())
    );
}
