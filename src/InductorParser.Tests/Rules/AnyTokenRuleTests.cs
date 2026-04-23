using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;
using static InductorParser.Tests.UnicodeExamples;

namespace InductorParser.Tests;

[TestFixture]
public class AnyTokenRuleTests
{
    [Test]
    public void AnyToken_matches_a_single_ascii_character()
    {
        var rule = AnyToken();
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("a"));
    }

    [Test]
    public void AnyToken_fails_at_EOF()
    {
        var rule = AnyToken().WithError("wanted any character");
        var result = rule.Parse("");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("wanted any character"));
    }

    [Test]
    public void AnyToken_under_grapheme_lexer_consumes_whole_grapheme()
    {
        // Under GraphemeLexer LatinEAcuteGrapheme is one token (two runes,
        // one grapheme). AnyToken consumes the whole token as a single match.
        //
        // NormalizeInput = null so the two-rune decomposed form survives to
        // the lexer. The default NFC would compose to a one-rune grapheme
        // and undo this test's premise.
        var rule = AnyToken();
        var result = rule.Parse(LatinEAcuteGrapheme,
            new ParseOptions { NormalizeInput = null });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(LatinEAcuteGrapheme));
    }

    [Test]
    public void AnyToken_under_rune_lexer_consumes_one_rune_at_a_time()
    {
        // Under RuneLexer each token is one rune. LatinEAcuteGrapheme is
        // two runes, so a single AnyToken() only covers the first one and
        // the grammar has to ask for more to consume the rest.
        var rule = And(AnyToken(), AnyToken());
        var result = rule.Parse(LatinEAcuteGrapheme,
            new ParseOptions { InputUnit = InputUnit.Rune, NormalizeInput = null });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(string.Concat(result.Symbols), Is.EqualTo(LatinEAcuteGrapheme));
    }

    [Test]
    public void AnyToken_repeated_consumes_arbitrary_text_to_EOF()
    {
        var rule = ZeroOrMore(AnyToken());
        var result = rule.Parse("anything at all 123 " + GuitarGrapheme);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(string.Concat(result.Symbols),
            Is.EqualTo("anything at all 123 " + GuitarGrapheme));
    }

    [Test]
    public void AnyToken_trace_success_produces_expected_output()
    {
        var sink = NewSink();
        AnyToken().Parse("q", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: 'q', Consumed: 1",
            "   SUCC | AnyToken: found 'q'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void AnyToken_trace_failure_produces_expected_output()
    {
        var sink = NewSink();
        AnyToken().Parse("", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: '<EOF>', Consumed: 0",
            "   FAIL | AnyToken: found '<EOF>'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }
}
