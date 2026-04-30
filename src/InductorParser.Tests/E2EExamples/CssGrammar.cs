using InductorParser;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// CSS grammar: Inductor Parser port of the C++ InductorParser CSS parser
// (https://github.com/EricZinda/InductorParser/blob/master/src/FXPlatform/Languages/CssParser.h).
// Covers a pragmatic subset of
// CSS 2.1: block comments, whitespace, identifiers, single/double quoted
// strings with \\" and \<CR><LF> escapes, all four simple selector kinds
// plus the universal selector, descendant combinator, selector lists,
// hex and rgba colors, url(), lengths (px/pt/%/em and unitless zero),
// declarations, rules, and the top-level document.
//
// A few notes on translating from the C++ expression-template form to
// the C# factory form:
//
//   * AndExpression<Args<A, B, ...>>           -> AllOf(A, B, ...)
//   * OrExpression<Args<A, B, ...>>            -> FirstOf(A, B, ...)
//   * OneOrMore/ZeroOrMore/Optional            -> OneOrMore/ZeroOrMore/Optional
//   * AtLeastAndAtMostExpression<X, N, M>      -> BetweenInclusive(N, M, X)
//   * LiteralExpression<"str">                 -> Literal("str")
//   * CharacterSymbol<"c">                     -> Grapheme(c)
//   * CharacterSetExceptSymbol<"chars">        -> NoneOf("chars")
//   * NotLiteralExpression<"str">              -> ZeroOrMore(AllOf(Not(Literal("str")), AnyToken()))
//   * WhitespaceSymbol / OptionalWhitespaceSymbol -> one-or-more / zero-or-more over WhitespaceChars
//
// Inductor Parser vs regex ordering: every FirstOf below is written
// longest-first where two branches share a prefix. The C++ template form
// has the same first-match-wins semantics, so this just mirrors what the
// original already relied on.
public static class CssGrammar
{
    // C++ WhitespaceChars = "\r\n\t ". The library's Whitespace() factory
    // uses the full Unicode whitespace class, which is stricter than
    // what the C++ parser actually accepts. Use the ASCII-only set so
    // the grammar decides the same way on inputs that contain NBSP or
    // other Unicode whitespace.
    private static readonly RuneSet WhitespaceChars = RuneSet.Ascii.Whitespace;

    // C++ Chars = ASCII letters only.
    private static readonly RuneSet LetterChars = RuneSet.Ascii.Letters;

    // C++ CharsAndNumbers = ASCII alphanumerics.
    private static readonly RuneSet LetterOrDigitChars =
        RuneSet.Ascii.Letters | RuneSet.Ascii.Digits;

    // C++ HexNumbers = 0-9 and A-F and a-f.
    private static readonly RuneSet HexDigitChars = RuneSet.Ascii.HexDigits;

    // /* comment */, with the body as a single ScanUntil scan on a
    // rule-based stopper. ScanUntil peeks the stopper on each rune
    // and rolls back, so the closing "*/" is left for the outer AllOf.
    public static readonly Rule BlockComment = AllOf(
        Literal("/*"),
        ScanUntil(Literal("*/")),
        Literal("*/")
    );

    // CSS whitespace: any mix of whitespace characters and block comments,
    // zero or more. Matches C++ CssWhitespaceRule.
    public static readonly Rule CssWhitespace = ZeroOrMore(FirstOf(
        OneOf(WhitespaceChars),
        BlockComment
    ));

    // Identifier = (letter | _) (letter | digit | _ | -)*
    public static readonly Rule Identifier = AllOf(
        FirstOf(OneOf(LetterChars), Grapheme('_')),
        ZeroOrMore(FirstOf(OneOf(LetterOrDigitChars), Grapheme('_'), Grapheme('-')))
    );

    // Strings can escape the quote character, include a line continuation
    // (\ followed by CRLF), or contain any other rune that isn't the
    // outer quote. The C++ version uses ReplaceExpression to rewrite
    // the escaped form in the AST. For accept/reject purposes that
    // reduces to matching the escaped form as a two-rune literal.
    public static readonly Rule DoubleQuotedString = AllOf(
        Grapheme('"'),
        ZeroOrMore(FirstOf(
            Literal("\\\""),
            Literal("\\\r\n"),
            NoneOf("\"")
        )),
        Grapheme('"')
    );

    public static readonly Rule SingleQuotedString = AllOf(
        Grapheme('\''),
        ZeroOrMore(FirstOf(
            Literal("\\'"),
            Literal("\\\r\n"),
            NoneOf("'")
        )),
        Grapheme('\'')
    );

    public static readonly Rule ValueString = FirstOf(SingleQuotedString, DoubleQuotedString);

    public static readonly Rule ClassSelector = AllOf(Grapheme('.'), Identifier);
    public static readonly Rule IdSelector = AllOf(Grapheme('#'), Identifier);

    public static readonly Rule PseudoSelector = AllOf(
        Grapheme(':'),
        Optional(Grapheme(':')),
        Identifier
    );

    public static readonly Rule TypeSelector = Identifier;
    public static readonly Rule UniversalSelector = Grapheme('*');

