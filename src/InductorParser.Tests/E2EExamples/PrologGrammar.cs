using InductorParser;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Prolog grammar: Inductor Parser port of InductorProlog's PrologParser.h
// (https://github.com/EricZinda/InductorProlog/blob/master/src/FXPlatform/Prolog/PrologParser.h).
// InductorProlog is MIT licensed, same author as this project.
//
// Covers: line comments (% ... CRLF), block comments (/* ... */), atoms
// (bare words, single- and double-quoted, numeric, math-symbol runs, !),
// variables (two flavors), lists with | tail terms, compound functors
// with nested terms, rules (head :- body), queries (functor list '.' eof),
// and the top-level document (one-or-more rules/facts/lists separated by
// periods, terminated by eof).
public static class PrologGrammar
{
    // Character classes from Parser.cpp / PrologParser.cpp.
    private static readonly TokenSet WhitespaceChars = TokenSet.Ascii.AnyWhitespace;
    // CR and LF as two separate scalars (not the CRLF grapheme
    // cluster). The line-break rule below wants either rune to match
    // on its own. Built from Singles because Runes("\r\n") would
    // throw: adjacent CR+LF form the CRLF cluster under UAX #29.
    private static readonly TokenSet CrlfChars = TokenSet.Single('\r') | TokenSet.Single('\n');
    private static readonly TokenSet LetterChars = TokenSet.Ascii.Letters;
    private static readonly TokenSet CapitalChars = TokenSet.Range('A', 'Z');
    private static readonly TokenSet LetterDigitChars = TokenSet.Ascii.Letters | TokenSet.Ascii.Digits;

    // (letter | digit | '_' | '-'): the set of characters allowed after the
    // first character of an identifier. shared by atoms, variables, and compound identifiers.
    private static readonly TokenSet IdentifierTailChars =
        LetterDigitChars | TokenSet.Runes("_-");

    // Prolog math symbols: +, -, <, >, =, /, *, \
    // Atoms like "+", "=", "<>" are legal (Prolog operators).
    private static readonly TokenSet MathSymbolChars = TokenSet.Runes("+-<>=/*\\");

    // A newline token. The lexer treats "\r\n" as one grapheme cluster
    // (Unicode GB3), so a plain OneOf({'\r', '\n'}) won't match it.
    // OneOf only matches single-rune tokens, and a CRLF grapheme is two
    // runes. Adding Literal("\r\n") as a first alternative lets every
    // whitespace rule in this grammar accept both LF- and CRLF-
    // terminated input.
    //
    // See docs/UnicodeGotchas.md § "CRLF Line Endings" for the full
    // explanation of why OneOf / NoneOf / Token('\n') all fail on CRLF
    // input and the three-anti-patterns-to-avoid list.
    private static readonly Rule LineBreak = Or(
        Literal("\r\n"),
        OneOf(CrlfChars)
    );

    // Comment: "% ...\r\n" OR "% ...<EOF>" OR "/* ... */"
    //
    // Both bodies use ScanUntil with a rule-based stopper. It peeks
    // the stopper on each rune and rolls back, so the terminator is
    // left for the surrounding And to consume. ScanUntil replaces
    // the manual ZeroOrMore(And(Not(stop), AnyToken())) idiom with a
    // tight single-rule scan that returns one leaf Symbol over the
    // matched body text.
    //
    // The line-comment body specifically needs the Rule-stopper form
    // (not OneOf) because a CRLF grapheme is multi-rune and trivially
    // passes any NoneOf, which would greedily swallow the line-ending
    // CRLF and leave the terminator nothing to match.
    public static readonly Rule Comment = Or(
        And(
            Token('%'),
            ScanUntil(LineBreak, eofIsTerminator: true),
            Or(
                OneOrMore(LineBreak),
                Eof()
            )
        ),
        And(
            Literal("/*"),
            ScanUntil(Literal("*/")),
            Literal("*/")
        )
    );

    // Whitespace or comment, zero or more. CRLF-as-grapheme is handled
    // via the Literal alternative for the same reason as in Comment.
    public static readonly Rule OptionalWhitespace = ZeroOrMore(Or(
        Literal("\r\n"),
        OneOf(WhitespaceChars),
        Comment
    ));

