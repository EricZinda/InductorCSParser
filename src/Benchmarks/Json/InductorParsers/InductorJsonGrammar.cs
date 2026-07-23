using System.Text;
using global::InductorParser;
using global::InductorParser.SyntaxTree;
using static global::InductorParser.Rules;

namespace InductorParser.Benchmarks.Json.InductorParsers;

// JSON grammar for the shape the JsonBench harness generates: strings,
// objects, and arrays only. No numbers, booleans, or nulls, because the
// harness doesn't generate them (see JsonBench.BuildObject) and because
// every competitor in the bench (Pidgin, Sprache, Superpower, Pegasus,
// Parlot) also stops there. Including them here would make the comparison
// unfair on inputs competitors can't handle, and would also measure a
// code path that the competitors don't exercise.
//
// String bodies handle the full set of JSON escapes (\", \\, \/, \b, \f,
// \n, \r, \t, and \uXXXX) to match what the competitors do, so the
// string-parsing hot path is apples-to-apples.
//
// Entry point is the bare value rule (no surrounding
// And(Optional(AnyWhitespace()), value, Optional(AnyWhitespace()), Eof)). The harness
// feeds clean input that starts and ends at the value, competitors
// likewise skip a trailing Eof rule, and adding one would spend
// time on every parse that the bench isn't trying to measure.
//
// Build() returns a fresh, uncompiled graph each call so different
// benchmarks can compile the same grammar with different normalization
// forms: InductorJsonParser compiles one with the default FormC, and
// UnicodeJsonBench compiles a second with Compile(null) to measure a
// parse that skips input normalization.
public sealed class InductorJsonGrammar
{
    public Rule RootRule { get; }
    public Rule StringRule { get; }
    public Rule ArrayRule { get; }
    public Rule ObjectRule { get; }
    public Rule MemberRule { get; }

    private InductorJsonGrammar(
        Rule rootRule, Rule stringRule, Rule arrayRule, Rule objectRule, Rule memberRule)
    {
        RootRule = rootRule;
        StringRule = stringRule;
        ArrayRule = arrayRule;
        ObjectRule = objectRule;
        MemberRule = memberRule;
    }

    public static InductorJsonGrammar Build()
    {
        var simpleEscapeEnd = OneOf(TokenSet.Runes("\"\\/bfnrt"));
        var hexDigit = OneOf(TokenSet.Ascii.HexDigits);
        var unicodeEscapeEnd = And(Token('u'), hexDigit, hexDigit, hexDigit, hexDigit);
        var escapeEnd = Or(simpleEscapeEnd, unicodeEscapeEnd).Flatten(FlattenType.Delete);
        var stringBody = ScanUntil(stopAt: TokenSet.Runes("\""), escapeStart: new Rune('\\'), escapeEnd: escapeEnd);
        var stringRule = And(Token('"'), stringBody, Token('"')).As("string");

        var value = new LateBoundRule("value");

        var memberRule = And(
            stringRule,
            Optional(AnyWhitespace()),
            Token(':'),
            Optional(AnyWhitespace()),
            value
        ).As("member");

        var objectRule = And(
            Token('{'),
            Optional(AnyWhitespace()),
            Optional(And(
                memberRule,
                ZeroOrMore(And(Optional(AnyWhitespace()), Token(','), Optional(AnyWhitespace()), memberRule))
            )),
            Optional(AnyWhitespace()),
            Token('}')
        ).As("object");

        var arrayRule = And(
            Token('['),
            Optional(AnyWhitespace()),
            Optional(And(
                value,
                ZeroOrMore(And(Optional(AnyWhitespace()), Token(','), Optional(AnyWhitespace()), value))
            )),
            Optional(AnyWhitespace()),
            Token(']')
        ).As("array");

        value.Bind(Or(stringRule, objectRule, arrayRule));

        return new InductorJsonGrammar(value, stringRule, arrayRule, objectRule, memberRule);
    }
}