    // (class|id|pseudo|type|*) (class|pseudo|id)*
    // Ordering mirrors the C++ FirstOf: class/id/pseudo are distinguishable
    // by their leading sigil. TypeSelector only fires when none of the
    // others could, because it just matches a bare identifier.
    public static readonly Rule SimpleSelectorSequence = AllOf(
        FirstOf(ClassSelector, IdSelector, PseudoSelector, TypeSelector, UniversalSelector),
        ZeroOrMore(FirstOf(ClassSelector, PseudoSelector, IdSelector))
    );

    // Descendant combinator is literally whitespace. One-or-more to
    // disambiguate from an empty join.
    public static readonly Rule Combinator = OneOrMore(OneOf(WhitespaceChars));

    public static readonly Rule Selector = AllOf(
        SimpleSelectorSequence,
        ZeroOrMore(AllOf(Combinator, SimpleSelectorSequence))
    );

    public static readonly Rule SelectorList = AllOf(
        CssWhitespace,
        Selector,
        ZeroOrMore(AllOf(CssWhitespace, Grapheme(','), CssWhitespace, Selector))
    );

    // url("...") or url(anything-but-close-paren)
    public static readonly Rule ValueUrl = FirstOf(
        AllOf(
            Literal("url"),
            Grapheme('('),
            Grapheme('"'),
            ZeroOrMore(NoneOf("\"")),
            Grapheme('"'),
            Grapheme(')')
        ),
        AllOf(
            Literal("url"),
            Grapheme('('),
            ZeroOrMore(NoneOf(")")),
            Grapheme(')')
        )
    );

    // #rgb or #rrggbb. Must try 6 before 3: under Inductor Parser, a 3-digit branch
    // that matches a prefix of 6 digits would leave three digits unparsed
    // and break the surrounding declaration.
    //
    // The trailing Peek(Not(...)) is a deliberate deviation from the
    // literal C++ port. Without it the grammar accepts "#fffff" by
    // matching the 3-digit arm and leaving "ff" to be parsed as a
    // separate identifier value in the OneOrMore value-list. That makes
    // "color: #fffff;" parse as "color: #fff ff;", technically valid
    // under the value-list production but almost never what the author
    // meant. The Peek demands a hex-digit boundary right after the color
    // so a hex run that's not exactly 3 or 6 digits fails outright.
    public static readonly Rule ValueColorHex = AllOf(
        Grapheme('#'),
        FirstOf(
            BetweenInclusive(6, 6, OneOf(HexDigitChars)),
            BetweenInclusive(3, 3, OneOf(HexDigitChars))
        ),
        Peek(Not(OneOf(HexDigitChars)))
    );

    // rgba(int, int, int, float) with whitespace anywhere between pieces.
    public static readonly Rule ValueRgba = AllOf(
        Literal("rgba"),
        Grapheme('('), CssWhitespace, Integer(), CssWhitespace,
        Grapheme(','), CssWhitespace, Integer(), CssWhitespace,
        Grapheme(','), CssWhitespace, Integer(), CssWhitespace,
        Grapheme(','), CssWhitespace, Float(), CssWhitespace,
        Grapheme(')')
    );

    // Float before Integer: Integer would match the lead of a Float and
    // commit, leaving ".<digits>" behind.
    public static readonly Rule ValueNumber = FirstOf(Float(), Integer());

    // number <unit> | "0". The bare zero branch lets an unquoted unitless
    // zero ("margin: 0;") parse without a unit.
    public static readonly Rule LengthValue = FirstOf(
        AllOf(
            ValueNumber,
            FirstOf(
                Literal("px"),
                Literal("pt"),
                Literal("%"),
                Literal("em")
            )
        ),
        Grapheme('0')
    );

    // colorHex | rgba | url | length | number | string | identifier.
    // Each branch starts with a distinguishing prefix (#, r, u, digit/-,
    // ", letter/_) so first-match-wins lands on the right arm.
    public static readonly Rule DeclarationValue = FirstOf(
        ValueColorHex,
        ValueRgba,
        ValueUrl,
        LengthValue,
        ValueNumber,
        ValueString,
        Identifier
    );

    // Optional [property ":" value-list] followed by a terminating ";".
    // The declaration body is optional so an empty ";" still parses,
    // matching the C++ grammar's OptionalExpression wrapper around the
    // property:value part.
    public static readonly Rule Declaration = AllOf(
        Optional(AllOf(
            Identifier,
            CssWhitespace,
            Grapheme(':'),
            CssWhitespace,
            OneOrMore(AllOf(
                DeclarationValue,
                CssWhitespace,
                Optional(AllOf(Grapheme(','), CssWhitespace))
            ))
        )),
        Grapheme(';')
    );

    // selector-list { declaration; declaration; ... }
    public static readonly Rule CssRule = AllOf(
        SelectorList,
        CssWhitespace,
        Grapheme('{'),
        CssWhitespace,
        ZeroOrMore(AllOf(CssWhitespace, Declaration)),
        CssWhitespace,
        Grapheme('}')
    );

    public static readonly Rule Document = AllOf(
        ZeroOrMore(AllOf(CssWhitespace, CssRule)),
        CssWhitespace,
        Eof()
    );
}
