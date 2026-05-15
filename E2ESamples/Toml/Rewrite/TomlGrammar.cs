using System.Text;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.E2ESamples.Toml.Rewrite;

// InductorParser grammar for TOML 1.0 (https://toml.io/en/v1.0.0).
// The named rules below mirror the production names in the TOML ABNF
// (Original/toml-1.0.0.abnf). Rules that produce values for the AST
// consumer use .As("name") so they survive flattening as
// dispatchable nodes; structural noise (whitespace, separators,
// brackets) keeps the default Delete / Flatten behavior and never
// appears in the tree.
//
// Side-by-side: how TOML's ABNF translates to InductorParser. The
// left column is verbatim from Original/toml-1.0.0.abnf; the right
// column is the shape this file uses.
//
//   ABNF                                                   InductorParser
//   -----------------------------------------------------  --------------------------------------------------------------------------
//   wschar = %x20 / %x09                                   whitespaceChar = TokenSet.Runes(" \t")
//   ws = *wschar                                           whitespace = ZeroOrMore(OneOf(whitespaceChar)).Flatten(Delete)
//   newline = %x0A / %x0D.0A                               Or(Literal("\r\n"), Token('\n'))
//   comment = "#" *non-eol                                 And(Token('#'), ScanUntil(TokenSet.LineTerminators))
//   unquoted-key = 1*(ALPHA / DIGIT / "-" / "_")           ScanWhile(Letters | Digits | Runes("-_"))
//   keyval = key keyval-sep val                            And(Key, whitespace, Token('='), whitespace, valueLateBound)
//   key = simple-key / dotted-key                          Or(DottedKey, SimpleKey)  // dotted first so a.b.c isn't truncated
//   dotted-key = simple-key 1*(dot-sep simple-key)         And(SimpleKey, OneOrMore(And(dotSeparator, SimpleKey)))
//   basic-string = quotation-mark *basic-char "            And(Token('"'), basicStringBody, Token('"').WithError(...))
//   basic-char = basic-unescaped / escaped                 Or(ScanWhile(basicUnescaped), basicEscape)
//   escaped = escape escape-seq-char                       And(Token('\\'), Or(simpleEscapeChar, ...))
//   ml-basic-string-delim = 3quotation-mark                Literal("\"\"\"")
//   hex-int = "0x" HEXDIG *(HEXDIG / "_" HEXDIG)           And(Literal("0x"), hexadecimalDigit, ZeroOrMore(...))
//   special-float = [sign] (inf / nan)                     And(Optional(sign), Or(Literal("inf"), Literal("nan")))
//   array = "[" [array-values] ws-comment-newline "]"      And(Token('['), Optional(arrayValues), whitespaceCommentNewline, Token(']'))
//   std-table = "[" ws key ws "]"                          And(Token('['), whitespace, Key, whitespace, Token(']').WithError(...))
//   array-table = "[[" ws key ws "]]"                      And(Literal("[["), whitespace, Key, whitespace, Literal("]]").WithError(...))
//   toml = expression *(newline expression)                And(expression, ZeroOrMore(And(newline, expression)), Eof())
//
// Patterns the table demonstrates:
//   * Sequence in ABNF (juxtaposition) becomes And(...).
//   * Alternation (/) becomes Or(...) — but ordered, not first-match-of-equal-alternatives.
//   * Repetition: *X is ZeroOrMore(X), 1*X is OneOrMore(X), nX is Exactly(n, X).
//   * Optional [X] is Optional(X).
//   * Character class %x20-7E / non-ascii becomes a TokenSet built with operators (| union, & intersect, ~ complement).
//   * Multi-char terminals like "0x" become Literal("0x"); single-char terminals are Token('x').
//   * The dispatch-name + visibility-in-tree story (".As(name)" auto-flips the default flatten policy to Preserve)
//     has no ABNF analog; it's how the consumer-side code finds nodes after the parse runs. ABNF productions get
//     .As("name") whenever the AST consumer needs to dispatch on "this Symbol came from the X production";
//     structural rules don't.
//
// A few places where we deviate from the ABNF for InductorParser-friendly
// shapes:
//   * Strings use OneOrMore(OneOf(...)) over the unescaped character class
//     instead of ScanUntil. The ABNF defines the allowed body as a strict
//     character class (basic-unescaped / mlb-unescaped) and using OneOf
//     against that class rejects control characters at parse time, which
//     ScanUntil(TokenSet.Runes("\"\\")) would silently let through.
//   * The val ordered choice arranges the alternatives by decreasing
//     specificity so date-time wins over integer ("1979-05-27") and float
//     wins over integer ("3.14"), matching how the spec prose talks about
//     "longest valid match wins".
//   * Comment / blank-line handling at the top level happens through the
//     three forms of `expression` from the ABNF. The Or order is
//     keyval-line, table-line, blank-or-comment-line so the grammar
//     commits to a value early and only falls through to the noise case.
public static class TomlGrammar
{
    // The compiled root.
    public static readonly Rule TomlDocument;

