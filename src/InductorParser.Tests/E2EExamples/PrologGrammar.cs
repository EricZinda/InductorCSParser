using InductorParser;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Prolog grammar: PEG port of InductorProlog's PrologParser.h
// (https://github.com/EricZinda/InductorProlog/blob/master/src/FXPlatform/Prolog/PrologParser.h).
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
    private static readonly RuneSet WhitespaceChars = RuneSet.Ascii.Whitespace;
    private static readonly RuneSet CrlfChars = RuneSet.Runes("\r\n");
    private static readonly RuneSet LetterChars = RuneSet.Ascii.Letters;
    private static readonly RuneSet CapitalChars = RuneSet.Range('A', 'Z');
    private static readonly RuneSet LetterDigitChars = RuneSet.Ascii.Letters | RuneSet.Ascii.Digits;

    // (letter | digit | '_' | '-'): the set of characters allowed after the
    // first character of an identifier. shared by atoms, variables, and compound identifiers.
    private static readonly RuneSet IdentifierTailChars =
        LetterDigitChars | RuneSet.Runes("_-");

    // Prolog math symbols: +, -, <, >, =, /, *, \
    // Atoms like "+", "=", "<>" are legal (Prolog operators).
    private static readonly RuneSet MathSymbolChars = RuneSet.Runes("+-<>=/*\\");

    // A newline token. Under the default GraphemeLexer, "\r\n" is one
    // grapheme cluster (Unicode GB3), so a plain OneOf({'\r', '\n'})
    // won't match it. OneOf only matches single-rune tokens, and a
    // CRLF grapheme is two runes. Adding Literal("\r\n") as a first
    // alternative lets every whitespace rule in this grammar accept
    // both LF- and CRLF-terminated input under either lexer without
    // forcing callers to switch the lexer via ParseOptions.
    //
    // See docs/UnicodeGotchas.md § "CRLF Under GraphemeLexer" for the
    // full explanation of why OneOf / NoneOf / Grapheme('\n') all fail
    // on CRLF input and the three-anti-patterns-to-avoid list.
    private static readonly Rule LineBreak = FirstOf(
        Literal("\r\n"),
        OneOf(CrlfChars)
    );

    // Comment: "% ...\r\n" OR "% ...<EOF>" OR "/* ... */"
    //
    // Both bodies use ScanUntil with a rule-based stopper. It peeks
    // the stopper on each rune and rolls back, so the terminator is
    // left for the surrounding AllOf to consume. ScanUntil replaces
    // the manual ZeroOrMore(AllOf(Not(stop), AnyToken())) idiom with a
    // tight single-rule scan that returns one leaf Symbol over the
    // matched body text.
    //
    // The line-comment body specifically needs the Rule-stopper form
    // (not OneOf) because under GraphemeLexer a CRLF grapheme is
    // multi-rune and trivially passes any NoneOf, which would
    // greedily swallow the line-ending CRLF and leave the terminator
    // nothing to match.
    public static readonly Rule Comment = FirstOf(
        AllOf(
            Grapheme('%'),
            ScanUntil(LineBreak),
            FirstOf(
                OneOrMore(LineBreak),
                Eof()
            )
        ),
        AllOf(
            Literal("/*"),
            ScanUntil(Literal("*/")),
            Literal("*/")
        )
    );

    // Whitespace or comment, zero or more. CRLF-as-grapheme is handled
    // via the Literal alternative for the same reason as in Comment.
    public static readonly Rule OptionalWhitespace = ZeroOrMore(FirstOf(
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
    public static readonly Rule Atom = FirstOf(
        Float(),
        Integer(),
        OneOrMore(OneOf(MathSymbolChars)),
        Grapheme('!'),
        AllOf(
            Grapheme('"'),
            ScanUntil(RuneSet.Runes("\"")),
            Grapheme('"')
        ),
        AllOf(
            Grapheme('\''),
            ScanUntil(RuneSet.Runes("'")),
            Grapheme('\'')
        ),
        AllOf(
            FirstOf(OneOf(LetterChars), Grapheme('-')),
            ZeroOrMore(OneOf(IdentifierTailChars))
        )
    );

    // Variable body shared between both flavors: starts with '_', then
    // zero-or-more identifier-tail chars. "_foo", "_", "_X123-Y".
    private static readonly Rule UnderscoreVariable = AllOf(
        Grapheme('_'),
        ZeroOrMore(OneOf(IdentifierTailChars))
    );

    // Standard Prolog: variable = Capital (letter|digit|_|-)*
    // "X", "Foo", "MyVar_1".
    public static readonly Rule CapitalizedVariableRule = AllOf(
        OneOf(CapitalChars),
        ZeroOrMore(OneOf(IdentifierTailChars))
    );

    // Inductor HTN convention: variable = '?' Atom. Cheap discriminator: the '?'
    // prefix tells the parser it's looking at a variable before the
    // name itself is scanned, which sidesteps the "is this capitalized?"
    // lookahead that standard Prolog needs.
    public static readonly Rule HtnVariableRule = AllOf(
        Grapheme('?'),
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

    private static GrammarBundle Build(Rule variableFlavorRule)
    {
        // Mutual recursion: Term -> Functor -> TermList -> Term, and
        // List -> TermList -> Term -> List. One LateBoundRule breaks
        // both cycles because every reference points back through Term.
        var termForward = new LateBoundRule("term");

        // Variable = flavor | '_' Tail
        var variable = FirstOf(variableFlavorRule, UnderscoreVariable);

        // TermList = Term ws (, ws Term ws)* (| ws Term ws)?
        // Prolog list tail syntax [H | T] rides on the final optional
        // clause. Trailing OptionalWhitespace after each term means the
        // close bracket in List doesn't need its own leading ws.
        var termList = AllOf(
            termForward,
            OptionalWhitespace,
            ZeroOrMore(AllOf(
                Grapheme(','),
                OptionalWhitespace,
                termForward,
                OptionalWhitespace
            )),
            Optional(AllOf(
                Grapheme('|'),
                OptionalWhitespace,
                termForward,
                OptionalWhitespace
            ))
        );

        // List = "[]" | "[" ws TermList "]"
        // The empty-list literal goes first because the "[" prefix is
        // shared and we want first-match-wins to commit to the empty
        // branch for input "[]".
        var list = FirstOf(
            Literal("[]"),
            AllOf(
                Grapheme('['),
                OptionalWhitespace,
                termList,
                Grapheme(']')
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
        var functor = AllOf(
            Not(variableFlavorRule),
            Atom,
            Optional(AllOf(
                Grapheme('('),
                OptionalWhitespace,
                Optional(termList),
                OptionalWhitespace,
                Grapheme(')')
            ))
        );

        // Term = Variable | Functor | List. Order matters: Variable
        // first so "X" commits to the variable branch. Functor next
        // because every functor-atom shape is also reached here. List
        // last because its "[" prefix doesn't collide with the other
        // two.
        var termDef = FirstOf(variable, functor, list);
        var _termBinding = termForward.Bind(termDef);

        // FunctorList = Functor (, ws Functor ws)*
        // Used by Query, which is a list of goals separated by commas.
        var functorList = AllOf(
            functor,
            OptionalWhitespace,
            ZeroOrMore(AllOf(
                Grapheme(','),
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
        var rule = AllOf(
            functor,
            OptionalWhitespace,
            Literal(":-"),
            OptionalWhitespace,
            Optional(termList)
        );

        // Query = ws FunctorList ws "." ws Eof
        // Queries are a single statement terminated by '.', no
        // OneOrMore wrapper, unlike Document.
        var query = AllOf(
            OptionalWhitespace,
            functorList,
            OptionalWhitespace,
            Grapheme('.'),
            OptionalWhitespace,
            Eof()
        );

        // Document = (ws (Rule | Functor | List) ws "." ws)+ ws Eof
        //
        // OneOrMore at the outer level is a deliberate C++ choice: an
        // empty file is a parse error. Inside each item, Rule is tried
        // first so "foo :- bar." doesn't get mis-identified as a fact
        // "foo" followed by garbage ":- bar." that can't find its '.'.
        var document = AllOf(
            OneOrMore(AllOf(
                OptionalWhitespace,
                FirstOf(rule, functor, list),
                OptionalWhitespace,
                Grapheme('.'),
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
