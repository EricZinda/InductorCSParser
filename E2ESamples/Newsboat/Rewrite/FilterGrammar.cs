// Newsboat filter expression grammar (mirrors filter/filter.atg from the
// upstream newsboat repo, MIT licensed).
//
//   <filter>     ::= space0 <expr> space0 EOF
//   <expr>       ::= <primary> (space0 <logop> space0 <expr>)?
//   <primary>    ::= '(' space0 <expr> space0 ')' | <comparison>
//   <comparison> ::= <attribute> space0 <operator> space0 <value>
//   <attribute>  ::= [A-Za-z0-9_.\-]+
//   <operator>   ::= '=~' | '==' | '!~' | '!=' | '<=' | '>=' |
//                    'between' | '!#' | '=' | '<' | '>' | '#'
//   <value>      ::= <quotedString> | <range> | <number>
//   <number>     ::= '-'? digit+
//   <range>      ::= <number> ':' <number>
//   <logop>      ::= ('and' | 'or') &(' ' | '(')
//
// Three quirks worth naming because the rewrite has to match upstream:
//
// 1. Whitespace is the ASCII space (0x20) only. Tab, NEL, CR, LF, VT,
//    FF do NOT count as whitespace and trip the parser the way any
//    other unrecognized rune would.
// 2. The `and` / `or` keywords must be followed by a space or by `(`.
//    `x=1and y=0` and `x=1and(y=0)` parse; `x=1and(y=0)` parses too.
//    `x=1andy=0` doesn't. Encoded here as `Peek(Or(Token(' '),
//    Token('(')))` after the keyword.
// 3. The expression chain is right-associative: `a or b and c` parses
//    as `Or(a, And(b, c))`, not `And(Or(a, b), c)`. There is no
//    precedence between `and` and `or`. We get this for free from the
//    right-recursive shape of <expr>.

using InductorParser;
using static InductorParser.Rules;

namespace NewsboatSample.Rewrite;

public static class FilterGrammar
{
    public static readonly Rule Filter;
    public static readonly Rule Comparison;
    public static readonly Rule AttributeName;
    public static readonly Rule ComparisonOperator;
    public static readonly Rule ComparisonValue;
    public static readonly Rule QuotedStringLiteral;
    public static readonly Rule QuotedStringBody;
    public static readonly Rule NumberLiteral;
    public static readonly Rule RangeLiteral;
    public static readonly Rule Group;
    public static readonly Rule AndKeyword;
    public static readonly Rule OrKeyword;

    static FilterGrammar()
    {
        var space = Token(' ');
        var optionalSpaces = ZeroOrMore(space).Delete();

        var attributeChar = TokenSet.Ascii.Letters | TokenSet.Ascii.Digits | TokenSet.Runes("_-.");
        AttributeName = ScanWhile(attributeChar, minimumCount: 1)
            .As("attribute").Preserve()
            .WithError("expected attribute name");

        ComparisonOperator = Or(
            Literal("=~"),
            Literal("=="),
            Literal("!~"),
            Literal("!="),
            Literal("<="),
            Literal(">="),
            Literal("between"),
            Literal("!#"),
            Token('='),
            Token('<'),
            Token('>'),
            Token('#')
        ).As("operator").Preserve()
         .WithError("expected one of: =~, ==, =, !~, !=, <=, >=, <, >, between, #, !#");

        NumberLiteral = And(
            Optional(Token('-')),
            ScanWhile(TokenSet.Ascii.Digits, minimumCount: 1)
        ).As("number").Preserve();

        RangeLiteral = And(NumberLiteral, Token(':'), NumberLiteral).As("range").Preserve();

        // stopAt is just the closing quote. The backslash is the
        // escape-start, but ScanUntil checks the stopper FIRST and only
        // tries the escape if the stopper didn't match, so putting `\`
        // in the stopper would make the escape unreachable.
        QuotedStringBody = ScanUntil(
            TokenSet.Runes("\""),
            new System.Text.Rune('\\'),
            AnyToken()
        ).As("quotedStringBody");

        QuotedStringLiteral = And(Token('"'), QuotedStringBody, Token('"'))
            .As("quotedString").Preserve();

        // Range tries before Number because both start with -? digit;
        // committing to Number first would leave the ":15:30" tail
        // dangling on inputs like "AAAA between 0:15:30".
        ComparisonValue = Or(QuotedStringLiteral, RangeLiteral, NumberLiteral)
            .As("value").Preserve()
            .WithError("expected one of: quoted string, range, number");

        Comparison = And(
            AttributeName,
            optionalSpaces,
            ComparisonOperator,
            optionalSpaces,
            ComparisonValue
        ).As("comparison").Preserve();

        // `and` / `or` must be followed by whitespace or `(`, otherwise
        // an attribute starting with those letters would be eaten as a
        // logop. Peek consumes nothing, so the spaces / paren stay for
        // the next rule to handle.
        var logopFollower = Peek(Or(space, Token('(')));
        AndKeyword = And(Literal("and"), logopFollower).As("and").Preserve();
        OrKeyword = And(Literal("or"), logopFollower).As("or").Preserve();
        var logop = Or(AndKeyword, OrKeyword);

        var expression = new LateBoundRule("expression");
        Group = And(
            Token('('),
            optionalSpaces,
            expression,
            optionalSpaces,
            Token(')').WithError("expected ')'")
        ).As("group").Preserve();
        var primary = Or(Group, Comparison);

        // Right-associative chain. The optional tail makes a single
        // comparison parse to just <comparison> (no expression
        // wrapper), and a chain like `a and b and c` parse as a
        // comparison followed by an `and` and a recursive expression.
        expression.Bind(And(
            primary,
            Optional(And(
                optionalSpaces,
                logop,
                optionalSpaces,
                expression
            ))
        ));

        Filter = And(
            optionalSpaces,
            expression,
            optionalSpaces,
            Eof()
        ).As("filter").Preserve();

        Filter.Compile();
    }
}
