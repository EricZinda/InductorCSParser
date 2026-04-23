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
    // Token leaves (default FlattenType.Delete) survive parse-time
    // filtering and appear in the concatenated view.
    private static ParseOptions Debug() => new() { PreserveFlattenWrappers = true };

    [Test]
    public void Optional_inner_match_is_consumed()
    {
        // Optional wraps a rule. When inner matches, that input is consumed
        // and the surrounding grammar sees the post-match position.
        var rule = And(Optional(Token('-')), Token('a'));
        var result = rule.Parse("-a", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("-a"));
    }

    [Test]
    public void Optional_inner_miss_succeeds_with_no_consumption()
    {
        // Inner doesn't match. Optional still succeeds with empty and the
        // surrounding grammar runs from the same position Optional started at.
        var rule = And(Optional(Token('-')), Token('a'));
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
        // Token('x') then fails at offset 0 with its own "need 'x'".
        // Deepest-wins picks offset 2: user sees "need 'c'", pointing
        // inside what was supposedly optional. Grammars that care can
        // override with a WithError at the outer required rule, but
        // that won't help here because the outer Token('x') already has
        // one and it's still shallower.
        var rule = And(Optional(And(Token('a'),
                                    Token('b'),
                                    Token('c').WithError("need 'c'"))),
                       Token('x').WithError("need 'x'"));

        var result = rule.Parse("abdy");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
        Assert.That(result.ErrorMessage, Is.EqualTo("need 'c'"));
    }

    [Test]
    public void Optional_with_no_match_produces_empty_symbols()
    {
        // Optional / ZeroOrMore that matches zero times has
        // FlattenType.Flatten, so no wrapper Symbol is ever produced:
        // the empty match just leaves the root Symbols list empty. No per-rune leaves, no
        // BetweenInclusive wrapper, no children-list allocation survives
        // into the tree.
        var result = Optional(Token('x')).Parse("");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Symbols, Is.Empty);
    }

    [Test]
    public void Optional_trace_with_match_produces_expected_output()
    {
        // Optional opens its own transaction. And(Optional(Token('a')),
        // Token('b')) on "ab": And at depth 1, Optional adds depth 2,
        // the inner Token adds depth 3 (nine spaces).
        var sink = NewSink();
        And(Optional(Token('a')), Token('b'))
            .Parse("ab", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "         Lexer.Read: 'a', Consumed: 1",
            "         SUCC | Token: found 'a'",
            "      SUCC | Optional: count= 1",
            "      Lexer.Read: 'b', Consumed: 2",
            "      SUCC | Token: found 'b'",
            "   SUCC | And: found 2"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Optional_trace_without_match_produces_expected_output()
    {
        // Optional's first-rune lookahead skip proves Token('a') can't match
        // on input "b" without reading (peek 'b' not in {'a'}), so Optional
        // succeeds with count= 0 immediately and no inner Read/FAIL trace
        // appears. Token('b') then runs against the original position since
        // Optional's commit didn't advance the lexer.
        var sink = NewSink();
        And(Optional(Token('a')), Token('b'))
            .Parse("b", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      SUCC | Optional: count= 0",
            "      Lexer.Read: 'b', Consumed: 1",
            "      SUCC | Token: found 'b'",
            "   SUCC | And: found 2"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }
}