    // Atom: Float | Integer | MathSymbol+ | "!" | "..." | '...' | (letter|-) (letter|digit|_|-)*
    //
    // Ordering is critical. Float before Integer because Integer would
    // consume the leading digits of a Float and commit. MathSymbol run
    // after Integer so "-5" parses as Integer, not as math "-" followed
    // by Integer "5". Bare atom last so every other specific shape wins
    // first.
    //
    // Quoted forms don't handle escape sequences. The C++ grammar
    // doesn't either. "\\'" inside a single-quoted atom would end the
    // atom at the first apostrophe regardless of the preceding
    // backslash. Keeping the same behavior for fidelity.
    public static readonly Rule Atom = Or(
        Float(),
        Integer(),
        OneOrMore(OneOf(MathSymbolChars)),
        Token('!'),
        And(
            Token('"'),
            ScanUntil(TokenSet.Runes("\"")),
            Token('"')
        ),
        And(
            Token('\''),
            ScanUntil(TokenSet.Runes("'")),
            Token('\'')
        ),
        And(
            Or(OneOf(LetterChars), Token('-')),
            ZeroOrMore(OneOf(IdentifierTailChars))
        )
    );

    // Variable body shared between both flavors: starts with '_', then
    // zero-or-more identifier-tail chars. "_foo", "_", "_X123-Y".
    private static readonly Rule UnderscoreVariable = And(
        Token('_'),
        ZeroOrMore(OneOf(IdentifierTailChars))
    );

    // Standard Prolog: variable = Capital (letter|digit|_|-)*
    // "X", "Foo", "MyVar_1".
    public static readonly Rule CapitalizedVariableRule = And(
        OneOf(CapitalChars),
        ZeroOrMore(OneOf(IdentifierTailChars))
    );

    // Inductor HTN convention: variable = '?' Atom. Cheap discriminator: the '?'
    // prefix tells the parser it's looking at a variable before the
    // name itself is scanned, which sidesteps the "is this capitalized?"
    // lookahead that standard Prolog needs.
    public static readonly Rule HtnVariableRule = And(
        Token('?'),
        Atom
    );

    // A bundle of the rules that parameterize over the variable flavor.
    // Exposing each one lets tests fragment-target any level of the
    // grammar instead of only the top-level Document.
    public sealed record GrammarBundle(
        Rule Variable,
        Rule Term,
        Rule List,
        Rule Functor,
        Rule TermList,
        Rule FunctorList,
        Rule Rule,
        Rule Query,
        Rule Document);

    public static readonly GrammarBundle Standard = Build(CapitalizedVariableRule);
    public static readonly GrammarBundle Htn = Build(HtnVariableRule);

    // Compile Standard and Htn together so the strict "fresh-tree" Compile
    // invariant is satisfied for tests that .Parse on individual sub-rules
    // (Atom, OptionalWhitespace, Comment, Standard.Term, Htn.Term, ...).
    // Without this, the first sub-rule .Parse auto-compiles its own subtree
    // and seals the shared rules. Later .Parse on a sibling root walks the
    // sealed shared sub-rules and throws. An Or used only for its
    // reachability (never parsed) lets one Compile walk both grammar
    // bundles plus every shared sub-rule in a single pass.
    static PrologGrammar()
    {
        // Query and FunctorList aren't reachable from Document (Document
        // uses Or(rule, functor, list), not Query), so include them
        // explicitly. Same for the other GrammarBundle exposed rules
        // tests call .Parse on. They all need to live in the same
        // compile pass as their sibling sub-rules.
        Or(
            Standard.Document, Htn.Document,
            Standard.Query, Htn.Query
        ).Compile();
    }

