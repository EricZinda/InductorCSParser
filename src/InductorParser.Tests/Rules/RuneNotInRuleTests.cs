using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;
using static InductorParser.Tests.UnicodeExamples;

namespace InductorParser.Tests;

[TestFixture]
public class RuneNotInRuleTests
{
    [Test]
    public void RuneNotIn_matches_a_rune_outside_the_set()
    {
        // 'x' is not a digit, so RuneNotIn(Digits) succeeds on it.
        var rule = RuneNotIn(RuneSet.Digits);
        var result = rule.Parse("x");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("x"));
    }

    [Test]
    public void RuneNotIn_fails_when_rune_is_in_the_set()
    {
        // '5' is a digit, so RuneNotIn(Digits) fails at offset 0.
        var rule = RuneNotIn(RuneSet.Digits).WithError("no digits here");
        var result = rule.Parse("5");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("no digits here"));
    }

    [Test]
    public void RuneNotIn_fails_at_EOF()
    {
        // EOF is not "a rune not in the set" — it is no rune at all. Fail.
        var rule = RuneNotIn(RuneSet.Digits).WithError("wanted a non-digit");
        var result = rule.Parse("");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("wanted a non-digit"));
    }

    [Test]
    public void RuneNotIn_matches_multi_rune_grapheme_under_grapheme_lexer()
    {
        // Under GraphemeLexer a multi-rune grapheme like LatinEAcuteGrapheme
        // arrives as a single token whose RuneValue is -1. The "not a single
        // rune in the set" predicate is trivially true for it: the token
        // isn't any single rune at all. This is the property that lets
        // ZeroOrMore(RuneNotIn(...)) sweep up arbitrary Unicode text.
        // NormalizeInput = null so the decomposed "e\u0301" arrives at the
        // lexer verbatim; the default NFC would compose it to "\u00E9" and
        // collapse this test's "multi-rune grapheme" premise.
        var rule = RuneNotIn(RuneSet.Ascii.Letters);
        var result = rule.Parse(LatinEAcuteGrapheme,
            new ParseOptions { NormalizeInput = null });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(LatinEAcuteGrapheme));
    }

    [Test]
    public void RuneNotIn_under_rune_lexer_matches_a_single_rune_outside_the_set()
    {
        // Under RuneLexer each token is exactly one rune. The asymmetry
        // with GraphemeLexer doesn't apply here. A letter 'x' is one rune
        // outside RuneSet.Digits, so the rule succeeds.
        var rule = RuneNotIn(RuneSet.Digits);
        var result = rule.Parse("x",
            new ParseOptions { InputUnit = InputUnit.Rune });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("x"));
    }

    [Test]
    public void RuneNotIn_sweeps_passthrough_text_up_to_a_delimiter()
    {
        // The pass-through-text idiom: ZeroOrMore(RuneNotIn(stopSet)) matches
        // everything that isn't in the stop set, then the surrounding rule
        // handles the stop character. Here the stop is a single '\n'.
        //
        // WARNING: this idiom is LF-only under the default GraphemeLexer.
        // A CRLF grapheme passes RuneNotIn unconditionally (it isn't a
        // single rune, so it can't be in any single-rune set), which
        // means the sweep silently consumes the CRLF and the trailing
        // Token('\n') terminator then fails. For real line-based grammars,
        // add Literal("\r\n") to both the stop set and the terminator.
        // See docs/UnicodeGotchas.md § "CRLF Under GraphemeLexer".
        var rule = And(
            ZeroOrMore(RuneNotIn(RuneSet.Single('\n'))),
            Token('\n'));

        // PreserveFlattenWrappers keeps the trailing Token('\n') in the
        // tree so Tree.ToString reproduces the full matched line.
        var result = rule.Parse("hello world\n",
            new ParseOptions { PreserveFlattenWrappers = true });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("hello world\n"));
    }

    [Test]
    public void RuneNotIn_trace_success_produces_expected_output()
    {
        var sink = NewSink();
        RuneNotIn(RuneSet.Ascii.Digits).Parse("x",
            new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: 'x', Consumed: 1",
            "   SUCC | RuneNotIn: found 'x', wanted one not in '[0-9]'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void RuneNotIn_trace_failure_produces_expected_output()
    {
        var sink = NewSink();
        RuneNotIn(RuneSet.Ascii.Digits).Parse("5",
            new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: '5', Consumed: 1",
            "   FAIL | RuneNotIn: found '5', wanted one not in '[0-9]'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }
}
