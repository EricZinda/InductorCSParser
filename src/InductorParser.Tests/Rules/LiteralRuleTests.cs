using System;
using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;
using static InductorParser.Tests.UnicodeExamples;

namespace InductorParser.Tests;

[TestFixture]
public class LiteralRuleTests
{
    [Test]
    public void Literal_matches_single_rune_ascii_string()
    {
        var rule = Literal("a");
        var result = rule.Parse("a");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Literal_matches_multi_character_ascii_word()
    {
        var rule = Literal("major");
        var result = rule.Parse("major");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Literal_matches_multi_rune_grapheme_under_grapheme_lexer()
    {
        // Under GraphemeLexer the e+combining-acute is one token whose
        // Chars span covers both chars. The lockstep compares the full
        // token span against the two-char expected slice in a single
        // iteration.
        var rule = Literal(LatinEAcuteGrapheme);
        var result = rule.Parse(LatinEAcuteGrapheme,
            new ParseOptions { NormalizeInput = null });
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Literal_matches_multi_rune_grapheme_under_rune_lexer()
    {
        // Under RuneLexer the same input tokenizes as two rune tokens.
        // Same expected string, same lockstep loop, two iterations.
        var rule = Literal(LatinEAcuteGrapheme);
        var result = rule.Parse(LatinEAcuteGrapheme,
            new ParseOptions { InputUnit = InputUnit.Rune, NormalizeInput = null });
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Literal_matches_word_then_grapheme_then_word()
    {
        // Mixed width: a two-char BMP prefix, a supplementary-plane rune
        // (2 UTF-16 chars), and a BMP suffix. Under GraphemeLexer the
        // guitar emoji is one token of length 2; the lockstep handles
        // variable token widths naturally.
        var literal = "hi" + GuitarGrapheme + "!";
        var rule = Literal(literal);
        var result = rule.Parse(literal);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Literal_rejects_mismatched_prefix_at_first_token()
    {
        var rule = Literal("major").WithError("expected 'major'");
        var result = rule.Parse("xajor");
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected 'major'"));
    }

    [Test]
    public void Literal_rejects_mismatched_middle_at_failing_token_position()
    {
        // The points-at-offender test: first three chars match, fourth
        // doesn't. Pre-read position for that failing read is 3, not 0.
        var rule = Literal("major").WithError("expected 'major'");
        var result = rule.Parse("majxr");
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected 'major'"));
    }

    [Test]
    public void Literal_rejects_eof_midway_at_eof_position()
    {
        // Input runs out in the middle of the expected string. The EOF
        // read happens at offset 3, so the failure records there.
        var rule = Literal("major").WithError("expected 'major'");
        var result = rule.Parse("maj");
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected 'major'"));
    }

    [Test]
    public void Literal_rejects_empty_input()
    {
        var rule = Literal("hi").WithError("expected 'hi'");
        var result = rule.Parse("");
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected 'hi'"));
    }

    [Test]
    public void Literal_rejects_empty_string_at_construction()
    {
        var ex = Assert.Throws<ArgumentException>(() => Literal(""));
        Assert.That(ex!.Message, Does.Contain("non-empty"));
    }

    [Test]
    public void Literal_symbol_text_round_trips_via_ToString()
    {
        // Leaf symbol carries a Memory slice over the matched input. The
        // ToString round-trip should produce the original literal.
        var rule = Literal("select").Flatten(SyntaxTree.FlattenType.Preserve);
        var result = rule.Parse("select");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("select"));
    }

    [Test]
    public void Literal_in_composite_reports_offender_in_context()
    {
        // Prefix And-member consumes "select " then Literal fails at offset 7.
        // Confirms Literal's RecordFailure uses the token-local pre-read
        // position, not transaction.StartPosition of some outer frame.
        var rule = And(
            Literal("select "),
            Literal("FROM").WithError("expected 'FROM'"));
        var result = rule.Parse("select XXXX");
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(7));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected 'FROM'"));
    }

    [Test]
    public void Literal_trace_success_produces_expected_output()
    {
        var sink = NewSink();
        Literal("hi").Parse("hi", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: 'h', Consumed: 1",
            "   Lexer.Read: 'i', Consumed: 2",
            "   SUCC | Literal: found 'hi'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Literal_trace_failure_produces_expected_output()
    {
        // Failure on the first token of the literal: deepest-failure stays
        // at 0, so RecordFailure doesn't trace, matching Token's fail-at-0
        // shape. The mid-literal-fail case is covered by the RecordFailure
        // trace line that appears when position advances past 0.
        var sink = NewSink();
        Literal("hi").Parse("xo", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: 'x', Consumed: 1",
            "   FAIL | Literal: found 'x', wanted 'hi'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void LiteralIgnoreAsciiCase_matches_same_case()
    {
        var rule = LiteralIgnoreAsciiCase("SELECT");
        var result = rule.Parse("SELECT");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void LiteralIgnoreAsciiCase_matches_different_case()
    {
        var rule = LiteralIgnoreAsciiCase("SELECT");
        Assert.That(rule.Parse("select").Success, Is.True);
        Assert.That(rule.Parse("Select").Success, Is.True);
        Assert.That(rule.Parse("sElEcT").Success, Is.True);
    }

    [Test]
    public void LiteralIgnoreAsciiCase_leaves_non_letter_chars_strict()
    {
        // The 0x20 bit difference between '[' and '{', '@' and '`', etc.
        // must NOT be treated as a case-insensitive match. Only A-Za-z
        // get that treatment.
        var rule = LiteralIgnoreAsciiCase("a[b");
        Assert.That(rule.Parse("A[B").Success, Is.True);
        Assert.That(rule.Parse("A{B").Success, Is.False);
    }

    [Test]
    public void LiteralIgnoreAsciiCase_rejects_non_ascii_case_variant()
    {
        // German sharp s does NOT match SS under this primitive (that
        // would require full Unicode case-insensitive matching, which
        // we deliberately don't do). Document the behavior by pinning it.
        var rule = LiteralIgnoreAsciiCase("straße");
        Assert.That(rule.Parse("STRASSE").Success, Is.False);
    }

    [Test]
    public void LiteralIgnoreAsciiCase_rejects_empty_string_at_construction()
    {
        Assert.Throws<ArgumentException>(() => LiteralIgnoreAsciiCase(""));
    }

    [Test]
    public void LiteralIgnoreAsciiCase_records_failure_at_failing_token_position()
    {
        var rule = LiteralIgnoreAsciiCase("major").WithError("expected 'major'");
        var result = rule.Parse("maJxr");
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected 'major'"));
    }

    [Test]
    public void LiteralIgnoreAsciiCase_trace_uses_LiteralIgnoreAsciiCase_label()
    {
        // The trace label must read "LiteralIgnoreAsciiCase", not "Literal",
        // so a reader can tell the two rule kinds apart in a mixed trace.
        var sink = NewSink();
        LiteralIgnoreAsciiCase("hi").Parse("HI", new ParseOptions { TraceSink = sink });
        Assert.That(sink.ToString(), Does.Contain("LiteralIgnoreAsciiCase"));
    }
}