    // Keys. Named so the AST consumer can read "this part of the parse is
    // the key half of a keyval, not the value half".
    public static readonly Rule UnquotedKey;
    public static readonly Rule QuotedKey;
    public static readonly Rule SimpleKey;
    public static readonly Rule DottedKey;
    public static readonly Rule Key;

    // Top-level structural pieces.
    public static readonly Rule KeyValue;
    public static readonly Rule StandardTable;
    public static readonly Rule ArrayTable;
    public static readonly Rule Table;

    // String values.
    public static readonly Rule BasicString;
    public static readonly Rule LiteralString;
    public static readonly Rule MultiLineBasicString;
    public static readonly Rule MultiLineLiteralString;

    // Boolean values.
    public static readonly Rule TomlTrue;
    public static readonly Rule TomlFalse;

    // Numeric values.
    public static readonly Rule HexadecimalInteger;
    public static readonly Rule OctalInteger;
    public static readonly Rule BinaryInteger;
    public static readonly Rule DecimalInteger;
    public static readonly Rule TomlFloat;
    public static readonly Rule SpecialFloat;

    // Date-time values.
    public static readonly Rule OffsetDateTime;
    public static readonly Rule LocalDateTime;
    public static readonly Rule LocalDate;
    public static readonly Rule LocalTime;

    // Composite values.
    public static readonly Rule TomlArray;
    public static readonly Rule InlineTable;
    public static readonly Rule InlineTableKeyValue;

    // The value Or branch, exposed so a consumer can dispatch on
    // "this Symbol was produced as a TOML value of some kind".
    public static readonly Rule Value;

