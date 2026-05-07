using System;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;
using static InductorParser.Tests.UnicodeExamples;

namespace InductorParser.Tests;

[TestFixture]
public class OneOfRuleTests
{
    [Test]
    public void OneOf_matches_a_letter_and_returns_a_single_rune_symbol()
    {
        var rule = OneOf(TokenSet.Letters);
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("a"));
    }

    [Test]
    public void OneOf_mismatch_without_WithError_falls_back_to_positional_message()
    {
        // No WithError anywhere, so OneOf records a null message at offset
        // 0 and BuildErrorMessage's positional fallback decides what to say.
        var rule = OneOrMore(OneOf(TokenSet.Letters));
        var result = rule.Parse("1abc");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Does.StartWith("Parse failed at offset 0"));
    }

    [Test]
    public void OneOf_EOF_on_empty_input_points_at_zero()
    {
        // OneOrMore requires at least one letter. Empty input can't satisfy
        // that. OneOf sees EOF on its first read and records at its pre-
        // read position 0 with its WithError message.
        var rule = OneOrMore(OneOf(TokenSet.Letters).WithError("need a letter"));

        var result = rule.Parse("");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("need a letter"));
    }

    [Test]
    public void OneOf_mismatch_at_start_points_at_offender()
    {
        // '1' is at offset 0. Not a letter. OneOf records its WithError
        // message at pre-read position 0.
        var rule = OneOrMore(OneOf(TokenSet.Letters).WithError("need a letter"));

        var result = rule.Parse("1abc");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("need a letter"));
    }

    [Test]
    public void OneOf_mismatch_after_successful_matches_points_at_first_bad_char()
    {
        // OneOrMore(Letters) commits "abc" up to offset 3. Then Token(';')
        // runs at offset 3, reads '1', and records its own WithError at
        // pre-read offset 3. That's deeper than the letter's WithError
        // (which is at offset 3 too, from the OneOrMore's terminating
        // attempt, but recorded first). First-writer at equal depth wins.
        //
        // To make the test unambiguous we only put a WithError on Token(';')
        // so there's no contention.
        var rule = AllOf(OneOrMore(OneOf(TokenSet.Letters)),
                       Token(';').WithError("expected ';'"));

        var result = rule.Parse("abc1");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected ';'"));
    }

    [Test]
    [RecursiveEngineOnly]
    public void OneOf_trace_success_produces_expected_output()
    {
        var sink = NewSink();
        OneOf(TokenSet.Ascii.Letters).Parse("x", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: 'x', Consumed: 1",
            "   SUCC | OneOf: found 'x', wanted one of '[A-Z,a-z]'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    [RecursiveEngineOnly]
    public void OneOf_trace_failure_produces_expected_output()
    {
        var sink = NewSink();
        OneOf(TokenSet.Ascii.Letters).Parse("1", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: '1', Consumed: 1",
            "   FAIL | OneOf: found '1', wanted one of '[A-Z,a-z]'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Sealed_OneOf_rejects_Flatten()
    {
        var rule = OneOf("abc");
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.Flatten(FlattenType.Preserve));
    }

    [Test]
    public void Sealed_OneOf_rejects_WithError()
    {
        var rule = OneOf("abc");
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.WithError("late"));
    }

    [Test]
    public void Sealed_OneOf_rejects_As()
    {
        var rule = OneOf("abc");
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.As("late"));
    }

    // Multi-rune grapheme support -------------------------------------------

    [Test]
    public void OneOf_matches_a_multi_rune_grapheme_under_grapheme_lexer()
    {
        // OneOf(set) where set has multi-rune entries: the lexer
        // hands back the whole grapheme as one token with RuneValue
        // == -1, and OneOf uses the multi-rune-array path to match it.
        // NormalizeInput stays default; the test inputs aren't
        // affected by NFC.
        var rule = OneOf(TokenSet.Runes(USFlagGrapheme + WomanShruggingGrapheme));

        Assert.That(rule.Parse(USFlagGrapheme).Success, Is.True);
        Assert.That(rule.Parse(WomanShruggingGrapheme).Success, Is.True);
        // A different multi-rune grapheme isn't a member.
        Assert.That(rule.Parse(SkinTonedWaveGrapheme).Success, Is.False);
        // EOF still fails.
        Assert.That(rule.Parse("").Success, Is.False);
    }

    [Test]
    public void OneOf_mixed_set_matches_both_letters_and_a_multi_rune_Token()
    {
        // Letters | Runes(USFlag) is the canonical mixed set: a
        // big rune-only class plus a single multi-rune entry. OneOf
        // uses the rune intervals for letter tokens and the
        // multi-rune array for the flag token.
        var rule = OneOf(TokenSet.Letters | TokenSet.Runes(USFlagGrapheme));

        Assert.That(rule.Parse("a").Success, Is.True);
        Assert.That(rule.Parse(USFlagGrapheme).Success, Is.True);
        // A digit isn't a letter and isn't the flag.
        Assert.That(rule.Parse("1").Success, Is.False);
    }

    [Test]
    public void Unnamed_OneOf_uses_the_rune_value_as_the_leaf_id_for_single_rune_tokens()
    {
        // The documented unnamed-OneOf optimization: tree consumers can
        // switch on which rune matched without going through a synthetic
        // per-OneOf id. An unnamed rule has Name == null, so the leaf
        // carries the rune's code point directly.
        var rule = OneOf(TokenSet.Ascii.Letters);
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Tree!.Id.Value, Is.EqualTo(0x61),
            "unnamed OneOf: leaf carries the matched rune's code point");
    }

    [Test]
    public void Named_OneOf_uses_rule_id_so_Find_resolves_the_named_rule()
    {
        // A named OneOf carries the rule's Id on every leaf. Tree.Find,
        // Tree.Is, and NameOf all resolve through the rule reference.
        var letter = OneOf(TokenSet.Ascii.Letters).As("letter");
        var result = letter.Parse("a");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Tree!.Is(letter), Is.True);
        Assert.That(result.Tree!.Find(letter), Is.Not.Null);
    }

    [Test]
    public void OneOf_with_multi_rune_match_uses_rule_id_regardless_of_naming()
    {
        // For a multi-rune match (Token.RuneValue == -1), the leaf carries
        // the rule's own Id either way: there's no rune code point that
        // fits in one int, so the rune-as-leaf-id branch can't fire.
        // Find and Is resolve through rule.Id for both unnamed and named
        // shapes.
        var unnamedRule = OneOf(TokenSet.Runes(USFlagGrapheme));
        var unnamedResult = unnamedRule.Parse(USFlagGrapheme);
        Assert.That(unnamedResult.Success, Is.True);
        Assert.That(unnamedResult.Tree!.Id, Is.EqualTo(unnamedRule.Id));
        Assert.That(unnamedResult.Tree!.Find(unnamedRule), Is.Not.Null);
        Assert.That(unnamedRule.NameOf(unnamedResult.Tree!.Id), Is.EqualTo("OneOf"));

        var namedRule = OneOf(TokenSet.Runes(USFlagGrapheme)).As("flag");
        var namedResult = namedRule.Parse(USFlagGrapheme);
        Assert.That(namedResult.Success, Is.True);
        Assert.That(namedResult.Tree!.Id, Is.EqualTo(namedRule.Id));
        Assert.That(namedResult.Tree!.Find(namedRule), Is.Not.Null);
        Assert.That(namedRule.NameOf(namedResult.Tree!.Id), Is.EqualTo("flag"));
    }

}
