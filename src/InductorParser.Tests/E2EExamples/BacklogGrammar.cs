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
// The ^...$ rules above end in Eof() to enforce the $ (the ^ is
// implicit, since rules match from the start of input). Paragraph
// has no anchors, so it uses ScanUntil to find the break anywhere.
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
    public static readonly Rule BulletHeading = And(
        OneOf("-*+"),
        OptionalOneWhitespace,
        RestOfLine,
        Eof()
    );

    // Horizontal rule from 3+ repeated chars: ---, ***, +++.
    // ^[-*+]{3,}$
    public static readonly Rule HrRun = And(
        AtLeast(3, OneOf("-*+")),
        Eof()
    );

    // Horizontal rule from 3+ chars separated by single spaces: - - -, * * *.
    // ^[-*+]( [-*+]){2,}$
    public static readonly Rule HrSpaced = And(
        OneOf("-*+"),
        AtLeast(2, And(Token(' '), OneOf("-*+"))),
        Eof()
    );

    // LF, zero-or-more intra-line whitespace, LF. LF-only.
    private static readonly Rule ParagraphTarget = And(
        Token('\n'),
        ZeroOrMore(OneOf(TokenSet.InlineWhitespace)),
        Token('\n')
    );

    // Scan up to a ParagraphTarget, consume it, then the rest.
    // ScanUntil with a rule-based stopper replaces the manual
    // ZeroOrMore(And(Not(target), AnyToken())) idiom.
    public static readonly Rule ParagraphSplit = And(
        ScanUntil(ParagraphTarget),
        ParagraphTarget,
        ZeroOrMore(AnyToken())
    );

    // Compile every top-level rule together so the strict "fresh-tree"
    // Compile invariant holds for tests that .Parse on more than one of
    // them. This Or is only used for its reachability (never parsed
    // against), so one Compile walk seals every reachable rule (including
    // the shared private sub-rules RestOfLine and OptionalOneWhitespace)
    // in a single pass.
    static BacklogGrammar()
    {
        Or(H1Heading, H2Heading, BulletHeading, HrRun, HrSpaced, ParagraphSplit).Compile();
    }
}
