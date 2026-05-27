using System;
using System.Text;
using NUnit.Framework;
using InductorParser;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;
using InductorParser.Tracing;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;
using static InductorParser.Tests.UnicodeExamples;

using static InductorParser.Tests.CanaryHelper;
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
    public void Inner_WithError_surfaces_when_WithinToken_fails_without_its_own_WithError()
    {
        // WithinToken doesn't clear inner failures on success, and when
        // it fails (inner sub-rule failed inside the token), the inner's
        // failure survives. Verifies the inner sub-rule's
        // WithError is what the user sees if WithinToken itself has no
        // WithError.
        var rule = WithinToken(Literal("ab").WithError("inner literal failed"));
        var result = rule.Parse("c");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("inner literal failed"));
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
        Assert.That(asciiOnly.Parse(UnicodeExamples.LatinEAcutePrecomposedGrapheme).Success, Is.False);  // é isn't ASCII
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
        var result = rule.Parse(Canary("हि", "devanagari letter ha + devanagari vowel sign i", 0x0939, 0x093F));

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(Canary("हि", "devanagari letter ha + devanagari vowel sign i", 0x0939, 0x093F)));
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
        var result = rule.Parse(UnicodeExamples.ThaiKamGrapheme);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(UnicodeExamples.ThaiKamGrapheme));
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
        var result = rule.Parse(Canary("كَ", "arabic letter kaf + arabic fatha", 0x0643, 0x064E));

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(Canary("كَ", "arabic letter kaf + arabic fatha", 0x0643, 0x064E)));
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
        // Inside WithinToken, the sub-lexer's end-of-bound is conceptually
        // the inner rule's EOF, and the inner ScanUntil should succeed
        // there (otherwise the sub-token's run-to-end has no way to
        // complete). eofIsTerminator: true gives it that semantics.
        var rule = WithinToken(ScanUntil(TokenSet.Runes("?"), eofIsTerminator: true));
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
        // Inside WithinToken, the sub-lexer's end-of-bound is conceptually
        // the inner rule's EOF, and the inner ScanUntil should succeed
        // there (otherwise the sub-token's run-to-end has no way to
        // complete). eofIsTerminator: true gives it that semantics.
        var rule = WithinToken(ScanUntil(TokenSet.Runes("?"), eofIsTerminator: true));
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

        var devResult = character.Parse(Canary("हि", "devanagari letter ha + devanagari vowel sign i", 0x0939, 0x093F));
        Assert.That(devResult.Success, Is.True);
        Assert.That(devResult.Tree!.Id, Is.EqualTo(character.Id));
        Assert.That(devResult.Tree!.Find(character), Is.Not.Null);

        var asciiResult = character.Parse("a");
        Assert.That(asciiResult.Success, Is.True);
        Assert.That(asciiResult.Tree!.Id, Is.EqualTo(character.Id));
        Assert.That(asciiResult.Tree!.Find(character), Is.Not.Null);
    }

    [Test]
    public void WithinToken_with_explicit_SymbolId_uses_explicit_id_for_single_rune_outer_token()
    {
        // .As(SymbolId) is the user's "set a stable id" signal, parallel
        // to .As("name") for findability. The leaf has to carry the
        // explicit id so Tree.Find / Tree.Is resolve through the user's
        // explicit reference. Same shape as the OneOf explicit-id test.
        var explicitId = new SymbolId(SymbolRanges.CustomRangeStart + 103);
        var rule = WithinToken(OneOf(TokenSet.Ascii.Letters)).As(explicitId);
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Tree!.Id, Is.EqualTo(explicitId),
            "leaf carries the user's explicit SymbolId, not the rune value");
        Assert.That(result.Tree!.Is(rule), Is.True);
        Assert.That(result.Tree!.Find(rule), Is.Not.Null);
    }

    [Test]
    public void WithinToken_with_multi_rune_match_uses_rule_id_regardless_of_naming()
    {
        // For a multi-rune outer token (Token.RuneValue == -1), the leaf
        // carries the rule's own Id regardless of naming. Locks the
        // multi-rune branch so Find resolves through rule.Id for unnamed
        // rules too.
        var unnamedRule = WithinToken(And(OneOf(TokenSet.XidStart), OneOf(TokenSet.XidContinue)));
        var unnamedResult = unnamedRule.Parse(Canary("हि", "devanagari letter ha + devanagari vowel sign i", 0x0939, 0x093F));
        Assert.That(unnamedResult.Success, Is.True);
        Assert.That(unnamedResult.Tree!.Id, Is.EqualTo(unnamedRule.Id));
        Assert.That(unnamedResult.Tree!.Find(unnamedRule), Is.Not.Null);
        Assert.That(unnamedRule.NameOf(unnamedResult.Tree!.Id), Is.EqualTo("WithinToken"));

        var namedRule = WithinToken(And(OneOf(TokenSet.XidStart), OneOf(TokenSet.XidContinue)))
            .As("character");
        var namedResult = namedRule.Parse(Canary("हि", "devanagari letter ha + devanagari vowel sign i", 0x0939, 0x093F));
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

    [Test]
    public void Or_WithinToken_does_not_skip_when_inner_negative_rule_walks_a_multi_rune_cluster()
    {
        // A TokenSet whose only member is the CRLF grapheme cluster. \r
        // and \n are NOT members on their own — only the two-rune "\r\n"
        // cluster is.
        var crlfCluster = TokenSet.Graphemes("\r\n");

        // WithinToken runs OneOrMore(NoneOf(...)) against the runes
        // inside one outer token. On the "\r\n" cluster the inner rule
        // sees \r and \n one rune at a time; neither is a member of
        // crlfCluster, so the inner NoneOf accepts both and WithinToken
        // consumes the whole cluster. Standalone, it matches:
        var standalone = WithinToken(OneOrMore(NoneOf(crlfCluster)));
        Assert.That(standalone.Parse("\r\n").Success, Is.True,
            "WithinToken(OneOrMore(NoneOf(crlfCluster))) should consume the CRLF cluster rune by rune");

        // The same WithinToken as an Or branch must still match. The
        // Or's lookahead shortcut peeks the whole "\r\n" cluster and asks
        // the WithinToken's published first-token requirement whether
        // the branch can match. WithinToken forwards the inner NoneOf's
        // MustNotBeIn fail-set { "\r\n" } unchanged, so the shortcut sees
        // the peek cluster IS in the fail-set and skips the branch — even
        // though the branch would have matched.
        var rule = Or(WithinToken(OneOrMore(NoneOf(crlfCluster))), Literal("ZZ"));
        var result = rule.Parse("\r\n");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    // Matrix-driven SourceRange test. See docs/TestArchitecture.md
    // "Per-rule SourceRange-matrix tests live in each rule's own
    // test file." Shared scaffold lives in SourceRangeMatrixHelper.
    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void SourceRange_for_WithinToken_target_after_normalized_Literal_prefix_uses_original_coords(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        SourceRangeMatrixHelper.AssertTargetAfterLiteralPrefix(
            row, form,
            target: WithinToken(OneOf("X")).As("checked"),
            targetText: "X");
    }

    [Test]
    public void SourceText_on_WithinToken_returns_matched_text_under_every_FlattenType()
    {
        SourceTextFlattenTypeMatrixHelper.AssertSourceTextUnderEveryFlattenType(
            ruleBuilder: () => WithinToken(OneOf("X")),
            input: "X",
            expectedSourceText: "X");
    }

    [Test]
    public void WithinToken_WithError_is_shadowed_by_a_deeper_orphan_from_an_abandoned_Or_alternative()
    {
        // Or's first alternative reads three tokens before failing at offset 3
        // with its own WithError ("expected z at end"). The parser abandons
        // that alternative and commits to alternative 2 (Token('a').Delete()
        // at offset 0). WithinToken then runs at offset 1, fails because the
        // next token isn't 'b', and records its own WithError at the outer
        // cluster boundary, offset 1.
        //
        // Depth ranks first (docs/ErrorArchitecture.md, Case 4): a rejected
        // Or branch keeps its failure, and offset 3 is deeper than offset 1,
        // so the abandoned branch's "expected z at end" is the reported
        // error. WithinToken's shallower WithError is correctly shadowed by
        // the deeper near-miss. A grammar author who wants the WithinToken
        // message to win regardless of depth marks it forced.
        var rule = And(
            Or(
                And(AnyToken(), AnyToken(), AnyToken(), Token('z').WithError("expected z at end")),
                Token('a').Delete()
            ),
            WithinToken(Token('b')).WithError("expected b in WithinToken")
        );
        var result = rule.Parse("axyw");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected z at end"));
    }

    // --- Forced .WithError carried across the WithinToken boundary -------
    //
    // A skin-tone-modified emoji is one grapheme cluster: a base emoji
    // rune followed by a modifier rune. A grammar that validates such a
    // "reaction" has to look inside the cluster with WithinToken. These
    // code points drive the reaction-parsing tests below.
    private const int ThumbsUp = 0x1F44D;
    private const int ThumbsDown = 0x1F44E;
    private static readonly TokenSet SkinToneModifiers = TokenSet.Range(0x1F3FB, 0x1F3FF);

    [Test]
    public void Forced_inner_WithError_surfaces_when_WithinToken_fails()
    {
        // A reaction is a thumbs-up emoji, optionally skin-toned. The
        // inner check carries a forced .WithError, so a wrong emoji is
        // reported with that message rather than a rune-level default.
        var reaction = WithinToken(
            And(Token(ThumbsUp), Optional(OneOf(SkinToneModifiers)))
                .WithError("a reaction must be a thumbs-up emoji", forced: true));

        var result = reaction.Parse(char.ConvertFromUtf32(ThumbsDown));

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("a reaction must be a thumbs-up emoji"));
    }

    [Test]
    public void Forced_WithError_inside_WithinToken_keeps_its_forced_flag()
    {
        // A message is an emoji reaction (a '+' then the emoji) or a
        // slash-command. Each form carries a forced .WithError summary.
        // On a '+' followed by the wrong emoji, the reaction branch
        // consumes the '+' and fails at offset 1, the command branch at
        // offset 0. Forced failures rank by depth, so the deeper one (the
        // reaction's, at offset 1) wins, as long as WithinToken keeps the
        // inner .WithError forced when it surfaces it.
        var reaction = And(
            Token('+'),
            WithinToken(
                And(Token(ThumbsUp), Optional(OneOf(SkinToneModifiers)))
                    .WithError("a reaction must be a thumbs-up emoji", forced: true)));
        var command = And(Token('/'), OneOrMore(OneOf(TokenSet.Ascii.Letters)))
            .WithError("a command must start with '/'", forced: true);

        var result = Or(reaction, command).Parse("+" + char.ConvertFromUtf32(ThumbsDown));

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("a reaction must be a thumbs-up emoji"));
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
    }

    [Test]
    public void WithinToken_own_forced_WithError_outranks_an_inner_named_hint()
    {
        // The inner emoji check carries a plain (named) .WithError hint.
        // The WithinToken carries a forced summary. A forced failure
        // outranks a named one, so the summary is what surfaces.
        // WithinToken has to record its own .WithError for ranking to
        // pick it over the inner hint.
        var reaction = WithinToken(
            And(Token(ThumbsUp).WithError("expected a thumbs-up"),
                Optional(OneOf(SkinToneModifiers))))
            .WithError("not a recognized reaction", forced: true);

        var result = reaction.Parse(char.ConvertFromUtf32(ThumbsDown));

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("not a recognized reaction"));
    }

    [Test]
    public void Scanner_skip_in_one_rune_sublexer_does_not_skip_mid_cluster_runes()
    {
        // The lexer normally reads one grapheme per token
        // (a character can be several code points: an accented letter, an
        // emoji). WithinToken switches to a mode that reads one code point
        // per token, so an inner rule can look inside a character.
        //
        // The scanner-skip speed trick (Lexer.AdvanceUntilRuneIn and
        // friends) jumps ahead with a fast text search, then asks
        // IsAtMidToken "did I land on a real token start?" before stopping.
        // IsAtMidToken answers by the active token unit: in the one-code-
        // point mode a combining mark is its own token, and
        // only the trailing half of a surrogate pair counts as token
        // interior. So the fast search has to stop on the combining mark,
        // the same place the slow per-token scan stops. This test holds the
        // two scans to that agreement.
        //
        // Fixture: "e" + combining accent (U+0065 then U+0301) is one
        // character made of two code points. Reading one code point at a
        // time, a scan for the accent has to stop on it (offset 1), not run
        // off the end (offset 2).
        string subInput = "e\u0301"; // e + combining acute, decomposed (ASCII-safe in source)
        var set = TokenSet.Single(0x0301);
        Assert.That(set.TryGetBmpChars(1, out var bmpCandidates), Is.True,
            "U+0301 is a single BMP char, so the vectorized fast path applies.");

        var fastPath = new Lexer(subInput, startPosition: 0, endPosition: subInput.Length,
            traceSink: null, traceLevel: TraceLevel.Normal, oneRunePerToken: true);
        fastPath.AdvanceUntilRuneIn(set, bmpCandidates);

        var slowPath = new Lexer(subInput, startPosition: 0, endPosition: subInput.Length,
            traceSink: null, traceLevel: TraceLevel.Normal, oneRunePerToken: true);
        slowPath.AdvanceUntilRuneIn(set, bmpCandidates: null);

        Assert.That(fastPath.Position, Is.EqualTo(1),
            "Vectorized fast path must stop on the combining mark, the one-rune token at offset 1.");
        Assert.That(fastPath.Position, Is.EqualTo(slowPath.Position),
            "The IndexOfAny fast path and the per-token slow path must agree in one-rune mode.");
    }
}
