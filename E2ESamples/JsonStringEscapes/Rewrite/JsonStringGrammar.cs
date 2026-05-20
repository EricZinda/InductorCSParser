// InductorParser grammar for one JSON string literal (RFC 8259), with full
// escape handling and UTF-16 surrogate-pair validation.
//
// This is the InductorParser rewrite of serde_json's string-escape decoder
// (see ../Original/). The sample answers the backlog item "do some
// appbuilding to ensure it is natural to give good error messages": serde_json
// reports six distinct, positioned errors while parsing a string, and the
// exercise is to see how naturally a grammar can reproduce them. README.md
// walks through that, and the friction is collected in backlog items 0000a,
// 0000b, and 0000c.
//
// What it parses: ONE complete string literal, opening quote through closing
// quote. The grammar matches the structure AND rejects what RFC 8259 and
// serde_json reject:
//
//   "abc"                  ok
//   "\n\tA"                ok        (simple + BMP escapes)
//   "𝄞"         ok        (a surrogate pair -> U+1D11E)
//   "\uD834"               error     lone leading surrogate
//   "\uDD1E"               error     lone trailing surrogate
//   "\uD834\n"             error     leading surrogate not followed by \u
//   "abc                   error     EOF, missing closing quote
//   "a<TAB>b"              error     raw control character
//   "\x"                   error     invalid escape
//
// Five of serde_json's six string errors are grammar errors. The sixth,
// InvalidUnicodeCodePoint for a bare unpaired surrogate code unit sitting
// literally in the input, can't be a grammar rule: the surrogate block
// 0xD800..0xDFFF isn't expressible as a TokenSet (TokenSet.Range rejects
// surrogate endpoints, and the ~ operator strips the surrogate block out).
// JsonStringParser.cs handles that one as a post-parse check. See backlog
// item 0000a.
//
// Tree shape: JsonString -> Body -> a run of `text` and `escape` nodes in
// source order. The decoder in JsonStringParser.cs walks that.
//
// The existing E2EExamples/StringLiteralGrammars.cs has a JSON string grammar
// too, but it matches `\uXXXX` purely structurally (four hex digits, no
// surrogate logic) and emits no escape-specific message. This grammar is the
// "validate the surrogate pairing and say something useful" version.

using InductorParser;
using static InductorParser.Rules;

namespace JsonStringEscapes.Rewrite;

public static class JsonStringGrammar
{
    /// The root rule: a complete `"..."` literal.
    public static readonly Rule JsonString;

    /// The run of characters between the quotes. Children are `text` and
    /// `escape` nodes in source order.
    public static readonly Rule Body;

    /// One escape sequence. SourceText is the raw escape (`\n`, `A`, or a
    /// whole `𝄞` pair). A surrogate pair is one `escape` node.
    public static readonly Rule Escape;

    /// A maximal run of unescaped characters. SourceText is the raw run.
    public static readonly Rule Text;

