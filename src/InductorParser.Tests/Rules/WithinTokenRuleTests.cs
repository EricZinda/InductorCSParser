using System;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;
using static InductorParser.Tests.UnicodeExamples;

namespace InductorParser.Tests;

// Tests for Rules.WithinToken, the combinator that runs an inner rule
// against the runes inside one outer token. Used by Identifier() to handle
// Devanagari / Thai / Arabic-with-vowels under the grapheme-cluster lexer,
// but usable by any grammar that needs to validate grapheme-internal
// structure (emoji sequences, Hangul jamo clusters, ASCII strictness).
[TestFixture]
public class WithinTokenRuleTests
{
    [Test]
    public void Single_rune_grapheme_matches_inner_rune_rule()
    {
        var rule = WithinToken(OneOf(TokenSet.Ascii.Letters));
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("a"));
    }

    [Test]
    public void Single_rune_grapheme_fails_when_inner_rejects()
    {
        var rule = WithinToken(OneOf(TokenSet.Ascii.Letters));
        var result = rule.Parse("3");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
    }

    [Test]
    public void Multi_rune_grapheme_matches_when_inner_consumes_all_runes()
    {
        // "é" as e + combining acute is one grapheme containing two runes.
        // The inner rule accepts both in order: a letter then a combining
        // mark. Uses NormalizeInput=null so the decomposed form survives
        // to the lexer.
        var rule = WithinToken(And(
            OneOf(TokenSet.Ascii.Letters),
            OneOf(TokenSet.Category(System.Globalization.UnicodeCategory.NonSpacingMark))
        ));
        rule.Compile(null);
        var result = rule.Parse(LatinEAcuteGrapheme);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(LatinEAcuteGrapheme));
    }

    [Test]
    public void Multi_rune_grapheme_fails_when_inner_matches_only_prefix()
    {
        // A grapheme is atomic. If the inner rule matches just the first
        // rune and leaves the combining mark unconsumed, the whole
        // WithinToken fails rather than accepting a partial match.
        var rule = WithinToken(OneOf(TokenSet.Ascii.Letters));
        rule.Compile(null);
        var result = rule.Parse(LatinEAcuteGrapheme);

        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void Fails_at_end_of_input()
    {
        var rule = WithinToken(AnyToken()).WithError("wanted a grapheme");
        var result = rule.Parse("");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("wanted a grapheme"));
    }

    [Test]
    public void Composes_into_zero_or_more_for_multi_grapheme_sequences()
    {
        // ZeroOrMore(WithinToken(letter)) walks a sequence of single-
        // rune graphemes. Proves the combinator composes into the normal
        // repeat combinators without special handling.
        var rule = ZeroOrMore(WithinToken(OneOf(TokenSet.Ascii.Letters)))
            .Flatten(FlattenType.Preserve)
            .As("letters");
        var result = rule.Parse("abc");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("abc"));
    }

    [Test]
    public void Emits_exactly_one_leaf_per_grapheme_regardless_of_inner_structure()
    {
        // The inner rule is a two-piece And, but WithinToken hides
        // that and emits a single leaf Symbol for the whole grapheme.
        // Grammar authors can rely on WithinToken looking like a leaf
        // from the outside.
        var rule = WithinToken(And(
            OneOf(TokenSet.Ascii.Letters),
            OneOf(TokenSet.Category(System.Globalization.UnicodeCategory.NonSpacingMark))
        ));
        rule.Compile(null);
        var result = rule.Parse(LatinEAcuteGrapheme);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        // One Symbol in the tree representing the whole grapheme. No
        // children from the inner And / OneOf pair.
        Assert.That(result.Tree!.Children.Count, Is.EqualTo(0));
    }

    [Test]
    public void Reject_multi_rune_graphemes_idiom_works()
    {
        // WithinToken(OneOf(singleTokenSet)) is the idiomatic "reject
        // any multi-rune grapheme" rule. Passes on ASCII, fails on
        // precomposed "é" (single rune but not ASCII), fails on
        // decomposed "é" (two runes).
        var asciiOnly = WithinToken(OneOf(TokenSet.Ascii.Letters));
        var asciiOnlyNoNorm = WithinToken(OneOf(TokenSet.Ascii.Letters));
        asciiOnlyNoNorm.Compile(null);

        Assert.That(asciiOnly.Parse("a").Success, Is.True);
        Assert.That(asciiOnly.Parse("é").Success, Is.False);  // é isn't ASCII
        Assert.That(asciiOnlyNoNorm.Parse(LatinEAcuteGrapheme).Success,
            Is.False);  // two runes
    }

    // The three tests below are the real-world reason WithinToken
    // exists. Devanagari, Thai, and Arabic-with-vowels all produce
    // multi-rune graphemes unconditionally (NFC doesn't compose them),
    // which is the case the Latin-decomposed tests above only simulate
    // via NormalizeInput=null. These tests run under the lexer's
    // grapheme-cluster tokenization and the default NFC normalization.

    [Test]
    public void Devanagari_consonant_plus_vowel_sign_grapheme_matches()
    {
        // "हि" is one grapheme, two runes:
        // U+0939 DEVANAGARI LETTER HA (Lo) + U+093F DEVANAGARI VOWEL SIGN I (Mc).
        // The inner rule walks both runes.
        var rule = WithinToken(And(
            OneOf(TokenSet.XidStart),
            OneOf(TokenSet.XidContinue)));
        var result = rule.Parse("हि");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("हि"));
    }

    [Test]
    public void Thai_consonant_plus_sara_am_grapheme_matches()
    {
        // "กำ" is one grapheme, two runes: U+0E01 THAI CHARACTER KO KAI
        // (Lo, in XID_Start) + U+0E33 THAI CHARACTER SARA AM (in
        // XID_Continue, excluded from XID_Start via the NFKC-unstable
        // table since its NFKC decomposition is NIKHAHIT + SARA AA).
        var rule = WithinToken(And(
            OneOf(TokenSet.XidStart),
            OneOf(TokenSet.XidContinue)));
        var result = rule.Parse("กำ");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("กำ"));
    }

    [Test]
    public void Arabic_consonant_plus_fatha_grapheme_matches()
    {
        // "كَ" is one grapheme, two runes: U+0643 ARABIC LETTER KAF (Lo) +
        // U+064E ARABIC FATHA (Mn). Represents the common case of Arabic
        // text written with the optional vowel diacritics, which bundle
        // with their preceding consonant under grapheme clustering.
        var rule = WithinToken(And(
            OneOf(TokenSet.XidStart),
            OneOf(TokenSet.XidContinue)));
        var result = rule.Parse("كَ");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("كَ"));
    }

    [Test]
    public void Sealed_WithinToken_rejects_Flatten()
    {
        var rule = WithinToken(Token('a'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.Flatten(FlattenType.Preserve));
    }

    [Test]
    public void Sealed_WithinToken_rejects_WithError()
    {
        var rule = WithinToken(Token('a'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.WithError("late"));
    }

    [Test]
    public void Sealed_WithinToken_rejects_As()
    {
        var rule = WithinToken(Token('a'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.As("late"));
    }

    [Test]
    public void ScanUntil_inside_WithinToken_consumes_one_rune_token_with_trailing_input()
    {
        // A no-stopper / no-escape ScanUntil iteration calls no TryParse,
        // so RuleCountLimit / Timeout / Cancellation don't trip if the
        // rule fails to make progress. The Task.Wait is the external
        // catch: a regression that breaks the sub-lexer's end-of-input
        // bound shows up as a wait timeout, not a hung test runner.
        var rule = WithinToken(ScanUntil(TokenSet.Runes("?")));
        var options = new ParseOptions { AllowTrailingInput = true };

        var task = System.Threading.Tasks.Task.Run(() => rule.Parse("aX", options));
        bool completed = task.Wait(TimeSpan.FromSeconds(5));
        Assert.That(completed, Is.True, "WithinToken(ScanUntil(...)) hung past its outer token");

        var result = task.Result;
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("a"));
    }

    [Test]
    public void ScanUntil_inside_WithinToken_consumes_every_rune_of_a_multi_rune_cluster()
    {
        // Compile(null) so the decomposed e + combining acute survives
        // to the lexer as one two-rune cluster instead of being
        // precomposed away by NFC.
        var rule = WithinToken(ScanUntil(TokenSet.Runes("?")));
        rule.Compile(null);
        var options = new ParseOptions { AllowTrailingInput = true };

        var task = System.Threading.Tasks.Task.Run(
            () => rule.Parse(LatinEAcuteGrapheme + "X", options));
        bool completed = task.Wait(TimeSpan.FromSeconds(5));
        Assert.That(completed, Is.True, "WithinToken(ScanUntil(...)) hung past its outer token");

        var result = task.Result;
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(LatinEAcuteGrapheme));
    }

    [Test]
    public void WithinToken_partial_inner_match_reports_outer_cluster_position()
    {
        // WithinToken on a multi-rune cluster ("é" decomposed = 'e' +
        // combining acute) where the inner rule consumes only the first
        // rune. The whole grapheme is one outer token, so the failure
        // belongs at offset 0 (the cluster's start), not offset 1 (mid-
        // cluster, INSIDE the outer token). Asserts every position unit
        // because the rest of the parser only ever reports cluster-
        // boundary positions and a divergence here means user-visible
        // diagnostics lie about where the parser was looking.
        var rule = WithinToken(OneOf(TokenSet.Ascii.Letters));
        rule.Compile(null);
        var result = rule.Parse(LatinEAcuteGrapheme);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0),
            "failing cluster starts at offset 0; offset 1 is inside the cluster");
        Assert.That(result.ErrorTokenIndex, Is.EqualTo(0),
            "input has exactly one token; an index of 1 is past-the-end");
        Assert.That(result.ErrorLine, Is.EqualTo(0));
        Assert.That(result.ErrorColumn, Is.EqualTo(0));
        Assert.That(result.ErrorPosition!.Value.CharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorPosition!.Value.TokenIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage,
            Is.EqualTo("Parse failed at offset 0: unexpected '" + LatinEAcuteGrapheme + "'."));
    }

    [Test]
    public void WithinToken_inner_composite_failure_reports_outer_cluster_position()
    {
        // The other failure path: inner is an And whose first child
        // succeeds and advances the sub-lexer past the first rune, then
        // the second child fails. WithinTokenRule's inner-failure branch
        // reads subLexer.DeepestFailure (set by the second child at the
        // rune offset where it failed) and feeds that mid-cluster offset
        // to outerLexer.RecordFailure. Same outer-view invariant
        // violation as the prefix-match path above.
        var rule = WithinToken(And(Token('e'), Token('b')));
        rule.Compile(null);
        var result = rule.Parse(LatinEAcuteGrapheme);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorTokenIndex, Is.EqualTo(0));
        Assert.That(result.ErrorLine, Is.EqualTo(0));
        Assert.That(result.ErrorColumn, Is.EqualTo(0));
        Assert.That(result.ErrorMessage,
            Is.EqualTo("Parse failed at offset 0: unexpected '" + LatinEAcuteGrapheme + "'."));
    }

    [Test]
    public void Named_rule_is_findable_in_tree_for_single_rune_match()
    {
        // A WithinToken rule named via .As("...") emits a leaf whose
        // Id is the rule's own Id, regardless of the matched grapheme's
        // rune count. Tree.Find(rule) and Tree.Is(rule) walk the tree
        // comparing Symbol.Id against rule.Id, so the named rule is
        // findable by reference for both single-rune and multi-rune
        // matches.
        var character = WithinToken(OneOf(TokenSet.Ascii.Letters)).As("character");

        var asciiResult = character.Parse("a");
        Assert.That(asciiResult.Success, Is.True);
        Assert.That(asciiResult.Tree!.Is(character), Is.True,
            "single-rune match: leaf should carry the rule's Id so Tree.Is(rule) succeeds");
        Assert.That(asciiResult.Tree!.Find(character), Is.Not.Null,
            "single-rune match: Tree.Find(rule) should locate the leaf");
    }

    [Test]
    public void Named_rule_is_findable_for_both_single_and_multi_rune_inputs_consistently()
    {
        // Same rule, different inputs. The leaf carries the rule's own
        // Id regardless of whether the matched grapheme is one rune
        // (ASCII "a") or several (Devanagari "हि"), so Find resolves
        // the same way in both cases.
        var character = WithinToken(Or(
            And(OneOf(TokenSet.XidStart), OneOf(TokenSet.XidContinue)),
            OneOf(TokenSet.Ascii.Letters))).As("character");

        var devResult = character.Parse("हि");
        Assert.That(devResult.Success, Is.True);
        Assert.That(devResult.Tree!.Id, Is.EqualTo(character.Id));
        Assert.That(devResult.Tree!.Find(character), Is.Not.Null);

        var asciiResult = character.Parse("a");
        Assert.That(asciiResult.Success, Is.True);
        Assert.That(asciiResult.Tree!.Id, Is.EqualTo(character.Id));
        Assert.That(asciiResult.Tree!.Find(character), Is.Not.Null);
    }

    [Test]
    public void WithinToken_with_multi_rune_match_uses_rule_id_regardless_of_naming()
    {
        // For a multi-rune outer token (Token.RuneValue == -1), the leaf
        // carries the rule's own Id regardless of naming. Locks the
        // multi-rune branch so Find resolves through rule.Id for unnamed
        // rules too.
        var unnamedRule = WithinToken(And(OneOf(TokenSet.XidStart), OneOf(TokenSet.XidContinue)));
        var unnamedResult = unnamedRule.Parse("हि");
        Assert.That(unnamedResult.Success, Is.True);
        Assert.That(unnamedResult.Tree!.Id, Is.EqualTo(unnamedRule.Id));
        Assert.That(unnamedResult.Tree!.Find(unnamedRule), Is.Not.Null);
        Assert.That(unnamedRule.NameOf(unnamedResult.Tree!.Id), Is.EqualTo("WithinToken"));

        var namedRule = WithinToken(And(OneOf(TokenSet.XidStart), OneOf(TokenSet.XidContinue)))
            .As("character");
        var namedResult = namedRule.Parse("हि");
        Assert.That(namedResult.Success, Is.True);
        Assert.That(namedResult.Tree!.Id, Is.EqualTo(namedRule.Id));
        Assert.That(namedResult.Tree!.Find(namedRule), Is.Not.Null);
        Assert.That(namedRule.NameOf(namedResult.Tree!.Id), Is.EqualTo("character"));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Or_WithinToken_skips_when_peek_first_rune_is_outside_inner_set()
    {
        // WithinToken forwards the inner rule's first-token set with
        // Advance.Always. The inner OneOf({a..z}) gives a first-rune set
        // covering ASCII lowercase. Peek '1' isn't in that set, so the
        // shortcut skips WithinToken and the second branch wins.
        var sink = NewSink();
        var rule = Or(WithinToken(OneOf(TokenSet.Ascii.Letters)), Literal("1"));
        var result = rule.Parse("1", new ParseOptions { TraceSink = sink });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(sink.ToString(), Does.Contain("SKIP | WithinToken:"));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Or_WithinToken_runs_when_peek_first_rune_is_in_inner_set()
    {
        // Peek 'a' is in the inner's first-rune set, so the shortcut
        // doesn't skip. WithinToken runs and the inner rule consumes
        // the cluster.
        var sink = NewSink();
        var rule = Or(WithinToken(OneOf(TokenSet.Ascii.Letters)), Literal("1"));
        var result = rule.Parse("a", new ParseOptions { TraceSink = sink });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(sink.ToString(), Does.Not.Contain("SKIP | WithinToken:"));
    }
}
