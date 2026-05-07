using System;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;
using static InductorParser.Tests.UnicodeExamples;

namespace InductorParser.Tests;

[TestFixture]
public class NoneOfRuleTests
{
    [Test]
    public void NoneOf_matches_a_rune_outside_the_set()
    {
        // 'x' isn't a digit, so NoneOf(Digits) succeeds on it.
        var rule = NoneOf(TokenSet.Digits);
        var result = rule.Parse("x");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("x"));
    }

    [Test]
    public void NoneOf_fails_when_rune_is_in_the_set()
    {
        // '5' is a digit, so NoneOf(Digits) fails at offset 0.
        var rule = NoneOf(TokenSet.Digits).WithError("no digits here");
        var result = rule.Parse("5");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("no digits here"));
    }

    [Test]
    public void NoneOf_fails_at_EOF()
    {
        // EOF isn't "a rune not in the set". It's no rune at all. Fail.
        var rule = NoneOf(TokenSet.Digits).WithError("wanted a non-digit");
        var result = rule.Parse("");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("wanted a non-digit"));
    }

    [Test]
    public void NoneOf_matches_multi_rune_Token()
    {
        // A multi-rune grapheme like LatinEAcuteGrapheme arrives as a
        // single token whose RuneValue is -1. The "not a single rune in
        // the set" predicate is trivially true for it: the token isn't
        // any single rune at all. This is the property that lets
        // ZeroOrMore(NoneOf(...)) sweep up arbitrary Unicode text.
        // NormalizeInput = null so the decomposed "e\u0301" arrives at the
        // lexer verbatim. The default NFC would compose it to "\u00E9" and
        // collapse this test's "multi-rune grapheme" premise.
        var rule = NoneOf(TokenSet.Ascii.Letters);
        rule.Compile(null);
        var result = rule.Parse(LatinEAcuteGrapheme);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(LatinEAcuteGrapheme));
    }

    [Test]
    public void NoneOf_sweeps_passthrough_text_up_to_a_delimiter()
    {
        // The pass-through-text idiom: ZeroOrMore(NoneOf(stopSet)) matches
        // everything that isn't in the stop set, then the surrounding rule
        // handles the stop character. Here the stop is a single '\n'.
        //
        // WARNING: this idiom is LF-only under grapheme tokenization.
        // A CRLF grapheme passes NoneOf unconditionally (it isn't a
        // single rune, so it can't be in any single-rune set), which
        // means the sweep silently consumes the CRLF and the trailing
        // Token('\n') terminator then fails. For real line-based
        // grammars, don't use NoneOf as the line sweep at all. Use
        // Not(EndOfLine()) + AnyToken() for the sweep and EndOfLine()
        // for the terminator, which together handle CRLF, LF, CR, NEL,
        // LS, and PS as one terminator each.
        // See docs/UnicodeGotchas.md § "CRLF Line Endings".
        var rule = And(
            ZeroOrMore(NoneOf(TokenSet.Single('\n'))),
            Token('\n'));

        // PreserveAllSymbols keeps the trailing Token('\n') in the
        // tree so Tree.ToString reproduces the full matched line.
        var result = rule.Parse("hello world\n",
            new ParseOptions { PreserveAllSymbols = true });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("hello world\n"));
    }

    [Test]
    [RecursiveEngineOnly]
    public void NoneOf_trace_success_produces_expected_output()
    {
        var sink = NewSink();
        NoneOf(TokenSet.Ascii.Digits).Parse("x",
            new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: 'x', Consumed: 1",
            "   SUCC | NoneOf: found 'x', wanted one not in '[0-9]'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    [RecursiveEngineOnly]
    public void NoneOf_trace_failure_produces_expected_output()
    {
        var sink = NewSink();
        NoneOf(TokenSet.Ascii.Digits).Parse("5",
            new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: '5', Consumed: 1",
            "   FAIL | NoneOf: found '5', wanted one not in '[0-9]'"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Sealed_NoneOf_rejects_Flatten()
    {
        var rule = NoneOf("abc");
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.Flatten(FlattenType.Preserve));
    }

    [Test]
    public void Sealed_NoneOf_rejects_WithError()
    {
        var rule = NoneOf("abc");
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.WithError("late"));
    }

    [Test]
    public void Sealed_NoneOf_rejects_As()
    {
        var rule = NoneOf("abc");
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.As("late"));
    }

    // Multi-rune grapheme support -------------------------------------------

    [Test]
    public void NoneOf_with_multi_rune_set_rejects_the_listed_Token()
    {
        // A multi-rune set as the exclude list. The flag arrives as
        // one token and NoneOf finds it in the multi-rune array, so
        // it fails. Other multi-rune graphemes pass.
        var rule = NoneOf(TokenSet.Runes(USFlagGrapheme));

        Assert.That(rule.Parse(USFlagGrapheme).Success, Is.False);
        Assert.That(rule.Parse(WomanShruggingGrapheme).Success, Is.True);
        // Single-rune tokens not in the set's runes also pass.
        Assert.That(rule.Parse("a").Success, Is.True);
    }

    [Test]
    public void NoneOf_with_mixed_set_rejects_both_listed_runes_and_listed_graphemes()
    {
        // The mixed-set version of the previous test: include a
        // letter range and a multi-rune entry. Tokens that hit
        // either get rejected.
        var rule = NoneOf(TokenSet.Ascii.Letters | TokenSet.Runes(USFlagGrapheme));

        Assert.That(rule.Parse("a").Success, Is.False, "letters are rejected");
        Assert.That(rule.Parse(USFlagGrapheme).Success, Is.False, "the flag grapheme is rejected");
        Assert.That(rule.Parse("1").Success, Is.True, "digits are not in the set");
        Assert.That(rule.Parse(WomanShruggingGrapheme).Success, Is.True, "other multi-rune graphemes pass");
    }

    [Test]
    public void Unnamed_NoneOf_uses_the_rune_value_as_the_leaf_id_for_single_rune_tokens()
    {
        // Same Name-gated leaf-Id story as OneOfRule.
        var rule = NoneOf(TokenSet.Ascii.Digits);
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Tree!.Id.Value, Is.EqualTo(0x61));
    }

    [Test]
    public void Named_NoneOf_uses_rule_id_so_Find_resolves_the_named_rule()
    {
        // .As("name") makes the rule findable in the tree, regardless of
        // whether the matched grapheme is one rune or several.
        var notDigit = NoneOf(TokenSet.Ascii.Digits).As("notDigit");
        var result = notDigit.Parse("a");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Tree!.Is(notDigit), Is.True);
        Assert.That(result.Tree!.Find(notDigit), Is.Not.Null);
    }

    [Test]
    public void NoneOf_with_multi_rune_match_uses_rule_id_regardless_of_naming()
    {
        // The matched cluster (regional-indicator US flag) is multi-rune,
        // so Token.RuneValue == -1 and the leaf carries the rule's own Id
        // whether the rule is named or not.
        var unnamedRule = NoneOf(TokenSet.Ascii.Digits);
        var unnamedResult = unnamedRule.Parse(USFlagGrapheme);
        Assert.That(unnamedResult.Success, Is.True);
        Assert.That(unnamedResult.Tree!.Id, Is.EqualTo(unnamedRule.Id));
        Assert.That(unnamedResult.Tree!.Find(unnamedRule), Is.Not.Null);
        Assert.That(unnamedRule.NameOf(unnamedResult.Tree!.Id), Is.EqualTo("NoneOf"));

        var namedRule = NoneOf(TokenSet.Ascii.Digits).As("notDigit");
        var namedResult = namedRule.Parse(USFlagGrapheme);
        Assert.That(namedResult.Success, Is.True);
        Assert.That(namedResult.Tree!.Id, Is.EqualTo(namedRule.Id));
        Assert.That(namedResult.Tree!.Find(namedRule), Is.Not.Null);
        Assert.That(namedRule.NameOf(namedResult.Tree!.Id), Is.EqualTo("notDigit"));
    }

    [Test]
    public void OneOrMore_NoneOf_admits_multi_rune_cluster_starting_with_a_set_rune()
    {
        // BetweenInclusiveRule peeks the next rune and consults
        // Inner.CannotMatchLookahead before iterating. The input here
        // is one grapheme cluster (a + combining acute under
        // Compile(null)). NoneOf admits it, because a multi-rune cluster
        // is rejected only when its full chars are in the set's
        // multi-rune part, and {'a'} has none. So the rune-set
        // lookahead can't soundly exclude 'a' for NoneOf, even though
        // 'a' alone would be rejected as a single-rune token.
        var rule = OneOrMore(NoneOf(TokenSet.Single('a')));
        rule.Compile(null);
        var result = rule.Parse("a" + CombiningAcuteText);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("a" + CombiningAcuteText));
    }

    [Test]
    public void Or_NoneOf_admits_multi_rune_cluster_starting_with_a_set_rune()
    {
        // OrRule has the same CannotMatchLookahead shortcut as
        // BetweenInclusiveRule, so NoneOf has to admit any peeked first
        // rune in this context too. The literal alternative is
        // unreachable here: NoneOf has to be the one that matches.
        var rule = Or(NoneOf(TokenSet.Single('a')), Literal("zzzZZZ"));
        rule.Compile(null);
        var result = rule.Parse("a" + CombiningAcuteText);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("a" + CombiningAcuteText));
    }
}
