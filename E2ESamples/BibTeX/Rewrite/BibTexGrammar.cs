// Grammar for the BibTeX subset the Original/ parser handles,
// mirroring the bibtex-parser (https://github.com/digitalheir/bibtex-js-parser,
// MIT) and pybtex (https://pybtex.org, MIT) entry shape. Both upstreams
// use a character scanner with regex-driven entry recognition. This
// grammar replaces the scanner with a structural declaration.
//
//   document      ::= (strayText | entry)* EOF
//   strayText     ::= run of non-'@' runes (anything outside an entry
//                     is a comment in BibTeX)
//   entry         ::= regularEntry | preambleEntry | stringEntry
//   regularEntry  ::= '@' entryType '{' citationKey ',' fieldList '}'
//   preambleEntry ::= '@preamble' '{' fieldValue '}'
//   stringEntry   ::= '@string' '{' macroName '=' fieldValue '}'
//   entryType     ::= one or more ASCII letters (case-insensitive)
//   citationKey   ::= UAX #31 identifier + a small set of BibLaTeX
//                     additions (":.-+/")
//   macroName     ::= same shape as citationKey
//   fieldList     ::= (field (',' field)*)? ','?
//   field         ::= fieldName '=' fieldValue
//   fieldName     ::= one or more ASCII letters/digits/hyphens/underscores
//   fieldValue    ::= valuePart ('#' valuePart)*
//   valuePart     ::= quotedValue | bracedValue | integerValue | bareWord
//   bareWord      ::= macroName (an identifier reference like 'jul',
//                     'STOC', 'ACM' that biber expands at render time.
//                     The grammar keeps the literal name)
//   quotedValue   ::= '"' (escape | non-quote-non-backslash)* '"'
//   bracedValue   ::= '{' bracedContent '}'
//   bracedContent ::= (bracedValue | escape | non-brace-non-backslash)*
//
// The Unicode angle lives in the citation-key rule. A naive port (and
// the Original/) uses /[A-Za-z_][A-Za-z0-9_:.\-+/]*/, which silently
// rejects non-ASCII letters. Modern BibLaTeX (biber) accepts Unicode
// citation keys, so academic papers with Greek, Cyrillic, or Chinese
// author names benefit from Unicode citekeys like `gärtner2020` or
// `张2019`. Rules.Identifier(extraStartRunes: "_") gives UAX #31's
// XID_Start XID_Continue* shape, which the grammar then extends with
// the punctuation BibLaTeX allows in keys.
//
// Two structural notes:
//
// 1. bracedValue and bracedContent are mutually recursive: a braced
//    value's body can contain another braced value. The grammar wires
//    this via a LateBoundRule so bracedContent can mention bracedValue
//    before bracedValue is built.
// 2. The "stray text between entries" rule uses ScanWhile against the
//    complement of '@', and is Flatten(Delete) so the matched bytes
//    drop out of the tree. The Original handles the same case by
//    advancing _position past stray bytes. The grammar shapes it as a
//    no-emit run.

using System.Text;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace BibTexSample.Rewrite;

public static class BibTexGrammar
{
    public static readonly Rule EntryType;
    public static readonly Rule CitationKey;
    public static readonly Rule MacroName;
    public static readonly Rule FieldName;
    public static readonly Rule QuotedValue;
    public static readonly Rule BracedValue;
    public static readonly Rule IntegerValue;
    public static readonly Rule BareWord;
    public static readonly Rule FieldValue;
    public static readonly Rule Field;
    public static readonly Rule RegularEntry;
    public static readonly Rule PreambleEntry;
    public static readonly Rule StringEntry;
    public static readonly Rule Entry;
    public static readonly Rule Document;

