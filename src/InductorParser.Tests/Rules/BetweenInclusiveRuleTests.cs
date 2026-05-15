using System;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;
using static InductorParser.Tests.CanaryHelper;

namespace InductorParser.Tests;

// BetweenInclusiveRule is the shared body behind OneOrMore, ZeroOrMore,
// Optional, AtLeast, AtMost, and Exactly. All of those factories construct
// a BetweenInclusiveRule with different (atLeast, atMost) bounds and a
// different trace name, then return it unchanged. So the functional tests
// for every count rule live here, and each named-factory fixture just
// verifies that its factory wires the right bounds and trace name.
[TestFixture]
public class BetweenInclusiveRuleTests
{
    // Tests that assert on Tree.ToString() use PreserveAllSymbols so
    // Token rules (default FlattenType.Delete) stay in the tree and their
    // text is visible in the concatenated output. Without the flag the
    // tree would contain only non-Delete nodes, which is the correct
    // parse-time semantic, just not what these tests are looking at.
    private static ParseOptions Debug() => new() { PreserveAllSymbols = true };


    [Test]
    public void BetweenInclusive_exact_count_matches_exactly_N()
    {
        var rule = BetweenInclusive(3, 3, Token('a'));
        var result = rule.Parse("aaa", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aaa"));
    }

    [Test]
    public void BetweenInclusive_exact_count_fails_when_too_few()
    {
        var rule = BetweenInclusive(3, 3, Token('a'));
        var result = rule.Parse("aa");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
    }

    [Test]
    public void BetweenInclusive_exact_count_fails_when_too_many()
    {
        var rule = BetweenInclusive(3, 3, Token('a'));
        var result = rule.Parse("aaaa");

        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void BetweenInclusive_at_lower_bound_succeeds()
    {
        var rule = BetweenInclusive(2, 5, Token('a'));
        var result = And(rule, OneOrMore(Token('b'))).Parse("aabbb", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aabbb"));
    }

    [Test]
    public void BetweenInclusive_at_upper_bound_succeeds()
    {
        var rule = BetweenInclusive(2, 5, Token('a'));
        var result = And(rule, Token('b')).Parse("aaaaab", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aaaaab"));
    }

    [Test]
    public void BetweenInclusive_stops_at_upper_bound_even_with_more_input()
    {
        var rule = And(BetweenInclusive(1, 3, Token('a')), OneOrMore(Token('a')));
        var result = rule.Parse("aaaaa", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aaaaa"));
    }

    [Test]
    public void BetweenInclusive_one_below_lower_bound_fails()
    {
        var rule = BetweenInclusive(3, 5, Token('a'));
        var result = rule.Parse("aa");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
    }

    [Test]
    public void BetweenInclusive_zero_zero_succeeds_with_no_matches()
    {
        var rule = And(BetweenInclusive(0, 0, Token('a')), Token('b'));
        var result = rule.Parse("b", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("b"));
    }

    [Test]
    public void BetweenInclusive_zero_zero_does_not_consume_matching_input()
    {
        var rule = And(BetweenInclusive(0, 0, Token('a')), OneOrMore(Token('a')));
        var result = rule.Parse("aaa", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aaa"));
    }

    [Test]
    public void BetweenInclusive_zero_match_with_zero_lower_bound_produces_empty_symbols()
    {
        // A BetweenInclusive whose lower bound is 0 and that matched zero
        // times has FlattenType.Flatten, so no wrapper Symbol is ever
        // produced: the empty match leaves the root Symbols list empty.
        // No per-rune leaves, no BetweenInclusive wrapper, no children-list
        // allocation survives into the tree.
        var result = BetweenInclusive(0, int.MaxValue, Token('x')).Parse("");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Symbols, Is.Empty);
    }

    [Test]
    public void BetweenInclusive_failure_without_WithError_falls_back_to_positional_message()
    {
        var rule = BetweenInclusive(2, 4, Token('a'));
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
        Assert.That(result.ErrorMessage, Does.StartWith("Unexpected end of input"));
    }

    [Test]
    public void BetweenInclusive_WithError_message_surfaces_on_failure()
    {
        var rule = BetweenInclusive(2, 4, Token('a'))
            .WithError("need 2 to 4 a's");
        var result = rule.Parse("ab");

        Assert.That(result.Success, Is.False);
        // BetweenInclusive's WithError reports at the failing iteration's
        // start position (1, where Token('a') tried 'b' and failed). The
        // first 'a' matched and advanced the lexer; the failure point is
        // the position the user needs to fix, not the rule's overall start.
        // See docs/ErrorArchitecture.md.
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
        Assert.That(result.ErrorMessage, Is.EqualTo("need 2 to 4 a's"));
    }

    [Test]
    public void BetweenInclusive_inner_WithError_wins_at_equal_depth()
    {
        // Lower bound 1 forces the rule to fail at zero matches. The inner
        // Token('a') tries at offset 0, reads 'b', fails, and records
        // its WithError message. BetweenInclusive then records at the same
        // offset with its own WithError, but the slot is already filled by
        // the inner's more-specific message, so the inner wins
        // (first-writer at equal depth).
        var rule = BetweenInclusive(1, int.MaxValue, Token('a').WithError("want 'a'"))
                       .WithError("want at least one 'a'");

        var result = rule.Parse("bbb");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("want 'a'"));
    }

    [Test]
    public void BetweenInclusive_outer_WithError_wins_when_inner_has_none()
    {
        // Without a WithError on the inner, Token('a') records at offset
        // 0 with a null message. BetweenInclusive then records at offset 0
        // with its own WithError, which claims the empty slot via the
        // equal-depth rule.
        var rule = BetweenInclusive(1, int.MaxValue, Token('a'))
                       .WithError("want at least one 'a'");

        var result = rule.Parse("bbb");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("want at least one 'a'"));
    }

    [Test]
    public void BetweenInclusive_WithError_does_not_surface_when_rule_always_succeeds()
    {
        // Lower bound 0 means the rule always succeeds, so a WithError on
        // it never reaches the deepest-failure slot. Document the behavior
        // by verifying it. A failing parse here fails on the outer And,
        // not on the BetweenInclusive.
        var rule = And(
            BetweenInclusive(0, 3, Token('a')).WithError("unreachable"),
            Token('z'));
        var result = rule.Parse("aaab");

        Assert.That(result.Success, Is.False);
        // BetweenInclusive consumed three 'a's. The outer And failed on
        // Token('z') against 'b' at offset 3.
        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(result.ErrorMessage, Does.Not.Contain("unreachable"));
    }

    [Test]
    public void BetweenInclusive_descendant_WithError_surfaces_when_lookahead_shortcut_would_fire()
    {
        // OneOrMore(Or(Token('a').WithError("want 'a'"), Token('b'))) against "c".
        //
        // The Or itself has no WithError, but one of its children does. The
        // BetweenInclusive shortcut sees Or.ErrorMessage == null and (since
        // both Or children are Always-advance with FirstConsumedTokens
        // {'a','b'}) Or.CannotMatchLookahead('c') == true. The pre-fix gate
        // fired the failure-path shortcut without entering the Or, so
        // Token('a').WithError never got a chance to record "want 'a'" at
        // offset 0. The user saw the generic positional message.
        //
        // The fix gates the shortcut on Inner.HasErrorMessageInSubtree,
        // which is true here because Token('a').WithError is reachable
        // from the Or. The shortcut is bypassed. The loop runs the Or
        // once; the Or's own per-child shortcut skips Token('b') (no
        // WithError) but tries Token('a').WithError because its WithError
        // gates the per-child skip. "want 'a'" lands at the deepest-
        // failure slot for offset 0 and surfaces as ErrorMessage.
        var rule = OneOrMore(Or(Token('a').WithError("want 'a'"), Token('b')));
        var result = rule.Parse("c");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("want 'a'"));
    }

    [Test]
    public void BetweenInclusive_descendant_WithError_surfaces_when_zero_or_more_shortcut_would_fire()
    {
        // Sibling of the OneOrMore case but for ZeroOrMore (AtLeast == 0).
        // ZeroOrMore catches inner failures and succeeds with count=0,
        // but its commit does NOT clear inner failure records (count
        // rules preserve records about real input failures; see
        // docs/ErrorArchitecture.md). So the descendant Token('a')'s
        // .WithError("want 'a'") survives and is reported when the
        // outer And's Token('z') subsequently fails at the same offset.
        var rule = And(
            ZeroOrMore(Or(Token('a').WithError("want 'a'"), Token('b'))),
            Token('z'));
        var result = rule.Parse("c");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("want 'a'"));
    }

    [Test]
    public void BetweenInclusive_descendant_WithError_surfaces_under_scanner_skip_pattern()
    {
        // ZeroOrMore(Or(realMatch, AnyToken.Delete)) is the scanner-skip
        // shape. The descendant Token('a').WithError("want 'a'") inside
        // the realMatch alternative records its failure at the EOF
        // landing, and ZeroOrMore's commit preserves the record (count
        // rules don't clear). The outer And's Token('z') failure at the
        // same depth lets "want 'a'" win the tier resolution.
        var realMatch = And(Token('a').WithError("want 'a'"), Token('b'));
        var rule = And(
            ZeroOrMore(Or(realMatch, AnyToken().Flatten(FlattenType.Delete))),
            Token('z'));
        var result = rule.Parse("y");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("want 'a'"));
    }

