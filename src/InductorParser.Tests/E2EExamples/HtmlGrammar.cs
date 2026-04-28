using InductorParser;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// HTML grammar: PEG port of the C++ InductorParser HTML parser in
// src/FXPlatform/Languages/HtmlParser.h. Covers a pragmatic subset of
// HTML: tag names, attributes (four flavors: double-quoted, single-quoted,
// unquoted, and empty), start tags, void start tags, end tags, XML-style
// processing instructions, HTML comments, the non-replaceable
// <style>...</style> block, normal character elements, and the top-level
// document.
//
// Recursion. ElementRule references NormalCharacterElementRule which
// references ElementRule, a cycle C# static initialization can't resolve
// by itself. LateBoundRule stands in for Element during construction,
// then gets bound to the finished FirstOf(...) at the bottom of the file via
// a static initializer field.
//
// Illegal-character sets mirror HtmlParser.cpp line-for-line:
//
//   AttributeNameIllegalCharacters      = "\r\n\t \"'>/="
//   UnquotedAttributeIllegalCharacters  = "\r\n\t \"'<>/=`"
//   DoubleQuotedAttributeIllegalChars   = "\""
//   SingleQuotedAttributeIllegalChars   = "'"
public static class HtmlGrammar
{
    // Same ASCII-only choice as CssGrammar: the C++ WhitespaceSymbol
    // matches "\r\n\t " only, not the full Unicode whitespace class.
    private static readonly RuneSet WhitespaceChars = RuneSet.Ascii.Whitespace;

    private static readonly RuneSet LetterOrDigitChars =
        RuneSet.Ascii.Letters | RuneSet.Ascii.Digits;

    private static readonly Rule OptionalWs =
        ZeroOrMore(OneOf(WhitespaceChars));

    // TagName. Deliberate deviation from the C++ port. HtmlParser.h
    // restricts tag names to ASCII alphanumerics, which locks out common
    // XML tag-name patterns like "breakfast_menu", "my-element", and
    // "ns.name". Widen to the XML-spec-shaped subset:
    //
    //   Start char:    letter or '_'
    //   Continue char: letter, digit, '_', '-', '.'
    //
    // HTML in the wild never uses these characters in a tag name, so
    // every HTML document that parsed under the narrow form still parses
    // under the wider form. The win is that XmlGrammarTests can use
    // realistic XML fixtures instead of alphanumeric-only placeholders.
    private static readonly RuneSet TagNameStartChars =
        RuneSet.Ascii.Letters | RuneSet.Runes("_");
    private static readonly RuneSet TagNameContinueChars =
        RuneSet.Ascii.Letters | RuneSet.Ascii.Digits | RuneSet.Runes("_-.");

    public static readonly Rule TagName = AllOf(
        OneOf(TagNameStartChars),
        ZeroOrMore(OneOf(TagNameContinueChars))
    );

    // Attribute name: one or more characters that aren't whitespace, ", ',
    // >, /, =, or a control character. The null-byte and control-char
    // rules in the spec aren't enforced here (the C++ parser doesn't
    // enforce them either). The visible ASCII excludes are what matters.
    public static readonly Rule AttributeName =
        OneOrMore(NoneOf("\r\n\t \"'>/="));

    // Empty attribute: just the name, no "=value".
    public static readonly Rule EmptyAttribute = AttributeName;

    // Unquoted value: one or more chars that aren't whitespace, ", ', <, >,
    // /, =, or `.
    public static readonly Rule UnquotedAttributeValueAttribute = AllOf(
        AttributeName,
        OptionalWs,
        Token('='),
        OptionalWs,
        OneOrMore(NoneOf("\r\n\t \"'<>/=`"))
    );

    public static readonly Rule SingleQuotedAttributeValueAttribute = AllOf(
        AttributeName,
        OptionalWs,
        Token('='),
        OptionalWs,
        Token('\''),
        StringBody(RuneSet.Runes("'")),
        Token('\'')
    );

    public static readonly Rule DoubleQuotedAttributeValueAttribute = AllOf(
        AttributeName,
        OptionalWs,
        Token('='),
        OptionalWs,
        Token('"'),
        StringBody(RuneSet.Runes("\"")),
        Token('"')
    );

    // Attribute. Try quoted first (they're the more specific prefix, since
    // an unquoted run would read past the quote), then unquoted, then empty.
    // The empty-attribute branch is a bare AttributeName, so it must be last
    // because every other attribute form also starts with an AttributeName.
    public static readonly Rule Attribute = FirstOf(
        DoubleQuotedAttributeValueAttribute,
        SingleQuotedAttributeValueAttribute,
        UnquotedAttributeValueAttribute,
        EmptyAttribute
    );

