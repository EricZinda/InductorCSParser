using System;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;

namespace InductorParser.Tests;

[TestFixture]
public class OptionalRuleTests
{
    // Tree.ToString() assertions below use PreserveFlattenWrappers so
    // Char leaves (default FlattenType.Delete) survive parse-time
    // filtering and appear in the concatenated view.
    private static ParseOptions Debug() => new() { PreserveFlattenWrappers = true };

    [Test]
    public void Optional_inner_match_is_consumed()
    {
        // Optional wraps a rule; when inner matches, that input is consumed
        // and the surrounding grammar sees the post-match position.
        var rule = And(Optional(Char('-')), Char('a'));
        var result = rule.Parse("-a", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("-a"));
    }

    [Test]
    public void Optional_inner_miss_succeeds_with_no_consumption()
    {
        // Inner doesn't match; Optional still succeeds with empty and the
        // surrounding grammar runs from the same position Optional started at.
        var rule = And(Optional(Char('-')), Char('a'));
        var result = rule.Parse("a", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("a"));
    }

    [Test]
    public void Optional_inner_failure_still_contributes_to_DeepestFailure()
    {
        // Known PEG heuristic quirk: an Optional whose inner fails deeper
        // than the required path can still win the error message via
        // deepest-failure-wins.
        //
        // Grammar: And(Optional(And(a, b, c-with-message)), x-with-message)
        // Input:   "abdy"
        //
        // Optional's inner reads "ab" then 'c' fails at offset 2,
        // recording "need 'c'". Optional catches, succeeds with empty.
        // Char('x') then fails at offset 0 with its own "need 'x'".
        // Deepest-wins picks offset 2: user sees "need 'c'", pointing
        // inside what was supposedly optional. Grammars that care can
        // override with a WithError at the outer required rule, but
        // that won't help here because the outer Char('x') already has
        // one and it's still shallower.
        var rule = And(Optional(And(Char('a'),
                                    Char('b'),
                                    Char('c').WithError("need 'c'"))),
                       Char('x').WithError("need 'x'"));

        var result = rule.Parse("abdy");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
        Assert.That(result.ErrorMessage, Is.EqualTo("need 'c'"));
    }

    [Test]
    public void Optional_with_no_match_shares_empty_children_singleton()
    {
        // p200 lazy-allocation contract: an Optional / ZeroOrMore that
        // matches zero times must produce a wrapper Symbol whose Children
        // field is the shared Array.Empty<Symbol>() singleton, not a
        // freshly-allocated empty List<Symbol>. ReferenceEquals against
        // Array.Empty<Symbol>() is the cleanest unit-test signal that the
        // no-allocation path was actually taken; any future regression
        // that goes back to "always new List" will trip this assertion.
        var result = Optional(Char('x')).Parse("");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree, Is.Not.Null);
        Assert.That(result.Tree!.Children, Is.Empty);
        Assert.That(ReferenceEquals(result.Tree.Children, Array.Empty<Symbol>()),
            Is.True,
            "Optional with no match should reuse the shared empty array, not allocate a new list.");
    }

    [Test]
    public void Optional_trace_with_match_produces_expected_output()
    {
        // Optional opens its own transaction. And(Optional(Char('a')),
        // Char('b')) on "ab": And at depth 1, Optional adds depth 2,
        // the inner Char adds depth 3 (nine spaces).
        var sink = NewSink();
        And(Optional(Char('a')), Char('b'))
            .Parse("ab", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "         Lexer.Read: 'a', Consumed: 1",
            "         SUCC | Char: found 'a'",
            "      SUCC | Optional: count= 1",
            "      Lexer.Read: 'b', Consumed: 2",
            "      SUCC | Char: found 'b'",
            "   SUCC | And: found 2"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Optional_trace_without_match_produces_expected_output()
    {
        // Inner fails, Optional still succeeds with count= 0. Char('b')
        // then runs against the original position since Optional's
        // commit didn't advance the lexer.
        var sink = NewSink();
        And(Optional(Char('a')), Char('b'))
            .Parse("b", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "         Lexer.Read: 'b', Consumed: 1",
            "         FAIL | Char: found 'b', wanted 'a'",
            "      SUCC | Optional: count= 0",
            "      Lexer.Read: 'b', Consumed: 1",
            "      SUCC | Char: found 'b'",
            "   SUCC | And: found 2"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }
}
