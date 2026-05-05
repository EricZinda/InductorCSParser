using System;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;
using static InductorParser.Tests.UnicodeExamples;

namespace InductorParser.Tests;

[TestFixture]
public class EofRuleTests
{
    [Test]
    public void Eof_at_end_of_input_succeeds()
    {
        // Control case: Eof() on empty input succeeds with no error.
        var rule = Eof();
        var result = rule.Parse("");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Eof_fails_at_current_position_when_input_has_content()
    {
        // Token('a') matches. Eof() runs at offset 1 and finds 'b' there,
        // records its WithError at the current position.
        var rule = AllOf(Token('a').WithError("need an 'a'"),
                       Eof().WithError("expected end of input"));

        var result = rule.Parse("ab");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected end of input"));
    }

    [Test]
    public void Eof_failure_without_WithError_falls_back_to_positional_message()
    {
        // No WithError anywhere. Eof records a null message at offset 1 and
        // BuildErrorMessage's positional fallback renders the message.
        var rule = AllOf(Token('a'), Eof());

        var result = rule.Parse("ab");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
        Assert.That(result.ErrorMessage, Does.StartWith("Parse failed at offset 1"));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Eof_trace_success_produces_expected_output()
    {
        // EofRule doesn't open a transaction and top-level Parse doesn't
        // either, so the success line sits at depth 0 with no leading
        // indentation. The message is empty, so there's no ": {detail}"
        // tail either. The line reads simply "SUCC | Eof".
        var sink = NewSink();
        Eof().Parse("", new ParseOptions { TraceSink = sink });

        string expected = Lines("SUCC | Eof");
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Eof_trace_failure_produces_expected_output()
    {
        // Wrapped in AllOf so there's a transaction open when Eof fails,
        // giving us a non-trivial indentation to verify.
        var sink = NewSink();
        AllOf(Eof()).Parse("x", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   FAIL | Eof: found x",
            "   FAIL | AllOf: symbol #0"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Eof_trace_failure_renders_full_grapheme_for_supplementary_plane()
    {
        // The unconsumed character in EofRule's failure trace should be
        // the full token the lexer would have read, not the first UTF-16
        // code unit. A waving-hand emoji is one rune / one grapheme but
        // two UTF-16 chars (a surrogate pair), so input[position] is the
        // lone high surrogate and rendering that lies about what the
        // parser actually saw. Same shape as the {character}-placeholder
        // bug fixed in BuildErrorMessage; trace lines that quote the
        // current token are the next instance documented in
        // PotentialBugSources.md "Char-unit rendering in user-facing strings."
        var sink = NewSink();
        AllOf(Eof()).Parse(WavingHandGrapheme, new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   FAIL | Eof: found " + WavingHandGrapheme,
            "   FAIL | AllOf: symbol #0"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Eof_trace_failure_renders_full_grapheme_for_decomposed_cluster()
    {
        // Under Compile(null), a decomposed grapheme like "e + combining
        // acute" stays as two chars / one cluster. EofRule's trace should
        // report the full cluster the lexer would have read, not just
        // the first rune. Without this, a parse failure trace shows "e"
        // for an input the user perceives as "é".
        var sink = NewSink();
        var rule = AllOf(Eof());
        rule.Compile(null);
        rule.Parse(LatinEAcuteGrapheme, new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   FAIL | Eof: found " + LatinEAcuteGrapheme,
            "   FAIL | AllOf: symbol #0"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Sealed_Eof_rejects_Flatten()
    {
        var rule = Eof();
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.Flatten(FlattenType.Preserve));
    }

    [Test]
    public void Sealed_Eof_rejects_WithError()
    {
        var rule = Eof();
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.WithError("late"));
    }

    [Test]
    public void Sealed_Eof_rejects_As()
    {
        var rule = Eof();
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.As("late"));
    }
}
