using NUnit.Framework;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests.DocExamples;

// Spot-checks runnable code examples in docs/InductorParserDesignDecisions.md.
// The doc is mostly architecture and design rationale; this fixture covers
// the few examples that make specific runnable claims about behavior.
[TestFixture]
public class InductorParserDesignDecisionsExamples
{
    // "Greedy Repetition, No Repetition Backtracking": the doc shows
    // that PEG DOESN'T backtrack inside a OneOrMore. So
    //   And(OneOrMore(OneOf(TokenSet.Letters)), Token('a')).Parse("aaa")
    // greedily consumes "aaa", fails to match the trailing 'a' against
    // EOF, and the whole parse fails.
    [Test]
    public void PEG_does_not_backtrack_inside_repetition()
    {
        var rule = And(OneOrMore(OneOf(TokenSet.Letters)), Token('a'));
        var result = rule.Parse("aaa");

        Assert.That(result.Success, Is.False,
            "PEG OneOrMore is greedy with no rewind, so trailing Token('a') has nothing to match");
    }

    // "Parse Requires Consuming All Input": doc claim
    //   OneOrMore(Token('a')).Parse("aabb")
    //   result.Success == false
    //   result.ErrorCharIndex == 2
    //   result.ErrorMessage starts with "Parse failed at offset 2"
    [Test]
    public void Parse_requires_consuming_all_input()
    {
        var rule = OneOrMore(Token('a'));
        var result = rule.Parse("aabb");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
        Assert.That(result.ErrorMessage, Does.StartWith("Parse failed at offset 2"));
    }

    // "Where Errors Get Positioned" / "Walk through the smallest case":
    // doc claim Token('a').Parse("x") records failure at offset 0
    // (pre-read) and the error message identifies the unexpected 'x'.
    [Test]
    public void Token_failure_pre_read_position_is_offset_zero()
    {
        var result = Token('a').Parse("x");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0),
            "Token records at transaction.StartPosition (pre-read), not after consuming the wrong rune");
        Assert.That(result.ErrorMessage, Does.Contain("'x'"));
    }

    // "A Known Heuristic Limitation": doc claim about Optional capturing
    // the deepest failure. Concrete case from the doc:
    //   And(Optional(Literal("abc")), Token('x')).Parse("abdy")
    // Optional inner reads "ab" and fails on 'd' vs 'c' at offset 2.
    // Optional catches and succeeds with empty children. Token('x')
    // tries at offset 0, fails on 'a'. Count rules (Optional /
    // ZeroOrMore / ...) don't clear inner failures on commit, so the
    // inner's deeper failure survives and surfaces.
    [Test]
    public void Optional_can_capture_error_position_via_deepest_failure_wins()
    {
        var rule = And(Optional(Literal("abc")), Token('x'));
        var result = rule.Parse("abdy");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2),
            "Optional's inner attempt at offset 2 wins because count rules don't clear inner failures on commit");
    }

    // "Things That Got Worse" / "Rule graphs can have order-of-init traps":
    // the LateBoundRule pattern with self-reference. Doc claim: the
    //   static readonly LateBoundRule Expression = new();
    //   static readonly Rule Term = Or(Integer(), And(Token('('), Expression, Token(')')));
    //   static readonly Rule Sum = And(Term, ZeroOrMore(And(Token('+'), Term)));
    //   static readonly Rule _init = Expression.Bind(Sum);
    // pattern parses "(1+2)+3" successfully.
    private static class ExpressionGrammar
    {
        public static readonly LateBoundRule Expression = new LateBoundRule();

        public static readonly Rule Term =
            Or(
                Integer(),
                And(Token('('), Expression, Token(')')));

        public static readonly Rule Sum =
            And(Term, ZeroOrMore(And(Token('+'), Term)));

        public static readonly Rule _Init = Expression.Bind(Sum);
    }

    [Test]
    public void LateBoundRule_self_reference_pattern_parses()
    {
        var grammar = And(ExpressionGrammar.Expression, Eof()).Compile();
        Assert.That(grammar.Parse("1").Success, Is.True);
        Assert.That(grammar.Parse("1+2").Success, Is.True);
        Assert.That(grammar.Parse("(1+2)+3").Success, Is.True);
        Assert.That(grammar.Parse("(1+(2+3))+4").Success, Is.True);
    }
}
