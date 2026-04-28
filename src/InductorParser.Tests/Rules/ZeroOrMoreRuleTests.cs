using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;

namespace InductorParser.Tests;

[TestFixture]
public class ZeroOrMoreRuleTests
{
    // ZeroOrMore has no failure path at all, so this fixture only carries
    // success tests. The TestArchitecture doc calls this out explicitly.

    // Tree.ToString() assertions use PreserveAllSymbols so Token
    // leaves (default FlattenType.Delete) stay in the tree.
    private static ParseOptions Debug() => new() { PreserveAllSymbols = true };

    [Test]
    public void ZeroOrMore_with_zero_matches_succeeds_with_empty_consumption()
    {
        // Input doesn't start with 'a', so the inner rule fails on its very
        // first attempt. ZeroOrMore catches that and succeeds with zero
        // children, leaving the lexer position unchanged.
        var rule = AllOf(ZeroOrMore(Token('a')), Token('b'));
        var result = rule.Parse("b", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("b"));
    }

    [Test]
    public void ZeroOrMore_matches_multiple_occurrences_greedily()
    {
        var rule = AllOf(ZeroOrMore(Token('a')), Token('b'));
        var result = rule.Parse("aaab", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aaab"));
    }

    [Test]
    public void ZeroOrMore_stops_at_first_inner_mismatch_and_surrounding_rule_continues()
    {
        // Inner matches 'a' twice, then on the third try sees 'b' and the
        // inner rule fails. ZeroOrMore commits the two successful iterations
        // and hands 'b' off to the next rule in the AllOf.
        var rule = AllOf(ZeroOrMore(Token('a')), Token('b'), Token('c'));
        var result = rule.Parse("aabc", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aabc"));
    }

    [Test]
    public void ZeroOrMore_scanner_shape_skips_deleted_fallback_runs()
    {
        var match = Literal("Sherlock").As("match").Flatten(SyntaxTree.FlattenType.Preserve);
        var scanner = ZeroOrMore(FirstOf(
            match,
            AnyToken().Flatten(SyntaxTree.FlattenType.Delete)
        )).As("scan").Flatten(SyntaxTree.FlattenType.Preserve);

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
    public void ZeroOrMore_scanner_shape_prefilters_ascii_case_insensitive_literal()
    {
        var match = LiteralIgnoreAsciiCase("Sherlock Holmes")
            .As("match")
            .Flatten(SyntaxTree.FlattenType.Preserve);
        var scanner = ZeroOrMore(FirstOf(
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
    public void ZeroOrMore_scanner_shape_prefilters_nested_literal_alternates()
    {
        var match = FirstOf(
            LiteralIgnoreAsciiCase("Sherlock Holmes").Flatten(SyntaxTree.FlattenType.Preserve),
            LiteralIgnoreAsciiCase("John Watson").Flatten(SyntaxTree.FlattenType.Preserve),
            LiteralIgnoreAsciiCase("Irene Adler").Flatten(SyntaxTree.FlattenType.Preserve)
        ).As("match").Flatten(SyntaxTree.FlattenType.Preserve);
        var scanner = ZeroOrMore(FirstOf(
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
    public void ZeroOrMore_scanner_shape_preserves_debug_tree_when_requested()
    {
        var match = Literal("S").As("match").Flatten(SyntaxTree.FlattenType.Preserve);
        var scanner = ZeroOrMore(FirstOf(
            match,
            AnyToken().Flatten(SyntaxTree.FlattenType.Delete)
        )).As("scan").Flatten(SyntaxTree.FlattenType.Preserve);

        var result = scanner.Parse("xxS", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("xxS"));
    }

    [Test]
    public void ZeroOrMore_scanner_shape_does_not_skip_preserved_fallback()
    {
        var match = Literal("S").As("match").Flatten(SyntaxTree.FlattenType.Preserve);
        var scanner = ZeroOrMore(FirstOf(
            match,
            AnyToken()
        )).As("scan").Flatten(SyntaxTree.FlattenType.Preserve);

        var result = scanner.Parse("xxS");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("xxS"));
    }

    [Test]
    public void ZeroOrMore_trace_with_matches_produces_expected_output()
    {
        var sink = NewSink();
        AllOf(ZeroOrMore(OneOf(RuneSet.Ascii.Letters)), Eof())
            .Parse("ab", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "         Lexer.Read: 'a', Consumed: 1",
            "         SUCC | OneOf: found 'a', wanted one of '[A-Z,a-z]'",
            "         Lexer.Read: 'b', Consumed: 2",
            "         SUCC | OneOf: found 'b', wanted one of '[A-Z,a-z]'",
            "         Lexer.Read: '<EOF>', Consumed: 2",
            "         FAIL | OneOf: found '<EOF>', wanted one of '[A-Z,a-z]'",
            "         Lexer.RecordFailure: new deepest failure at char 2",
            "      SUCC | ZeroOrMore: count= 2",
            "   SUCC | Eof",
            "   SUCC | AllOf: found 2"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void ZeroOrMore_trace_with_zero_matches_produces_expected_output()
    {
        // ZeroOrMore has no failure path, so even "no matches" is a
        // success, with count= 0. Wrapped in AllOf so the indentation
        // shows the full transaction nesting.
        var sink = NewSink();
        AllOf(ZeroOrMore(Token('a')), Eof())
            .Parse("", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "         Lexer.Read: '<EOF>', Consumed: 0",
            "         FAIL | Token: found '<EOF>', wanted 'a'",
            "      SUCC | ZeroOrMore: count= 0",
            "   SUCC | Eof",
            "   SUCC | AllOf: found 2"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }
}