    static TomlGrammar()
    {
        // ---------------------------------------------------------
        // Whitespace, newlines, and comments
        // ---------------------------------------------------------
        // ABNF: wschar = SP / HT
        // ABNF: ws = *wschar
        // ABNF: newline = LF / CRLF
        // The built-in EndOfLine() factory also accepts bare CR, NEL,
        // LS, PS; TOML's ABNF allows only LF and CRLF, so define a
        // strict newline locally and use it everywhere a line break is
        // required.
        var whitespaceChar = TokenSet.Runes(" \t");
        var whitespace = ZeroOrMore(OneOf(whitespaceChar)).Flatten(FlattenType.Delete);
        var newline = Or(Literal("\r\n"), Token('\n')).Flatten(FlattenType.Delete);

        // Comment body excludes line terminators. The ABNF allows HT,
        // %x20-7E, and non-ASCII (excluding the surrogate range).
        var nonAsciiBody =
            TokenSet.Range(0x80, 0xD7FF) | TokenSet.Range(0xE000, 0x10FFFF);
        // Stop set for the comment body: only CR and LF. TOML's ABNF
        // doesn't accept NEL/LS/PS or bare VT/FF as line terminators,
        // so this set is narrower than TokenSet.LineTerminators.
        // eofIsTerminator: true lets a final comment without a
        // trailing newline still terminate cleanly.
        //
        // The CRLF cluster has to be a member as well, not just the
        // bare CR and LF runes. UAX #29 GB3 keeps CR followed by LF
        // glued into one grapheme cluster, and ScanUntil tests its
        // stop set against the next whole token (cluster). A set with
        // only the single runes never matches the two-char "\r\n"
        // token, so on a CRLF-terminated document the comment body
        // would scan straight past every line ending and swallow the
        // rest of the file. Graphemes("\r\n") adds the cluster so the
        // scan stops at the line ending whether the input uses LF or
        // CRLF. The bare CR and LF runes still cover a lone CR and a
        // lone LF.
        var tomlLineTerminator =
            TokenSet.Single('\r') | TokenSet.Single('\n') | TokenSet.Graphemes("\r\n");
        var comment = And(Token('#'),
                ScanUntil(tomlLineTerminator, eofIsTerminator: true).As("commentBody"))
            .As("comment");

        // ws-comment-newline: any mix of inline whitespace, comments, and
        // newlines. Used inside arrays where line breaks are legal mid-value.
        var whitespaceCommentNewline = ZeroOrMore(Or(
            OneOf(whitespaceChar),
            And(Optional(comment), newline)
        )).Flatten(FlattenType.Delete);

        // ---------------------------------------------------------
        // Keys
        // ---------------------------------------------------------
        // unquoted-key = 1*( ALPHA / DIGIT / "-" / "_" )
        var unquotedKeyChars = TokenSet.Ascii.Letters | TokenSet.Ascii.Digits | TokenSet.Runes("-_");
        UnquotedKey = ScanWhile(unquotedKeyChars).As("unquotedKey");

        // quoted-key = basic-string / literal-string
        // Forward-declare; the actual string rules are defined below.
        var basicStringLateBound = new LateBoundRule("basicString");
        var literalStringLateBound = new LateBoundRule("literalString");
        QuotedKey = Or(basicStringLateBound, literalStringLateBound).As("quotedKey");

        // simple-key = quoted-key / unquoted-key
        SimpleKey = Or(QuotedKey, UnquotedKey).As("simpleKey");

        // dot-sep = ws "." ws
        var dotSeparator = And(whitespace, Token('.'), whitespace);

        // dotted-key = simple-key 1*( dot-sep simple-key )
        DottedKey = And(
            SimpleKey,
            OneOrMore(And(dotSeparator, SimpleKey))
        ).As("dottedKey");

        // key = dotted-key / simple-key  (try dotted first so a key like
        // "a.b.c" doesn't get truncated to just "a")
        Key = Or(DottedKey, SimpleKey).As("key");

        // ---------------------------------------------------------
        // String values
        // ---------------------------------------------------------
        // basic-unescaped = wschar / %x21 / %x23-5B / %x5D-7E / non-ascii
        // (i.e. printable ASCII minus '"' and '\', plus tab/space and
        // most non-ASCII Unicode)
        var basicUnescaped =
            TokenSet.Runes(" \t")
            | TokenSet.Single(0x21)
            | TokenSet.Range(0x23, 0x5B)
            | TokenSet.Range(0x5D, 0x7E)
            | nonAsciiBody;

        // escape-seq-char = " | \ | b | f | n | r | t | uXXXX | UXXXXXXXX
        var hexadecimalDigit = OneOf(TokenSet.Ascii.HexDigits);
        var simpleEscapeChar = OneOf(TokenSet.Runes("\"\\bfnrt"));
        var unicodeShortEscape = And(Token('u'), hexadecimalDigit, hexadecimalDigit, hexadecimalDigit, hexadecimalDigit);
        var unicodeLongEscape = And(Token('U'),
            hexadecimalDigit, hexadecimalDigit, hexadecimalDigit, hexadecimalDigit,
            hexadecimalDigit, hexadecimalDigit, hexadecimalDigit, hexadecimalDigit);
        // Token('\\') is Delete by default. That's fine here: the
        // basicStringBody consumer (DecodeBasicStringBody) recovers
        // the verbatim body text via ParseResult.RawSourceTextOf, which
        // includes the backslash because it's a section of the original
        // input — independent of which children survived flattening.
        var basicEscape = And(
            Token('\\'),
            Or(simpleEscapeChar, unicodeShortEscape, unicodeLongEscape)
        );

        // basic-string = " *basic-char " — body is OneOrMore so the empty
        // string is handled by the surrounding Optional.
        var basicStringBody = ZeroOrMore(Or(
            ScanWhile(basicUnescaped),
            basicEscape
        )).As("basicStringBody");
        BasicString = And(Token('"'), basicStringBody, Token('"').WithError("Expected closing '\"' to end basic string"))
            .As("basicString");
        basicStringLateBound.Bind(BasicString);

        // literal-string = ' *literal-char '
        // literal-char = HT / %x20-26 / %x28-7E / non-ascii  (anything
        // except ' and most control chars)
        var literalChar =
            TokenSet.Single('\t')
            | TokenSet.Range(0x20, 0x26)
            | TokenSet.Range(0x28, 0x7E)
            | nonAsciiBody;
        // *literal-char: zero or more body chars. ScanWhile with
        // minimumCount: 0 always emits one leaf, so the empty-string
        // case '' gives a zero-width body leaf rather than a missing
        // child, and the consumer can read Children[0].ToString()
        // unconditionally.
        var literalStringBody = ScanWhile(literalChar, minimumCount: 0)
            .As("literalStringBody");
        LiteralString = And(Token('\''), literalStringBody, Token('\'').WithError("Expected closing \"'\" to end literal string"))
            .As("literalString");
        literalStringLateBound.Bind(LiteralString);

        // ml-basic-string body: any mix of allowed chars, escapes,
        // newlines, and "line-ending backslash" continuations. The
        // delimiter is """ but up to two trailing quotes inside the body
        // are allowed (mlb-quotes) before the final delim. We use a
        // Not-lookahead to stop the body at the first occurrence of three
        // consecutive quotes.
        //
        // mlb-escaped-nl = "\" ws newline *( wschar / newline )
        // (a backslash at the end of a line, possibly followed by trailing
        // whitespace, eats the newline and following blank lines)
        var multiLineBasicEscapedNewline = And(
            Token('\\'),
            whitespace,
            newline,
            ZeroOrMore(Or(OneOf(whitespaceChar), newline))
        );

        // For multiline body chars we need to allow newlines, but not have
        // ScanWhile cross a """ boundary. The simplest correct shape:
        // ZeroOrMore(Not("""), mlb-content). The strict TOML newline
        // (LF or CRLF only) sits inside the Or; the body consumer
        // recovers the verbatim text via Symbol.SourceText, so no
        // Preserve hack is needed on the newline.
        var threeQuotes = Literal("\"\"\"");
        var multiLineBasicBodyChar = And(
            Not(threeQuotes),
            Or(
                multiLineBasicEscapedNewline,
                basicEscape,
                newline,
                OneOf(basicUnescaped | TokenSet.Single('"'))
            )
        );
        var multiLineBasicStringBody = ZeroOrMore(multiLineBasicBodyChar)
            .As("multiLineBasicStringBody");
        MultiLineBasicString = And(
            threeQuotes,
            Optional(newline),
            multiLineBasicStringBody,
            threeQuotes.WithError("Expected closing '\"\"\"' to end multi-line basic string")
        ).As("multiLineBasicString");

        // ml-literal-string: same shape, single-quote delim, no escapes.
        var threeApostrophes = Literal("'''");
        var multiLineLiteralBodyChar = And(
            Not(threeApostrophes),
            Or(
                newline,
                OneOf(literalChar | TokenSet.Single('\''))
            )
        );
        var multiLineLiteralStringBody = ZeroOrMore(multiLineLiteralBodyChar)
            .As("multiLineLiteralStringBody");
        MultiLineLiteralString = And(
            threeApostrophes,
            Optional(newline),
            multiLineLiteralStringBody,
            threeApostrophes.WithError("Expected closing \"'''\" to end multi-line literal string")
        ).As("multiLineLiteralString");

        // ---------------------------------------------------------
        // Boolean values
        // ---------------------------------------------------------
        TomlTrue = Literal("true").As("true");
        TomlFalse = Literal("false").As("false");

        // ---------------------------------------------------------
        // Integer values
        // ---------------------------------------------------------
        var sign = OneOf("+-").Preserve();

        // unsigned-dec-int: one digit, OR a 1-9 digit followed by
        // (digit | underscore-digit) repeats. Underscores can't be
        // adjacent and can't be leading or trailing.
        var digit = OneOf(TokenSet.Ascii.Digits);
        var digitOneToNine = OneOf(TokenSet.Range('1', '9'));
        var underscoreDigit = And(Token('_'), digit);
        var unsignedDecimalInteger = Or(
            And(digitOneToNine, OneOrMore(Or(digit, underscoreDigit))),
            digit
        );
        DecimalInteger = And(Optional(sign), unsignedDecimalInteger).As("decimalInteger");

        // hex-int = "0x" HEXDIG *( HEXDIG / "_" HEXDIG )
        var hexadecimalUnderscore = And(Token('_'), hexadecimalDigit);
        HexadecimalInteger = And(Literal("0x"), hexadecimalDigit, ZeroOrMore(Or(hexadecimalDigit, hexadecimalUnderscore)))
            .As("hexadecimalInteger");

        var octalDigit = OneOf(TokenSet.Range('0', '7'));
        var octalUnderscore = And(Token('_'), octalDigit);
        OctalInteger = And(Literal("0o"), octalDigit, ZeroOrMore(Or(octalDigit, octalUnderscore)))
            .As("octalInteger");

        var binaryDigit = OneOf(TokenSet.Runes("01"));
        var binaryUnderscore = And(Token('_'), binaryDigit);
        BinaryInteger = And(Literal("0b"), binaryDigit, ZeroOrMore(Or(binaryDigit, binaryUnderscore)))
            .As("binaryInteger");

        // ---------------------------------------------------------
        // Float values
        // ---------------------------------------------------------
        // zero-prefixable-int = DIGIT *( DIGIT / "_" DIGIT )
        // Punctuation tokens (the leading dot of fraction, the 'e' of
        // exponent, the underscore separators) are Delete by default.
        // ProjectFloat recovers the verbatim float text via
        // ParseResult.RawSourceTextOf and hands it to double.Parse, so
        // we don't need to .Preserve() the punctuation here just to
        // keep it visible in ToString.
        var zeroPrefixableInteger = And(digit, ZeroOrMore(Or(digit, underscoreDigit)));
        var fraction = And(Token('.'), zeroPrefixableInteger);
        var floatExponentPart = And(Optional(sign), zeroPrefixableInteger);
        var exponent = And(OneOf("eE"), floatExponentPart);

        // float = float-int-part ( exp / frac [ exp ] )
        // i.e. a sign+integer-part followed by EITHER an exponent, OR a
        // fraction (with optional exponent). At least one of fraction /
        // exponent is required to distinguish from a plain integer.
        var floatIntegerPart = And(Optional(sign), unsignedDecimalInteger);
        var ordinaryFloat = And(
            floatIntegerPart,
            Or(
                And(fraction, Optional(exponent)),
                exponent
            )
        );

        // special-float = [sign] (inf | nan). Literal is Delete by
        // factory; ProjectFloat dispatches on the SpecialFloat node's
        // RawSourceTextOf (which includes the "inf"/"nan" mnemonic
        // because it's a section of the original input) so we don't
        // need to .Preserve() the keyword here.
        var infinityOrNan = Or(Literal("inf"), Literal("nan"));
        SpecialFloat = And(Optional(sign), infinityOrNan).As("specialFloat");

        TomlFloat = Or(SpecialFloat, ordinaryFloat.As("ordinaryFloat"))
            .As("float");

        // ---------------------------------------------------------
        // Date-time values
        // ---------------------------------------------------------
        // Per RFC 3339 and TOML 1.0. The structural punctuation
        // (-, :, .) is Delete by default; the four date/time
        // projection helpers in TomlParser recover the verbatim text
        // via RawSourceTextOf and hand it to DateTimeOffset.Parse /
        // DateTime.Parse / etc., so the punctuation doesn't need to
        // appear in ToString.
        var dash = Token('-');
        var colon = Token(':');
        var twoDigit = And(digit, digit);
        var fourDigit = And(digit, digit, digit, digit);
        var fullDate = And(fourDigit, dash, twoDigit, dash, twoDigit);
        var timeSecondFraction = And(Token('.'), OneOrMore(digit));
        var partialTime = And(twoDigit, colon, twoDigit, colon, twoDigit, Optional(timeSecondFraction));
        var timeNumericOffset = And(OneOf("+-"), twoDigit, colon, twoDigit);
        var timeOffset = Or(OneOf("Zz"), timeNumericOffset);
        var timeDelimiter = OneOf("Tt ");

        OffsetDateTime = And(fullDate, timeDelimiter, partialTime, timeOffset)
            .As("offsetDateTime");
        LocalDateTime = And(fullDate, timeDelimiter, partialTime)
            .As("localDateTime");
        // fullDate and partialTime are reused inside OffsetDateTime and
        // LocalDateTime. AliasedAs wraps the shared shape with a new
        // identity that only carries the "localDate" / "localTime" name
        // at the standalone-value position. The inner shape stays
        // anonymous everywhere else, so FindAll("localDate") on a tree
        // containing OffsetDateTime nodes doesn't return false-positive
        // date subtrees.
        LocalDate = fullDate.AliasedAs("localDate");
        LocalTime = partialTime.AliasedAs("localTime");

        // ---------------------------------------------------------
        // Composite values: array, inline-table
        // ---------------------------------------------------------
        // val needs to be late-bound because arrays and inline tables
        // contain values, which can be arrays / inline tables, etc.
        var valueLateBound = new LateBoundRule("value");

        // array = "[" [ array-values ] ws-comment-newline "]"
        // array-values =  ws-comment-newline val ws-comment-newline ","
        //                 array-values
        //              /  ws-comment-newline val ws-comment-newline [ "," ]
        // (i.e. comma-separated values with optional trailing comma, with
        // newlines and comments allowed between everything)
        var arrayValues = And(
            whitespaceCommentNewline,
            valueLateBound,
            ZeroOrMore(And(
                whitespaceCommentNewline,
                Token(','),
                whitespaceCommentNewline,
                valueLateBound
            )),
            Optional(And(whitespaceCommentNewline, Token(',')))
        );
        TomlArray = And(
            Token('['),
            Optional(arrayValues),
            whitespaceCommentNewline,
            Token(']').WithError("Expected ',' or ']' inside array")
        ).As("array");

        // inline-table = "{" [ inline-table-keyvals ] "}"
        // inline-table-sep = ws "," ws  (note: no newlines allowed inside
        // inline tables per TOML 1.0)
        // inline-table-keyvals = keyval [ inline-table-sep inline-table-keyvals ]
        var keyValueLateBound = new LateBoundRule("keyValue");
        InlineTableKeyValue = keyValueLateBound;
        var inlineTableKeyValues = And(
            keyValueLateBound,
            ZeroOrMore(And(whitespace, Token(','), whitespace, keyValueLateBound))
        );
        InlineTable = And(
            Token('{'),
            whitespace,
            Optional(inlineTableKeyValues),
            whitespace,
            Token('}').WithError("Expected ',' or '}' inside inline table")
        ).As("inlineTable");

        // ---------------------------------------------------------
        // Value choice — order matters
        // ---------------------------------------------------------
        // Strings first (delim-based, no ambiguity with the rest).
        // Then booleans (literal "true"/"false").
        // Then array / inline-table (delim-based).
        // Then date-time before float before integer because they share
        // digit prefixes.
        Value = Or(
            MultiLineBasicString,
            BasicString,
            MultiLineLiteralString,
            LiteralString,
            TomlTrue,
            TomlFalse,
            TomlArray,
            InlineTable,
            // Order within date-times: most-specific first.
            // (4-digit-year + delim + time + offset)  before  (4-digit-year + delim + time)
            // before  (4-digit-year alone)  before  (2-digit hours + ":" + ...).
            OffsetDateTime,
            LocalDateTime,
            LocalDate,
            LocalTime,
            TomlFloat,
            // Integer last; non-decimal forms before decimal so "0x..." doesn't
            // get truncated to integer 0.
            HexadecimalInteger,
            OctalInteger,
            BinaryInteger,
            DecimalInteger
        ).As("value");
        valueLateBound.Bind(Value);

        // ---------------------------------------------------------
        // Key / value pair
        // ---------------------------------------------------------
        // keyval = key keyval-sep val
        // keyval-sep = ws "=" ws
        KeyValue = And(
            Key,
            whitespace,
            Token('=').WithError("Expected '=' after key"),
            whitespace,
            valueLateBound
        ).As("keyValue");
        keyValueLateBound.Bind(KeyValue);

        // ---------------------------------------------------------
        // Table headers
        // ---------------------------------------------------------
        // std-table = "[" ws key ws "]"
        StandardTable = And(
            Token('['),
            whitespace,
            Key,
            whitespace,
            Token(']').WithError("Expected ']' to close table header")
        ).As("standardTable");

        // array-table = "[[" ws key ws "]]"
        ArrayTable = And(
            Literal("[["),
            whitespace,
            Key,
            whitespace,
            Literal("]]").WithError("Expected ']]' to close array-of-tables header")
        ).As("arrayTable");

        // table = array-table / std-table
        // (Try array-table first because "[[" must win over "[")
        Table = Or(ArrayTable, StandardTable).As("table");

        // ---------------------------------------------------------
        // Top-level expression and document
        // ---------------------------------------------------------
        // expression =  ws [ comment ]
        // expression =/ ws keyval ws [ comment ]
        // expression =/ ws table ws [ comment ]
        //
        // Each branch ends with a Peek that the next character is a
        // line break or EOF. This forces every branch to consume the
        // whole logical line, so the third (whitespace + optional
        // comment) branch can't silently absorb a malformed line that
        // a more-specific branch tried and rejected. Without this
        // anchor, the third branch matches with zero consumption on
        // any input (its Optional bodies always succeed), and Or's
        // commit-clears wipes the more-specific branches' WithError
        // records. With the anchor, branches fail when the line has
        // trailing garbage, the deeper WithError survives, and the
        // user sees the helpful message. See
        // docs/ErrorArchitecture.md.
        var endOfLogicalLine = Or(Peek(newline), Peek(Eof()));
        var expression = Or(
            And(whitespace, KeyValue, whitespace, Optional(comment), endOfLogicalLine),
            And(whitespace, Table, whitespace, Optional(comment), endOfLogicalLine),
            And(whitespace, Optional(comment), endOfLogicalLine)
        );

        // toml = expression *( newline expression )
        TomlDocument = And(
            expression,
            ZeroOrMore(And(newline, expression)),
            Optional(newline),
            Eof()
        );
        TomlDocument.Compile();
    }
}