    static BibTexGrammar()
    {
        EntryType = OneOrMore(OneOf(TokenSet.Ascii.Letters))
            .As("entryType");

        // Citation key: UAX #31 XID_Start + body, plus the BibLaTeX
        // extras ':', '.', '-', '+', '/'. The XID side picks up
        // 'gärtner2020', '张2019', 'καραμβάρης1998'. The extras pick up
        // the common 'author:year/page' shape some tools generate.
        var extraBody = TokenSet.Runes("_:.-+/");
        CitationKey = Rules.Identifier(extraStartRunes: TokenSet.Runes("_"), extraBodyRunes: extraBody)
            .As("citationKey");

        FieldName = OneOrMore(OneOf(TokenSet.Ascii.Letters | TokenSet.Ascii.Digits | TokenSet.Runes("_-")))
            .As("fieldName");

        // Quoted value: any rune except an unescaped '"'. Escape is
        // backslash followed by any one token. Stop set is the quote
        // and the backslash (backslash because we want to dispatch into
        // the escape branch).
        var anyTokenEscape = And(Token('\\'), AnyToken());
        var quotedBody = ScanUntil(
            stopAt: TokenSet.Runes("\""),
            escapeStart: new System.Text.Rune('\\'),
            escapeEnd: AnyToken()
        );
        QuotedValue = And(
            Token('"'),
            quotedBody,
            Token('"').WithError("expected closing '\"' on quoted field value")
        ).As("quotedValue");

        // Braced value (recursive): an outer '{' '}' pair whose body is
        // a sequence of plain runs, escape sequences, and nested braced
        // values. The recursion lets {Hello {nested} world} parse with
        // the inner braces as part of the value.
        var bracedValueForward = new LateBoundRule("bracedValue");

        // Plain run: any text up to the next '{', '}', or '\'. Stopping at
        // backslash lets the escape branch take over, and at '}' lets the
        // closing brace match.
        //
        // ScanUntil(stopAt), not ScanWhile(~Runes(...)). The two read as the
        // same intent ("a run of characters that aren't these"), but they
        // aren't. ~Runes("{}\\") is "any RUNE but those", and a complement can
        // only ever be runes. Complement is defined against the code-point
        // universe, which is finite and representable as intervals, but the
        // universe of grapheme clusters is unbounded (a grapheme is any rune
        // sequence that respects UAX #29 boundaries), so "every grapheme but
        // X" isn't a set you can write down. That's why TokenSet complements
        // rune-only sets and throws on ~ of a set that already holds a
        // multi-rune grapheme. See the header comment in
        // src/InductorParser/TokenSet.cs (the "Complement is only defined when
        // _multiRuneGraphemes is empty" note) for the full reasoning.
        //
        // The consequence here: a rune-set scan matches only single-rune
        // grapheme clusters, so ScanWhile(~Runes(...)) stops dead at a CRLF
        // (one character, two runes) and a braced value spanning a Windows
        // line break never reaches its '}'. ScanUntil instead tests each
        // character against the small stop set, so any character that isn't a
        // stopper, CRLF included, is consumed. ScanUntil succeeds zero-width
        // when already at a stopper. The ZeroOrMore in bracedContent counts
        // that once and then stops (see BetweenInclusiveRule), so the run
        // yields control at '}'.
        var bracedPlainRun = ScanUntil(stopAt: TokenSet.Runes("{}\\"));

        var bracedContent = ZeroOrMore(Or(
            bracedValueForward,
            anyTokenEscape,
            bracedPlainRun
        ));

        BracedValue = And(
            Token('{'),
            bracedContent,
            Token('}').WithError("expected closing '}' on braced value")
        ).As("bracedValue");

        bracedValueForward.Bind(BracedValue);

        IntegerValue = OneOrMore(OneOf(TokenSet.Ascii.Digits))
            .As("integerValue");

        // Macro name: same shape as a citation key, used both as the
        // name in @string{ name = ... } and as a bare value reference
        // in a field like 'month = jul' or 'organization = ACM'. Built
        // fresh because Rules.Identifier returns a fresh rule per call
        // and .As(name) is set-once.
        var macroIdentifier = Rules.Identifier(
            extraStartRunes: TokenSet.Runes("_"),
            extraBodyRunes: extraBody);
        MacroName = macroIdentifier.AliasedAs("macroName");

        var bareWordIdentifier = Rules.Identifier(
            extraStartRunes: TokenSet.Runes("_"),
            extraBodyRunes: extraBody);
        BareWord = bareWordIdentifier.AliasedAs("bareWord");

        // A value part is one of the four primitive shapes. Order in
        // Or matters: IntegerValue before BareWord because BareWord's
        // XID_Start excludes digits, but the integer alternative is
        // shorter and resolves faster on the common 'year = 1986'
        // case.
        var valuePart = Or(QuotedValue, BracedValue, IntegerValue, BareWord);

        // Field value: one or more value parts joined by '#'. The
        // join character is FlattenType.Delete so it drops out of the
        // tree. The reader concatenates the surviving parts' contents.
        var hash = Token('#').Flatten(FlattenType.Delete);
        FieldValue = And(
            valuePart,
            ZeroOrMore(And(
                Optional(AnyWhitespace()),
                hash,
                Optional(AnyWhitespace()),
                valuePart
            ))
        ).As("fieldValue");

        Field = And(
            FieldName,
            Optional(AnyWhitespace()),
            Token('=').Flatten(FlattenType.Delete).WithError("expected '=' after field name"),
            Optional(AnyWhitespace()),
            FieldValue
        ).As("field");

        // One shared comma rule for the silent field-separator and trailing
        // positions, and a fresh AliasRule at the citation-key position so
        // it can hold its own WithError without affecting the shared rule.
        // .WithError mutates the receiver, so wrapping in an alias is the
        // in-library way to attach a per-caller message to a shared rule
        // shape.
        var comma = Token(',').Flatten(FlattenType.Delete);
        var fieldList = Optional(And(
            Field,
            ZeroOrMore(And(
                Optional(AnyWhitespace()),
                comma,
                Optional(AnyWhitespace()),
                Field
            )),
            // BibTeX is permissive about a trailing comma.
            Optional(And(Optional(AnyWhitespace()), comma))
        ));

        // Entry types are case-insensitive ASCII keywords, but the
        // grammar dispatch is case-sensitive at the rule level. We
        // handle '@preamble' / '@PREAMBLE' / '@Preamble' by matching
        // the keyword with LiteralIgnoreAsciiCase, which accepts any
        // case combination.
        var atSign = Token('@').Flatten(FlattenType.Delete);
        var openBrace = Token('{').Flatten(FlattenType.Delete).WithError("expected '{' after entry type");
        var closeBrace = Token('}').Flatten(FlattenType.Delete).WithError("expected '}' to close entry");

        // @preamble{ value }: bibliography-scope macros, no citation
        // key. The body is just a value expression. The 'preamble'
        // keyword is matched but not surfaced as a tree node. The
        // reader hardcodes EntryType="preamble" when it sees this
        // shape, so the keyword text doesn't need to round-trip.
        var preambleKeyword = LiteralIgnoreAsciiCase("preamble");
        PreambleEntry = And(
            atSign,
            preambleKeyword,
            Optional(AnyWhitespace()),
            openBrace,
            Optional(AnyWhitespace()),
            FieldValue,
            Optional(AnyWhitespace()),
            closeBrace
        ).As("preambleEntry");

        // @string{ name = value }: macro definition, no citation key.
        // Same as @preamble: the keyword text isn't surfaced.
        var stringKeyword = LiteralIgnoreAsciiCase("string");
        StringEntry = And(
            atSign,
            stringKeyword,
            Optional(AnyWhitespace()),
            openBrace,
            Optional(AnyWhitespace()),
            MacroName.WithError("expected a macro name"),
            Optional(AnyWhitespace()),
            Token('=').Flatten(FlattenType.Delete).WithError("expected '=' after macro name"),
            Optional(AnyWhitespace()),
            FieldValue,
            Optional(AnyWhitespace()),
            closeBrace
        ).As("stringEntry");

        RegularEntry = And(
            atSign,
            EntryType,
            Optional(AnyWhitespace()),
            openBrace,
            Optional(AnyWhitespace()),
            CitationKey.WithError("expected a citation key"),
            Optional(AnyWhitespace()),
            Alias(comma).WithError("expected ',' after citation key"),
            Optional(AnyWhitespace()),
            fieldList,
            Optional(AnyWhitespace()),
            closeBrace
        ).As("regularEntry");

        // Entry-shape dispatch. PreambleEntry and StringEntry are
        // tried before RegularEntry because they share the '@'
        // prefix. First-match-wins in Or backtracks on inner failure,
        // so a RegularEntry-shaped input still parses if its type
        // isn't preamble/string. The Or is named so a tree walker
        // can FindAll(Entry) regardless of which shape matched.
        Entry = Or(PreambleEntry, StringEntry, RegularEntry).As("entry");

        // Stray text between entries: anything up to the next '@' is a
        // comment in the .bib format.
        //
        // ScanUntil(stopAt: '@'), not ScanWhile(~Runes("@")). The complement
        // form reads as "any character but '@'", but it actually means "any
        // RUNE but '@'", and a rune-set scan matches only single-rune
        // grapheme clusters, so it stops dead at the first CRLF (one
        // character, two runes). See bracedPlainRun above for the full why,
        // including why a complement can only ever be rune-only. ScanUntil
        // tests each character against the one-element stop set instead, so it
        // consumes any non-'@' character, CRLF included, and runs on to the
        // next '@'. eofIsTerminator: true so
        // trailing comment text with no following '@' reaches end of input.
        // Flatten(Delete) drops it so consumers see only entries.
        var strayText = ScanUntil(stopAt: TokenSet.Runes("@"), eofIsTerminator: true)
            .Flatten(FlattenType.Delete);

        // Entry before strayText: ScanUntil succeeds zero-width when already
        // at '@', so strayText first would match empty there and shadow the
        // entry. Entry only matches at '@', so trying it first costs nothing
        // elsewhere. The ZeroOrMore's zero-width stop (see
        // BetweenInclusiveRule) ends the loop on the empty strayText match at
        // end of input.
        Document = And(
            ZeroOrMore(Or(Entry, strayText)),
            Eof()
        );
        Document.Compile();
    }
}