    // "<" TagName (ws Attribute)* ws ">"
    public static readonly Rule StartTag = AllOf(
        Token('<'),
        TagName,
        ZeroOrMore(AllOf(OptionalWs, Attribute)),
        OptionalWs,
        Token('>')
    );

    // "<" TagName (ws Attribute)* ws "/>"
    public static readonly Rule VoidStartTag = AllOf(
        Token('<'),
        TagName,
        ZeroOrMore(AllOf(OptionalWs, Attribute)),
        OptionalWs,
        Token('/'),
        Token('>')
    );

    // XML processing instruction: <?tagname attrs?>
    public static readonly Rule ProcessingInstruction = AllOf(
        Token('<'),
        Token('?'),
        TagName,
        ZeroOrMore(AllOf(OptionalWs, Attribute)),
        OptionalWs,
        Token('?'),
        Token('>')
    );

    // "</" TagName ws ">"
    public static readonly Rule EndTag = AllOf(
        Token('<'),
        Token('/'),
        TagName,
        OptionalWs,
        Token('>')
    );

    public static readonly Rule VoidElement = VoidStartTag;

    // <!-- anything but "-->" -->
    public static readonly Rule Comment = AllOf(
        Literal("<!--"),
        StringBody(Literal("-->")),
        Literal("-->")
    );

    // <style> is non-replaceable character data in the HTML syntax: no
    // element recursion, just "anything until </style>". Start tag is
    // a specific literal, end tag is the same. The body scans forward
    // on a rule-based stop.
    private static readonly Rule StartStyleTag = AllOf(
        Token('<'),
        Literal("style"),
        ZeroOrMore(AllOf(OptionalWs, Attribute)),
        OptionalWs,
        Token('>')
    );

    private static readonly Rule EndStyleTag = AllOf(
        Literal("</style"),
        OptionalWs,
        Token('>')
    );

    // Stopper is the minimal "</style" prefix, not the full EndStyleTag
    // rule. Two reasons:
    //
    //   * Semantics. EndStyleTag = "</style" OptionalWs ">". Using it as
    //     the stopper would only stop at a complete end tag, so content
    //     like "</styled" (where "</style" is followed by 'd') would pass
    //     through as body and the scan would keep going. The HTML spec
    //     says "</style" must be followed by space, ">", or "/". Anything
    //     else is ill-formed. Stopping at the prefix catches this. If the
    //     characters after don't form a valid end tag, EndStyleTag fails
    //     and the outer rule fails loudly at the right spot.
    //
    //   * Cost. A Literal stopper is one string-compare per rune in a
    //     peek transaction. EndStyleTag as a stopper would invoke a
    //     compound rule (literal + OptionalWs + Token) per rune, noticeably
    //     more work on the 99%-of-runes path where the stopper doesn't
    //     match.
    //
    // General pattern: StringBody's stopper is the shortest unambiguous
    // prefix of the terminator. The outer AllOf re-matches the full
    // terminator to consume it.
    public static readonly Rule NonReplaceableCharacterElement = AllOf(
        StartStyleTag,
        StringBody(Literal("</style")),
        EndStyleTag
    );

    // Element is recursive: NormalCharacterElement -> Element -> ...
    // Bind below at the bottom of the file.
    private static readonly LateBoundRule ElementForward = new LateBoundRule("element");
    public static readonly Rule Element = ElementForward;

    // Normal character element: StartTag (Element | (not-"<")+)* EndTag.
    // The inner FirstOf tries element recursion first. If we're not sitting
    // on a "<", the OneOrMore(not-"<") sweeps up a run of text instead.
    public static readonly Rule NormalCharacterElement = AllOf(
        StartTag,
        ZeroOrMore(FirstOf(
            ElementForward,
            OneOrMore(NoneOf("<"))
        )),
        EndTag
    );

    // Ordering: comment first (starts with "<!--"), then the specific
    // <style> block (starts with "<style"), then void (ends with "/>")
    // before normal (ends with ">"). PEG doesn't peek the end, so we
    // commit in source order. Void must come before normal because the
    // two share the same left prefix and only diverge at the closing
    // "/" vs ">".
    private static readonly Rule ElementDef = FirstOf(
        Comment,
        NonReplaceableCharacterElement,
        VoidElement,
        NormalCharacterElement
    );

    // Fire the late-binding at type-init time.
    private static readonly Rule _elementBinding = ElementForward.Bind(ElementDef);

    // Document: any number of leading whitespace or comments, then one
    // root element, optional trailing whitespace, EOF. DOCTYPE and BOM
    // aren't implemented here. The C++ version doesn't handle them
    // either.
    public static readonly Rule Document = AllOf(
        ZeroOrMore(FirstOf(
            OneOf(WhitespaceChars),
            Comment
        )),
        ElementForward,
        OptionalWs,
        Eof()
    );
}