    static JsonStringGrammar()
    {
        // ---- character classes -------------------------------------------
        var hexDigit = OneOf(TokenSet.Ascii.HexDigits);

        // Surrogate halves are spotted from the first two hex digits of a
        // \uXXXX, no arithmetic required:
        //   D800..DBFF  leading (high)  surrogate -> 'D' then 8 9 A B
        //   DC00..DFFF  trailing (low)  surrogate -> 'D' then C D E F
        var dDigit              = OneOf("Dd");
        var leadingSecondDigit  = OneOf("89ABab");
        var trailingSecondDigit = OneOf("CDEFcdef");
        var anySurrogateSecondDigit = OneOf("89ABCDEFabcdef");

        // C0 control characters. RFC 8259 requires these be escaped, never
        // written raw inside a string.
        var controlCharacter = TokenSet.Range(0x00, 0x1F);

        // ---- error messages ----------------------------------------------
        // serde_json's ErrorCode names are in README.md's mapping table. The
        // wording is the rewrite's own. The exercise only asks that the
        // position be right and the message point at the real problem.

        const string ControlCharacterMessage =
            "control character (U+0000-U+001F) found while parsing a string; " +
            "it must be written as a \\u escape";

        const string MissingClosingQuoteMessage =
            "EOF while parsing a string; the closing '\"' is missing";

        const string InvalidEscapeMessage =
            "invalid escape sequence; expected one of \\\" \\\\ \\/ \\b \\f " +
            "\\n \\r \\t or \\uXXXX";

        const string BadHexDigitsMessage =
            "invalid \\u escape; expected four hexadecimal digits";

        const string UnexpectedEndOfHexEscapeMessage =
            "unexpected end of hex escape; a \\uD800-\\uDBFF leading surrogate " +
            "must be followed immediately by a \\uDC00-\\uDFFF \\u escape";

        const string LoneLeadingSurrogateMessage =
            "lone surrogate in hex escape; a \\uD800-\\uDBFF leading surrogate " +
            "must be paired with a \\uDC00-\\uDFFF trailing surrogate";

        const string LoneTrailingSurrogateMessage =
            "lone surrogate in hex escape; a \\uDC00-\\uDFFF trailing surrogate " +
            "has no \\uD800-\\uDBFF leading surrogate before it";

        // ---- \uXXXX escape, surrogate-aware ------------------------------
        // Tried after the '\' and the 'u' have been consumed.
        //
        // A leading surrogate is only legal as the first half of a pair, so
        // the rule REQUIRES the trailing `\uDCxx` right after it. PEG has no
        // cut operator, so the commit comes from structure: bmpEscape below
        // explicitly excludes the surrogate block, so once `\uD8xx` is seen
        // there is no other branch it can fall through to.
        //
        // The mandatory trailing half carries the lone-surrogate message on
        // its own And. A composite anchors its .WithError at the deepest spot
        // its children reach, so when the second \u escape isn't a trailing
        // surrogate the message lands on the digit that gave it away, and
        // beats the mechanical failure sitting at the same depth.
        var trailingSurrogate = And(dDigit, trailingSecondDigit, hexDigit, hexDigit)
            .WithError(LoneLeadingSurrogateMessage);

        var leadingSurrogatePair = And(
            dDigit, leadingSecondDigit, hexDigit, hexDigit,        // \uD8xx
            Token('\\').WithError(UnexpectedEndOfHexEscapeMessage),
            Token('u').WithError(UnexpectedEndOfHexEscapeMessage),
            trailingSurrogate);                                    // \uDCxx, required

        // A trailing surrogate with nothing before it is always wrong. The
        // four hex digits all match (it's a well-formed-looking escape), so
        // there's no natural failure to hang a message on. AlwaysFails
        // supplies one.
        var loneTrailingSurrogate = And(
            dDigit, trailingSecondDigit, hexDigit, hexDigit,       // \uDCxx
            AlwaysFails(LoneTrailingSurrogateMessage));

        // A normal BMP escape. The Not() keeps it from swallowing a surrogate
        // (which must be handled by the two rules above), so a lone `\uD8xx`
        // can't quietly succeed as if it were an ordinary character.
        var bmpEscape = And(
            Not(And(dDigit, anySurrogateSecondDigit)),
            hexDigit, hexDigit, hexDigit, hexDigit);

        var unicodeEscapeTail = Or(leadingSurrogatePair, loneTrailingSurrogate, bmpEscape);

        // ---- escape sequence ---------------------------------------------
        var simpleEscape = OneOf("\"\\/bfnrt");

        // unicodeEscapeTail classifies the escape by consuming its digits
        // across three branches, so no single rule owns the four-hex-digit
        // run. This Peek is that missing owner: a non-consuming assertion
        // that four hex digits are present, carrying the one message for
        // when they aren't. A rule that does own its run (Exactly(4, ...))
        // carries its own .WithError fine. The run here is just split.
        var unicodeEscape = And(
            Token('u'),
            Peek(Exactly(4, hexDigit)).WithError(BadHexDigitsMessage),
            unicodeEscapeTail);

        Escape = And(
                Token('\\'),
                Or(simpleEscape, unicodeEscape, AlwaysFails(InvalidEscapeMessage)))
            .As(nameof(Escape));

        // ---- the string body ---------------------------------------------
        // Anything that isn't a quote, a backslash, or a control character is
        // ordinary text. A bare unpaired surrogate code unit would belong in
        // this exclusion set too, but the surrogate block can't be written as
        // a TokenSet (see the header comment and backlog 0000a), so it's
        // left to a post-parse check in JsonStringParser.
        var literalCharacter = NoneOf(TokenSet.Runes("\"\\") | controlCharacter);

        Text = OneOrMore(literalCharacter).As(nameof(Text));
        Body = ZeroOrMore(Or(Escape, Text)).As(nameof(Body));

        // ---- the whole literal -------------------------------------------
        // After the body, exactly one of these is true: the next token is the
        // closing quote, it's a control character, or the input ended. The
        // Not() check turns the control character into its own message, and
        // the closing Token('"') covers the EOF case. A body that stopped on
        // a malformed escape fails deeper inside the escape, so that message
        // wins on depth and these two never fire.
        JsonString = And(
                Token('"'),
                Body,
                Not(OneOf(controlCharacter)).WithError(ControlCharacterMessage),
                Token('"').WithError(MissingClosingQuoteMessage))
            .As(nameof(JsonString));

        // serde_json passes string content through without Unicode
        // normalization, so this grammar opts out too. Compile(null) is also
        // what keeps a bare surrogate code unit in the input from throwing
        // during input normalization: with no normalization the lexer just
        // tokenizes it, the parse succeeds, and the post-parse check in
        // JsonStringParser reports it.
        JsonString.Compile(null);
    }

    // "This position is always an error." InductorParser has no Fail() or
    // Expected() leaf, so an unconditionally-failing rule is built from
    // Not() of a rule that always succeeds. Optional(AnyToken()) always
    // succeeds, so Not() of it always fails, and Not records its failure
    // (carrying this message) at its own start position. See backlog 0000b.
    private static Rule AlwaysFails(string message) =>
        Not(Optional(AnyToken())).WithError(message);
}