    private static GrammarBundle Build(Rule variableFlavorRule)
    {
        // Mutual recursion: Term -> Functor -> TermList -> Term, and
        // List -> TermList -> Term -> List. One LateBoundRule breaks
        // both cycles because every reference points back through Term.
        var termForward = new LateBoundRule("term");

        // Variable = flavor | '_' Tail
        var variable = Or(variableFlavorRule, UnderscoreVariable);

        // TermList = Term ws (, ws Term ws)* (| ws Term ws)?
        // Prolog list tail syntax [H | T] rides on the final optional
        // clause. Trailing OptionalWhitespace after each term means the
        // close bracket in List doesn't need its own leading ws.
        var termList = And(
            termForward,
            OptionalWhitespace,
            ZeroOrMore(And(
                Token(','),
                OptionalWhitespace,
                termForward,
                OptionalWhitespace
            )),
            Optional(And(
                Token('|'),
                OptionalWhitespace,
                termForward,
                OptionalWhitespace
            ))
        );

        // List = "[]" | "[" ws TermList "]"
        // The empty-list literal goes first because the "[" prefix is
        // shared and we want first-match-wins to commit to the empty
        // branch for input "[]".
        var list = Or(
            Literal("[]"),
            And(
                Token('['),
                OptionalWhitespace,
                termList,
                Token(']')
            )
        );

        // Functor = Not(Variable), Atom, ( "(" ws TermList? ws ")" )?
        //
        // The Not(variable) lookahead is what stops a Functor from
        // gobbling something that should parse as a variable. Under
        // standard Prolog, "Foo" reads as a variable. Without the
        // negative lookahead, Functor would match "Foo" as an atom with
        // no args and steal the parse. The Not only checks the
        // flavor-specific variable rule (CapitalizedVariableRule or
        // HtnVariableRule), not the underscore form. "_foo" parses
        // as a variable regardless, since Atom's bare branch doesn't
        // accept a leading underscore anyway.
        var functor = And(
            Not(variableFlavorRule),
            Atom,
            Optional(And(
                Token('('),
                OptionalWhitespace,
                Optional(termList),
                OptionalWhitespace,
                Token(')')
            ))
        );

        // Term = Variable | Functor | List. Order matters: Variable
        // first so "X" commits to the variable branch. Functor next
        // because every functor-atom shape is also reached here. List
        // last because its "[" prefix doesn't collide with the other
        // two.
        var termDef = Or(variable, functor, list);
        var _termBinding = termForward.Bind(termDef);

        // FunctorList = Functor (, ws Functor ws)*
        // Used by Query, which is a list of goals separated by commas.
        var functorList = And(
            functor,
            OptionalWhitespace,
            ZeroOrMore(And(
                Token(','),
                OptionalWhitespace,
                functor,
                OptionalWhitespace
            ))
        );

        // Rule = Functor ws ":-" ws TermList?
        // The C++ writes ":-" as two separate char symbols
        // (CharacterSymbol<Colon> + CharacterSymbol<Dash>). Either form
        // is equivalent for matching. Using Literal(":-") here for
        // readability.
        var rule = And(
            functor,
            OptionalWhitespace,
            Literal(":-"),
            OptionalWhitespace,
            Optional(termList)
        );

        // Query = ws FunctorList ws "." ws Eof
        // Queries are a single statement terminated by '.', with no
        // OneOrMore around them, unlike Document.
        var query = And(
            OptionalWhitespace,
            functorList,
            OptionalWhitespace,
            Token('.'),
            OptionalWhitespace,
            Eof()
        );

        // Document = (ws (Rule | Functor | List) ws "." ws)+ ws Eof
        //
        // OneOrMore at the outer level is a deliberate C++ choice: an
        // empty file is a parse error. Inside each item, Rule is tried
        // first so "foo :- bar." doesn't get mis-identified as a fact
        // "foo" followed by garbage ":- bar." that can't find its '.'.
        var document = And(
            OneOrMore(And(
                OptionalWhitespace,
                Or(rule, functor, list),
                OptionalWhitespace,
                Token('.'),
                OptionalWhitespace
            )),
            OptionalWhitespace,
            Eof()
        );

        return new GrammarBundle(
            Variable: variable,
            Term: termDef,
            List: list,
            Functor: functor,
            TermList: termList,
            FunctorList: functorList,
            Rule: rule,
            Query: query,
            Document: document
        );
    }
}
