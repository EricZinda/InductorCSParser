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
// in Eof(). Paragraph is unanchored — JS .test() returns true if the
// pattern occurs anywhere. The PEG equivalent scans forward with
// And(ZeroOrMore(And(Not(target), AnyChar())), target, ZeroOrMore(AnyChar()))
// and relies on lexer.IsEof for Parse success, so the trailing
// ZeroOrMore(AnyChar()) isn't decorative — it's what lets success happen
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
        ZeroOrMore(RuneNotIn("\n"));

    // \s? : optional whitespace grapheme. Uses RuneSet.Whitespace so we
    // pick up the same broad class .NET's Regex \s uses.
    private static readonly Rule OptionalOneWhitespace =
        Optional(RuneIn(RuneSet.Whitespace));

    // ^#(?!#)\s?(.*)$
    public static readonly Rule H1Heading = And(
        Char('#'),
        Not(Char('#')),
        OptionalOneWhitespace,
        RestOfLine,
        Eof()
    );

    // ^##(?!#)\s?(.*)$
    public static readonly Rule H2Heading = And(
        Literal("##"),
        Not(Char('#')),
        OptionalOneWhitespace,
        RestOfLine,
        Eof()
    );

    // ^[-*+]\s?(.*)$
    public static readonly Rule Bullet = And(
        RuneIn("-*+"),
        OptionalOneWhitespace,
        RestOfLine,
        Eof()
    );

    // ^[-*+]{3,}$
    public static readonly Rule HrRun = And(
        NOrMore(RuneIn("-*+"), 3),
        Eof()
    );

    // ^[-*+]( [-*+]){2,}$
    public static readonly Rule HrSpaced = And(
        RuneIn("-*+"),
        NOrMore(And(Char(' '), RuneIn("-*+")), 2),
        Eof()
    );

    // \n\s*\n, unanchored. Middle restricted to non-newline whitespace
    // so the greedy PEG ZeroOrMore can't run past the terminating \n.
    // See the class comment above for why this preserves equivalence.
    private static readonly RuneSet NonNewlineWhitespace =
        RuneSet.Whitespace & ~RuneSet.Runes("\n\r");

    private static readonly Rule ParagraphTarget = And(
        Char('\n'),
        ZeroOrMore(RuneIn(NonNewlineWhitespace)),
        Char('\n')
    );

    public static readonly Rule ParagraphSplit = And(
        ZeroOrMore(And(Not(ParagraphTarget), AnyChar())),
        ParagraphTarget,
        ZeroOrMore(AnyChar())
    );
}