    [Test]
    public void BetweenInclusive_inner_failure_still_contributes_to_deepest_failure()
    {
        // BetweenInclusive(0, 1, inner) is a count rule: its commit on
        // success doesn't clear inner failure records, so a deeper
        // inner WithError survives the success and wins the tier
        // resolution even when the outer rule succeeds with zero
        // matches.
        //
        // Grammar: And(BetweenInclusive(0, 1, And(a, b, c-with-message)),
        //                x-with-message)
        // Input:   "abdy"
        //
        // The (0, 1) inner reads "ab" then 'c' fails at offset 2,
        // recording "need 'c'". The outer rule catches and succeeds
        // with empty (lower bound 0). Token('x') then fails at offset 0
        // with its own "need 'x'". Deepest named wins: offset 2 with
        // "need 'c'", pointing inside what was supposedly optional.
        var rule = And(
            BetweenInclusive(0, 1,
                And(Token('a'),
                      Token('b'),
                      Token('c').WithError("need 'c'"))),
            Token('x').WithError("need 'x'"));

        var result = rule.Parse("abdy");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
        Assert.That(result.ErrorMessage, Is.EqualTo("need 'c'"));
    }

    [Test]
    public void BetweenInclusive_factory_rejects_negative_atLeast()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BetweenInclusive(-1, 5, Token('a')));
    }

    [Test]
    public void BetweenInclusive_factory_rejects_atMost_less_than_atLeast()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BetweenInclusive(3, 2, Token('a')));
    }

    [Test]
    public void BetweenInclusive_factory_rejects_null_inner()
    {
        Assert.Throws<ArgumentNullException>(
            () => BetweenInclusive(0, 1, null!));
    }

    [Test]
    [RecursiveEngineOnly]
    public void BetweenInclusive_trace_success_produces_expected_output()
    {
        var sink = NewSink();
        BetweenInclusive(2, 4, OneOf(TokenSet.Ascii.Letters))
            .Parse("abc", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'a', Consumed: 1",
            "      SUCC | OneOf: found 'a', wanted one of '[A-Z,a-z]'",
            "      Lexer.Read: 'b', Consumed: 2",
            "      SUCC | OneOf: found 'b', wanted one of '[A-Z,a-z]'",
            "      Lexer.Read: 'c', Consumed: 3",
            "      SUCC | OneOf: found 'c', wanted one of '[A-Z,a-z]'",
            "      Lexer.Read: '<EOF>', Consumed: 3",
            "      FAIL | OneOf: found '<EOF>', wanted one of '[A-Z,a-z]'",
            "      Lexer.RecordFailure: new deepest failure at char 3",
            "   SUCC | BetweenInclusive[2..4]: count= 3"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    [RecursiveEngineOnly]
    public void BetweenInclusive_trace_failure_produces_expected_output()
    {
        var sink = NewSink();
        BetweenInclusive(2, 4, Token('a'))
            .Parse("a", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'a', Consumed: 1",
            "      SUCC | Token: found 'a'",
            "      Lexer.Read: '<EOF>', Consumed: 1",
            "      FAIL | Token: found '<EOF>', wanted 'a'",
            "      Lexer.RecordFailure: new deepest failure at char 1",
            "   FAIL | BetweenInclusive[2..4]: count= 1"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    // ----- Scanner-skip optimization (atLeast=0, atMost=int.MaxValue) -----
    //
    // BetweenInclusiveRule.TryCreateScannerSkip recognizes the shape
    // ZeroOrMore(Or(match, AnyToken.Delete)) and jumps directly to
    // the next rune that could start a real match. The optimization only
    // triggers when atLeast=0 and atMost=int.MaxValue, which is the
    // ZeroOrMore-equivalent shape, so these tests construct that shape
    // explicitly via BetweenInclusive(0, int.MaxValue, ...).

    [Test]
    public void BetweenInclusive_scanner_shape_skips_deleted_fallback_runs()
    {
        var match = Literal("Sherlock").As("match").Flatten(SyntaxTree.FlattenType.Preserve);
        var scanner = BetweenInclusive(0, int.MaxValue, Or(
            match,
            AnyToken().Flatten(SyntaxTree.FlattenType.Delete)
        )).As("scan").Flatten(SyntaxTree.FlattenType.Preserve);

        // The slow path would invoke the inner Or once per rune (5000+
        // times). RuleCountLimit=100 caps invocations, so a successful parse
        // can only mean the scanner skip jumped over the 'x' run.
        string input = new string('x', 5000) + "Sherlock";
        scanner.Compile(null);
        var result = scanner.Parse(input, new ParseOptions
        {
            RuleCountLimit = 100,
            MaxDepth = 0
        });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("Sherlock"));
        Assert.That(result.Tree.Find(match)!.ToString(), Is.EqualTo("Sherlock"));
    }

    [Test]
    public void BetweenInclusive_scanner_shape_prefilters_ascii_case_insensitive_literal()
    {
        var match = LiteralIgnoreAsciiCase("Sherlock Holmes")
            .As("match")
            .Flatten(SyntaxTree.FlattenType.Preserve);
        var scanner = BetweenInclusive(0, int.MaxValue, Or(
            match,
            AnyToken().Flatten(SyntaxTree.FlattenType.Delete)
        )).As("scan").Flatten(SyntaxTree.FlattenType.Preserve);

        string input = new string('s', 5000) + "sHeRlOcK hOlMeS";
        scanner.Compile(null);
        var result = scanner.Parse(input, new ParseOptions
        {
            RuleCountLimit = 100,
            MaxDepth = 0
        });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("sHeRlOcK hOlMeS"));
        Assert.That(result.Tree.Find(match)!.ToString(), Is.EqualTo("sHeRlOcK hOlMeS"));
    }

    [Test]
    public void BetweenInclusive_scanner_shape_prefilters_nested_literal_alternates()
    {
        var match = Or(
            LiteralIgnoreAsciiCase("Sherlock Holmes").Flatten(SyntaxTree.FlattenType.Preserve),
            LiteralIgnoreAsciiCase("John Watson").Flatten(SyntaxTree.FlattenType.Preserve),
            LiteralIgnoreAsciiCase("Irene Adler").Flatten(SyntaxTree.FlattenType.Preserve)
        ).As("match").Flatten(SyntaxTree.FlattenType.Preserve);
        var scanner = BetweenInclusive(0, int.MaxValue, Or(
            match,
            AnyToken().Flatten(SyntaxTree.FlattenType.Delete)
        )).As("scan").Flatten(SyntaxTree.FlattenType.Preserve);

        string input = new string('j', 5000) + "jOhN wAtSoN";
        scanner.Compile(null);
        var result = scanner.Parse(input, new ParseOptions
        {
            RuleCountLimit = 100,
            MaxDepth = 0
        });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("jOhN wAtSoN"));
        Assert.That(result.Tree.Find(match)!.ToString(), Is.EqualTo("jOhN wAtSoN"));
    }

    [Test]
    public void BetweenInclusive_scanner_shape_preserves_debug_tree_when_requested()
    {
        var match = Literal("S").As("match").Flatten(SyntaxTree.FlattenType.Preserve);
        var scanner = BetweenInclusive(0, int.MaxValue, Or(
            match,
            AnyToken().Flatten(SyntaxTree.FlattenType.Delete)
        )).As("scan").Flatten(SyntaxTree.FlattenType.Preserve);

        var result = scanner.Parse("xxS", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("xxS"));
    }

    [Test]
    public void BetweenInclusive_scanner_shape_does_not_skip_preserved_fallback()
    {
        var match = Literal("S").As("match").Flatten(SyntaxTree.FlattenType.Preserve);
        var scanner = BetweenInclusive(0, int.MaxValue, Or(
            match,
            AnyToken()
        )).As("scan").Flatten(SyntaxTree.FlattenType.Preserve);

        var result = scanner.Parse("xxS");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("xxS"));
    }

    [Test]
    public void BetweenInclusive_scanner_skip_does_not_skip_NoneOf_alternative_matches()
    {
        // Regression Test: TryCreateScannerSkip used to union 
        // every non-fallback alternative's
        // FirstConsumedTokens.LookaheadFirstRunes into the candidate set
        // without considering Polarity. For a MustNotBeIn alternative
        // like NoneOf(stopSet), FirstConsumedTokens is the rule's
        // FAIL-set, not its match-set. Including it directs the
        // scanner to advance to positions where NoneOf will FAIL (and
        // fall through to AnyToken().Delete()), silently skipping past
        // every position where NoneOf would have MATCHED.
        var stopSet = TokenSet.Runes("xy");
        var scanner = BetweenInclusive(0, int.MaxValue, Or(
            NoneOf(stopSet),
            AnyToken().Flatten(SyntaxTree.FlattenType.Delete)
        ));

        var result = scanner.Parse("abxcyd");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        // NoneOf(stopSet) matches 'a','b','c','d' as Preserve leaves;
        // 'x' and 'y' fall to AnyToken().Delete() and contribute
        // nothing. The concatenated matched text must be "abcd".
        Assert.That(result.ToString(), Is.EqualTo("abcd"));
    }

    // ----- Scanner-skip optimization: CRLF mid-cluster regression tests -----
    //
    // CRLF is one grapheme cluster (UAX #29 GB3). The scanner-skip fast paths
    // in Lexer.AdvanceUntilRuneIn and Lexer.AdvanceUntilLiteralCandidateIn use
    // string.IndexOfAny / string.IndexOf, which operate on UTF-16 code units
    // and don't know about cluster boundaries. Without a guard, they land on
    // the LF inside CRLF, the inner Or reads a fresh one-rune "\n" token
    // at the mid-cluster offset, and a rule that should reject the multi-rune
    // CRLF cluster (OneOf("\n"), Literal("\nfoo"), etc.) mistakenly matches
    // it. The slow path walks one grapheme at a time and is the source of
    // truth for what these grammars should produce. See backlog 7crl.

    [Test]
    public void BetweenInclusive_scanner_shape_AdvanceUntilRuneIn_does_not_split_crlf_at_lf()
    {
        var match = OneOf("\n").Flatten(SyntaxTree.FlattenType.Preserve);
        var scanner = BetweenInclusive(0, int.MaxValue, 
                        Or(match,
                                AnyToken().Flatten(SyntaxTree.FlattenType.Delete)
        )).As("scan").Flatten(SyntaxTree.FlattenType.Preserve);

        scanner.Compile(null);
        var result = scanner.Parse("\r\n");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.Children.Count, Is.EqualTo(0),
            "AnyToken.Delete should swallow the whole CRLF cluster; no OneOf(\"\\n\") match should appear because the LF inside CRLF isn't a token boundary.");
    }

    [Test]
    public void BetweenInclusive_unoptimized_path_does_not_match_lf_inside_crlf()
    {
        // Control: AtLeast=1 disables the scanner-skip optimization, so the
        // loop walks one grapheme at a time. This pins the slow-path
        // behavior (CRLF cluster swallowed whole) as the source of truth.
        var match = OneOf("\n").Flatten(SyntaxTree.FlattenType.Preserve);
        var scanner = BetweenInclusive(1, int.MaxValue, 
                        Or(match,
                                AnyToken().Flatten(SyntaxTree.FlattenType.Delete)
        )).As("scan").Flatten(SyntaxTree.FlattenType.Preserve);

        scanner.Compile(null);
        var result = scanner.Parse("\r\n");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.Children.Count, Is.EqualTo(0));
    }

    [Test]
    public void BetweenInclusive_scanner_shape_single_literal_cache_does_not_split_crlf_at_lf()
    {
        // Exercises the cached single-literal path in
        // AdvanceUntilLiteralCandidateIn (literalPositions != null,
        // literals.Length == 1). IndexOf("\nfoo", ...) lands on the LF
        // inside CRLF. Without the guard, AnyLiteralMatchesAt confirms
        // the literal at the mid-cluster offset and the inner Literal
        // rule then reads "\nfoo" from that offset.
        var match = Literal("\nfoo").Flatten(SyntaxTree.FlattenType.Preserve);
        var scanner = BetweenInclusive(0, int.MaxValue, 
                        Or(match,
                                AnyToken().Flatten(SyntaxTree.FlattenType.Delete)
        )).As("scan").Flatten(SyntaxTree.FlattenType.Preserve);

        scanner.Compile(null);
        var result = scanner.Parse("\r\nfoo");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(""),
            "Literal(\"\\nfoo\") should not match starting at the LF inside CRLF; the slow path consumes \\r\\n then f, o, o each as deleted AnyTokens and produces an empty preserved tree.");
    }

    [Test]
    public void BetweenInclusive_scanner_shape_multi_literal_IndexOfAny_does_not_split_crlf_at_lf()
    {
        // Exercises the BMP-firstrunes IndexOfAny path in
        // AdvanceUntilLiteralCandidateIn (literalPositions == null because
        // there are two literals; bmpFirstRunes carries '\n' for both).
        // Same mid-CRLF landing problem as the single-literal cache.
        var matchFoo = Literal("\nfoo").Flatten(SyntaxTree.FlattenType.Preserve);
        var matchBar = Literal("\nbar").Flatten(SyntaxTree.FlattenType.Preserve);
        var scanner = BetweenInclusive(0, int.MaxValue, Or(
            matchFoo,
            matchBar,
            AnyToken().Flatten(SyntaxTree.FlattenType.Delete)
        )).As("scan").Flatten(SyntaxTree.FlattenType.Preserve);

        scanner.Compile(null);
        var result = scanner.Parse("\r\nfoo");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(""));
    }

    [Test]
    public void BetweenInclusive_scanner_shape_AdvanceUntilRuneIn_still_finds_lf_after_crlf()
    {
        // After the fix skips past a mid-CRLF LF, a real standalone LF
        // later in the input still has to be found. This test exercises
        // the "skip the CRLF, then keep searching" path.
        var match = OneOf("\n").Flatten(SyntaxTree.FlattenType.Preserve);
        var scanner = BetweenInclusive(0, int.MaxValue, Or(
            match,
            AnyToken().Flatten(SyntaxTree.FlattenType.Delete)
        )).As("scan").Flatten(SyntaxTree.FlattenType.Preserve);

        scanner.Compile(null);
        var result = scanner.Parse("\r\nx\nz");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.Children.Count, Is.EqualTo(1),
            "The CRLF should be swallowed by AnyToken.Delete and the standalone LF after 'x' should match OneOf(\"\\n\").");
        Assert.That(result.Tree!.Children[0].ToString(), Is.EqualTo("\n"));
    }

    // The same byte-level-search bug shape applies to any BMP char that
    // can sit as the second-or-later rune of a multi-rune cluster.
    // CRLF is the practical case; the others below test the broader
    // contract so a future regression in the IsAtMidGraphemeCluster
    // gate gets caught for the categories that actually appear in real
    // grammars.
    //
    // Most of these inputs contain invisible / hard-to-distinguish
    // codepoints (combining marks, ZWJ, variation selectors, Indic
    // viramas), so they're named below as constants. Reading the
    // tests becomes a matter of reading the constant names rather
    // than peering at lookalike whitespace.


    [Test]
    public void BetweenInclusive_scanner_shape_does_not_split_combining_mark_cluster()
    {
        // U+0301 (combining acute) attaches to the previous base under
        // UAX #29 GB9, so "e" + acute is one cluster. A grammar that
        // looks for OneOf(UnicodeExamples.CombiningAcuteText) expects the standalone
        // combining mark, so the cluster must be rejected as multi-rune.
        const string baseChar = "e";
        string clusterInput = baseChar + UnicodeExamples.CombiningAcuteText;

        var match = OneOf(UnicodeExamples.CombiningAcuteText).Flatten(SyntaxTree.FlattenType.Preserve);
        var scanner = BetweenInclusive(0, int.MaxValue, Or(
            match,
            AnyToken().Flatten(SyntaxTree.FlattenType.Delete)
        )).As("scan").Flatten(SyntaxTree.FlattenType.Preserve);

        scanner.Compile(null);
        var result = scanner.Parse(clusterInput);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.Children.Count, Is.EqualTo(0),
            "AnyToken.Delete should swallow the e+combining-acute cluster whole; OneOf(combining-acute) must not match the combining mark inside the cluster.");
    }

    [Test]
    public void BetweenInclusive_scanner_shape_does_not_split_zwj_emoji_sequence()
    {
        // ZWJ glues Extended_Pictographic chars into one cluster under
        // UAX #29 GB11. man + ZWJ + woman is one cluster.
        // OneOf(UnicodeExamples.ZeroWidthJoinerText) should reject the cluster because the
        // cluster is multi-rune, not a standalone ZWJ.
        string zwjSequenceInput = UnicodeExamples.ManEmojiGrapheme + UnicodeExamples.ZeroWidthJoinerText + UnicodeExamples.WomanEmojiGrapheme;

        var match = OneOf(UnicodeExamples.ZeroWidthJoinerText).Flatten(SyntaxTree.FlattenType.Preserve);
        var scanner = BetweenInclusive(0, int.MaxValue, Or(
            match,
            AnyToken().Flatten(SyntaxTree.FlattenType.Delete)
        )).As("scan").Flatten(SyntaxTree.FlattenType.Preserve);

        scanner.Compile(null);
        var result = scanner.Parse(zwjSequenceInput);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.Children.Count, Is.EqualTo(0),
            "ZWJ inside an emoji ZWJ sequence isn't a token boundary; OneOf(ZWJ) must not match it.");
    }

    [Test]
    public void BetweenInclusive_scanner_shape_does_not_split_variation_selector_cluster()
    {
        // Variation Selector 16 attaches to the previous base under
        // UAX #29 GB9 (it's in the Extend set). "#" + VS-16 is one
        // cluster (the keycap base). OneOf(UnicodeExamples.EmojiVariationSelectorText) should
        // reject the cluster.
        const string baseChar = "#";
        string clusterInput = baseChar + UnicodeExamples.EmojiVariationSelectorText;

        var match = OneOf(UnicodeExamples.EmojiVariationSelectorText).Flatten(SyntaxTree.FlattenType.Preserve);
        var scanner = BetweenInclusive(0, int.MaxValue, Or(
            match,
            AnyToken().Flatten(SyntaxTree.FlattenType.Delete)
        )).As("scan").Flatten(SyntaxTree.FlattenType.Preserve);

        scanner.Compile(null);
        var result = scanner.Parse(clusterInput);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.Children.Count, Is.EqualTo(0),
            "Variation selector inside a base+VS cluster isn't a token boundary; OneOf(VS-16) must not match it.");
    }

    [Test]
    public void BetweenInclusive_scanner_shape_indic_conjunct_agrees_with_unoptimized_path()
    {
        // Devanagari ka + virama + ssa. UAX #29 rev. 39 (GB9c) keeps
        // these glued as one Indic conjunct cluster; earlier revisions
        // break before the trailing consonant. .NET 8's StringInfo
        // currently uses the older rules, so the slow path treats this
        // as two clusters and OneOf(ssa) matches at the trailing
        // consonant. What this test pins is that the scanner-skip fast
        // path agrees with the slow path on whichever runtime is
        // hosting the suite: AtLeast=0 and AtLeast=1 produce the same
        // number of matches. If a future runtime upgrade implements
        // GB9c, both numbers will change in lockstep and the assertion
        // still holds.
        string conjunctInput = UnicodeExamples.DevanagariKaGrapheme + UnicodeExamples.DevanagariViramaText + UnicodeExamples.DevanagariSsaGrapheme;

        var fastMatch = OneOf(UnicodeExamples.DevanagariSsaGrapheme).Flatten(SyntaxTree.FlattenType.Preserve);
        var fastScanner = BetweenInclusive(0, int.MaxValue, Or(
            fastMatch,
            AnyToken().Flatten(SyntaxTree.FlattenType.Delete)
        )).As("scan-fast").Flatten(SyntaxTree.FlattenType.Preserve);
        fastScanner.Compile(null);
        var fastResult = fastScanner.Parse(conjunctInput);

        var slowMatch = OneOf(UnicodeExamples.DevanagariSsaGrapheme).Flatten(SyntaxTree.FlattenType.Preserve);
        var slowScanner = BetweenInclusive(1, int.MaxValue, Or(
            slowMatch,
            AnyToken().Flatten(SyntaxTree.FlattenType.Delete)
        )).As("scan-slow").Flatten(SyntaxTree.FlattenType.Preserve);
        slowScanner.Compile(null);
        var slowResult = slowScanner.Parse(conjunctInput);

        Assert.That(fastResult.Success, Is.True, fastResult.ErrorMessage);
        Assert.That(slowResult.Success, Is.True, slowResult.ErrorMessage);
        Assert.That(fastResult.Tree!.Children.Count,
            Is.EqualTo(slowResult.Tree!.Children.Count),
            "Scanner-skip fast path must produce the same tree shape as the un-optimized AtLeast=1 walk.");
    }

    [Test]
    public void Sealed_BetweenInclusive_rejects_Flatten()
    {
        var rule = BetweenInclusive(1, 5, Token('a'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.Flatten(FlattenType.Preserve));
    }

    [Test]
    public void Sealed_BetweenInclusive_rejects_WithError()
    {
        var rule = BetweenInclusive(1, 5, Token('a'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.WithError("late"));
    }

    [Test]
    public void Sealed_BetweenInclusive_rejects_As()
    {
        var rule = BetweenInclusive(1, 5, Token('a'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.As("late"));
    }

    // The inner-loop shortcut (peek before each iteration, take count==0
    // fast-exit when inner can't match) is covered by ZeroOrMoreRuleTests.
    // The tests below cover the OTHER direction: an enclosing Or
    // skipping a BetweenInclusive whose AtLeast >= 1 (so its Advance is
    // Always and the outer shortcut can fire).

    [Test]
    [RecursiveEngineOnly]
    public void Or_OneOrMore_skips_when_peek_is_not_in_inner_set()
    {
        // OneOrMore(inner) forwards inner's first-set with Advance.Always
        // (since AtLeast == 1). Peek 'x' isn't in {'a'}, so the outer
        // Or skips OneOrMore.
        var sink = NewSink();
        var rule = Or(OneOrMore(Token('a')), Literal("x"));
        var result = rule.Parse("x", new ParseOptions { TraceSink = sink });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(sink.ToString(), Does.Contain("SKIP | OneOrMore:"));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Or_OneOrMore_runs_when_peek_is_in_inner_set()
    {
        // Peek 'a' is in {'a'}, so the outer Or doesn't skip
        // OneOrMore. It runs and matches.
        var sink = NewSink();
        var rule = Or(OneOrMore(Token('a')), Literal("x"));
        var result = rule.Parse("aaa", new ParseOptions { TraceSink = sink });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(sink.ToString(), Does.Not.Contain("SKIP | OneOrMore:"));
    }

    [Test]
    public void SourceText_on_zero_match_shortcut_is_empty_at_anchor()
    {
        // atLeast = 0 with inner that can't match at this position
        // (Inner.CannotMatchLookahead returns true). The engine's
        // empty-match shortcut fires and returns a Preserve composite
        // with a zero-length consumed span at lexer.Position. SourceText
        // is empty, SourceRange is zero-width at that offset.
        var counted = ZeroOrMore(Literal("X")).As("count");
        var rule = And(Literal("ab"), counted, Literal("Y").Preserve()).Preserve();
        var result = rule.Parse("abY");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        var countSymbol = result.Tree!.Find(counted)!;
        Assert.That(countSymbol.SourceText, Is.EqualTo(string.Empty));
        var range = countSymbol.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(2));
        Assert.That(range.End.CharIndex, Is.EqualTo(2));
    }

    [Test]
    public void SourceText_on_single_match_returns_inner_text()
    {
        // Counted loop ran the inner once and stopped. The composite's
        // consumed span covers exactly what inner matched, so SourceText
        // returns "X" regardless of inner's FlattenType.
        var counted = ZeroOrMore(Literal("X")).As("count");
        var rule = And(counted, Literal("Y").Preserve()).Preserve();
        var result = rule.Parse("XY");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        var countSymbol = result.Tree!.Find(counted)!;
        Assert.That(countSymbol.SourceText, Is.EqualTo("X"));
    }

    [Test]
    public void SourceText_on_many_matches_covers_the_whole_iteration_run()
    {
        // Counted loop ran the inner three times. The composite's
        // consumed span covers all three iterations, even when the
        // inner leaf is Delete-default and contributes nothing to
        // Children. ToString returns "" because no children survived.
        var counted = OneOrMore(Literal("X")).As("count");
        var rule = And(counted, Literal("Y").Preserve()).Preserve();
        var result = rule.Parse("XXXY");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        var countSymbol = result.Tree!.Find(counted)!;
        Assert.That(countSymbol.ToString(), Is.EqualTo(""),
            "Sanity check: inner is Delete, no surviving children.");
        Assert.That(countSymbol.SourceText, Is.EqualTo("XXX"));
        var range = countSymbol.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(3));
    }

    [Test]
    public void SourceText_on_BetweenInclusive_returns_matched_text_under_every_FlattenType()
    {
        // BetweenInclusive(1, 3, inner) matched against "ab" — inner
        // ran twice (atLeast=1 ≤ count=2 ≤ atMost=3). The shared body
        // is the same machinery OneOrMore / ZeroOrMore / Optional /
        // AtLeast / AtMost / Exactly all run, so one matrix test
        // covers them all.
        SourceTextFlattenTypeMatrixHelper.AssertSourceTextUnderEveryFlattenType(
            ruleBuilder: () => OneOrMore(OneOf("abc")),
            input: "ab",
            expectedSourceText: "ab");
    }

    [Test]
    public void OneOrMore_SourceRange_spans_all_matched_iterations()
    {
        // Three iterations of "abc". The composite range covers the
        // first 'a' through the last 'c', not just the most recent
        // iteration. Pins that the engine records the consumed span
        // once at parse end, not piecewise per-iteration (a buggy
        // implementation might overwrite Start/End each iteration).
        var rule = OneOrMore(Literal("abc").Preserve()).As("repeat");
        var result = rule.Parse("abcabcabc");

        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(9));
        Assert.That(range.End.Column, Is.EqualTo(9));
    }
}
