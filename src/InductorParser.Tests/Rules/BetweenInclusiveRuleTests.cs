using System;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;

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
    // Grapheme rules (default FlattenType.Delete) stay in the tree and their
    // text is visible in the concatenated output. Without the flag the
    // tree would contain only non-Delete nodes, which is the correct
    // parse-time semantic, just not what these tests are looking at.
    private static ParseOptions Debug() => new() { PreserveAllSymbols = true };


    [Test]
    public void BetweenInclusive_exact_count_matches_exactly_N()
    {
        var rule = BetweenInclusive(3, 3, Grapheme('a'));
        var result = rule.Parse("aaa", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aaa"));
    }

    [Test]
    public void BetweenInclusive_exact_count_fails_when_too_few()
    {
        var rule = BetweenInclusive(3, 3, Grapheme('a'));
        var result = rule.Parse("aa");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
    }

    [Test]
    public void BetweenInclusive_exact_count_fails_when_too_many()
    {
        var rule = BetweenInclusive(3, 3, Grapheme('a'));
        var result = rule.Parse("aaaa");

        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void BetweenInclusive_at_lower_bound_succeeds()
    {
        var rule = BetweenInclusive(2, 5, Grapheme('a'));
        var result = AllOf(rule, OneOrMore(Grapheme('b'))).Parse("aabbb", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aabbb"));
    }

    [Test]
    public void BetweenInclusive_at_upper_bound_succeeds()
    {
        var rule = BetweenInclusive(2, 5, Grapheme('a'));
        var result = AllOf(rule, Grapheme('b')).Parse("aaaaab", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aaaaab"));
    }

    [Test]
    public void BetweenInclusive_stops_at_upper_bound_even_with_more_input()
    {
        var rule = AllOf(BetweenInclusive(1, 3, Grapheme('a')), OneOrMore(Grapheme('a')));
        var result = rule.Parse("aaaaa", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aaaaa"));
    }

    [Test]
    public void BetweenInclusive_one_below_lower_bound_fails()
    {
        var rule = BetweenInclusive(3, 5, Grapheme('a'));
        var result = rule.Parse("aa");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
    }

    [Test]
    public void BetweenInclusive_zero_zero_succeeds_with_no_matches()
    {
        var rule = AllOf(BetweenInclusive(0, 0, Grapheme('a')), Grapheme('b'));
        var result = rule.Parse("b", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("b"));
    }

    [Test]
    public void BetweenInclusive_zero_zero_does_not_consume_matching_input()
    {
        var rule = AllOf(BetweenInclusive(0, 0, Grapheme('a')), OneOrMore(Grapheme('a')));
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
        var result = BetweenInclusive(0, int.MaxValue, Grapheme('x')).Parse("");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Symbols, Is.Empty);
    }

    [Test]
    public void BetweenInclusive_failure_without_WithError_falls_back_to_positional_message()
    {
        var rule = BetweenInclusive(2, 4, Grapheme('a'));
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
        Assert.That(result.ErrorMessage, Does.StartWith("Unexpected end of input"));
    }

    [Test]
    public void BetweenInclusive_WithError_message_surfaces_on_failure()
    {
        var rule = BetweenInclusive(2, 4, Grapheme('a'))
            .WithError("need 2 to 4 a's");
        var result = rule.Parse("ab");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
        Assert.That(result.ErrorMessage, Is.EqualTo("need 2 to 4 a's"));
    }

    [Test]
    public void BetweenInclusive_inner_WithError_wins_at_equal_depth()
    {
        // Lower bound 1 forces the rule to fail at zero matches. The inner
        // Grapheme('a') tries at offset 0, reads 'b', fails, and records
        // its WithError message. BetweenInclusive then records at the same
        // offset with its own WithError, but the slot is already filled by
        // the inner's more-specific message, so the inner wins
        // (first-writer at equal depth).
        var rule = BetweenInclusive(1, int.MaxValue, Grapheme('a').WithError("want 'a'"))
                       .WithError("want at least one 'a'");

        var result = rule.Parse("bbb");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("want 'a'"));
    }

    [Test]
    public void BetweenInclusive_outer_WithError_wins_when_inner_has_none()
    {
        // Without a WithError on the inner, Grapheme('a') records at offset
        // 0 with a null message. BetweenInclusive then records at offset 0
        // with its own WithError, which claims the empty slot via the
        // equal-depth rule.
        var rule = BetweenInclusive(1, int.MaxValue, Grapheme('a'))
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
        // by verifying it. A failing parse here fails on the outer AllOf,
        // not on the BetweenInclusive.
        var rule = AllOf(
            BetweenInclusive(0, 3, Grapheme('a')).WithError("unreachable"),
            Grapheme('z'));
        var result = rule.Parse("aaab");

        Assert.That(result.Success, Is.False);
        // BetweenInclusive consumed three 'a's. The outer AllOf failed on
        // Grapheme('z') against 'b' at offset 3.
        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(result.ErrorMessage, Does.Not.Contain("unreachable"));
    }

    [Test]
    public void BetweenInclusive_inner_failure_still_contributes_to_deepest_failure()
    {
        // Known PEG heuristic quirk: a BetweenInclusive whose lower bound
        // is 0 and whose inner fails deeper than the required path can
        // still win the error message via deepest-failure-wins.
        //
        // Grammar: AllOf(BetweenInclusive(0, 1, AllOf(a, b, c-with-message)),
        //                x-with-message)
        // Input:   "abdy"
        //
        // The (0, 1) rule's inner reads "ab" then 'c' fails at offset 2,
        // recording "need 'c'". The outer rule catches and succeeds with
        // empty (lower bound 0). Grapheme('x') then fails at offset 0 with
        // its own "need 'x'". Deepest-wins picks offset 2: user sees
        // "need 'c'", pointing inside what was supposedly optional.
        var rule = AllOf(
            BetweenInclusive(0, 1,
                AllOf(Grapheme('a'),
                      Grapheme('b'),
                      Grapheme('c').WithError("need 'c'"))),
            Grapheme('x').WithError("need 'x'"));

        var result = rule.Parse("abdy");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
        Assert.That(result.ErrorMessage, Is.EqualTo("need 'c'"));
    }

    [Test]
    public void BetweenInclusive_factory_rejects_negative_atLeast()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BetweenInclusive(-1, 5, Grapheme('a')));
    }

    [Test]
    public void BetweenInclusive_factory_rejects_atMost_less_than_atLeast()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BetweenInclusive(3, 2, Grapheme('a')));
    }

    [Test]
    public void BetweenInclusive_factory_rejects_null_inner()
    {
        Assert.Throws<ArgumentNullException>(
            () => BetweenInclusive(0, 1, null!));
    }

    [Test]
    public void BetweenInclusive_trace_success_produces_expected_output()
    {
        var sink = NewSink();
        BetweenInclusive(2, 4, OneOf(RuneSet.Ascii.Letters))
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
    public void BetweenInclusive_trace_failure_produces_expected_output()
    {
        var sink = NewSink();
        BetweenInclusive(2, 4, Grapheme('a'))
            .Parse("a", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'a', Consumed: 1",
            "      SUCC | Grapheme: found 'a'",
            "      Lexer.Read: '<EOF>', Consumed: 1",
            "      FAIL | Grapheme: found '<EOF>', wanted 'a'",
            "      Lexer.RecordFailure: new deepest failure at char 1",
            "   FAIL | BetweenInclusive[2..4]: count= 1"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    // ----- Scanner-skip optimization (atLeast=0, atMost=int.MaxValue) -----
    //
    // BetweenInclusiveRule.TryCreateScannerSkip recognizes the shape
    // ZeroOrMore(FirstOf(match, AnyToken.Delete)) and jumps directly to
    // the next rune that could start a real match. The optimization only
    // triggers when atLeast=0 and atMost=int.MaxValue, which is the
    // ZeroOrMore-equivalent shape, so these tests construct that shape
    // explicitly via BetweenInclusive(0, int.MaxValue, ...).

    [Test]
    public void BetweenInclusive_scanner_shape_skips_deleted_fallback_runs()
    {
        var match = Literal("Sherlock").As("match").Flatten(SyntaxTree.FlattenType.Preserve);
        var scanner = BetweenInclusive(0, int.MaxValue, FirstOf(
            match,
            AnyToken().Flatten(SyntaxTree.FlattenType.Delete)
        )).As("scan").Flatten(SyntaxTree.FlattenType.Preserve);

        // The slow path would invoke the inner FirstOf once per rune (5000+
        // times). RuleCountLimit=100 caps invocations, so a successful parse
        // can only mean the scanner skip jumped over the 'x' run.
        string input = new string('x', 5000) + "Sherlock";
        var result = scanner.Parse(input, new ParseOptions
        {
            InputUnit = InputUnit.Rune,
            NormalizeInput = null,
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
        var scanner = BetweenInclusive(0, int.MaxValue, FirstOf(
            match,
            AnyToken().Flatten(SyntaxTree.FlattenType.Delete)
        )).As("scan").Flatten(SyntaxTree.FlattenType.Preserve);

        string input = new string('s', 5000) + "sHeRlOcK hOlMeS";
        var result = scanner.Parse(input, new ParseOptions
        {
            InputUnit = InputUnit.Rune,
            NormalizeInput = null,
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
        var match = FirstOf(
            LiteralIgnoreAsciiCase("Sherlock Holmes").Flatten(SyntaxTree.FlattenType.Preserve),
            LiteralIgnoreAsciiCase("John Watson").Flatten(SyntaxTree.FlattenType.Preserve),
            LiteralIgnoreAsciiCase("Irene Adler").Flatten(SyntaxTree.FlattenType.Preserve)
        ).As("match").Flatten(SyntaxTree.FlattenType.Preserve);
        var scanner = BetweenInclusive(0, int.MaxValue, FirstOf(
            match,
            AnyToken().Flatten(SyntaxTree.FlattenType.Delete)
        )).As("scan").Flatten(SyntaxTree.FlattenType.Preserve);

        string input = new string('j', 5000) + "jOhN wAtSoN";
        var result = scanner.Parse(input, new ParseOptions
        {
            InputUnit = InputUnit.Rune,
            NormalizeInput = null,
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
        var scanner = BetweenInclusive(0, int.MaxValue, FirstOf(
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
        var scanner = BetweenInclusive(0, int.MaxValue, FirstOf(
            match,
            AnyToken()
        )).As("scan").Flatten(SyntaxTree.FlattenType.Preserve);

        var result = scanner.Parse("xxS");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("xxS"));
    }

    [Test]
    public void Sealed_BetweenInclusive_rejects_Flatten()
    {
        var rule = BetweenInclusive(1, 5, Grapheme('a'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.Flatten(FlattenType.Preserve));
    }

    [Test]
    public void Sealed_BetweenInclusive_rejects_WithError()
    {
        var rule = BetweenInclusive(1, 5, Grapheme('a'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.WithError("late"));
    }

    [Test]
    public void Sealed_BetweenInclusive_rejects_As()
    {
        var rule = BetweenInclusive(1, 5, Grapheme('a'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.As("late"));
    }
}
