using System;
using System.Linq;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;

namespace InductorParser.Tests;

// AliasRule is a single-child composite: it wraps one inner rule, gives
// it a fresh name/Id, and at parse time produces a Symbol carrying the
// alias's identity over the inner's matched content. When the inner is
// Preserve, the alias rebadges — its Symbol replaces the inner's rather
// than nesting it. Error behavior follows docs/ErrorArchitecture.md: the
// alias records at its own start (the Or / Peek / Not category), the
// rebadge is success-only and never touches failures.
[TestFixture]
public class AliasRuleTests
{
    // PreserveAllSymbols keeps every grammar node in the tree so the tests
    // that walk Children can see what AliasRule contributed. Without it,
    // default-Delete Token leaves disappear and a few of the structural
    // checks would have to thread around the gaps.
    private static ParseOptions Debug() => new() { PreserveAllSymbols = true };

    // --- Success ---------------------------------------------------------

    [Test]
    public void Alias_factory_returns_AliasRule_instance()
    {
        var inner = OneOrMore(OneOf(TokenSet.Digits));
        var alias = Alias(inner);

        Assert.That(alias, Is.InstanceOf<AliasRule>());
    }

    [Test]
    public void AliasedAs_sets_name_on_alias_only()
    {
        var inner = OneOrMore(OneOf(TokenSet.Digits));
        var alias = inner.AliasedAs("digits");

        Assert.That(alias, Is.InstanceOf<AliasRule>());
        Assert.That(alias.Name, Is.EqualTo("digits"));
        Assert.That(inner.Name, Is.Null,
            "Original rule's Name should be untouched by AliasedAs.");
    }

    [Test]
    public void Alias_with_no_name_parses_inner_and_flattens_into_parent()
    {
        var alias = Alias(OneOrMore(Token('a')));

        var result = alias.Parse("aaa", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aaa"));
    }

    [Test]
    public void AliasedAs_makes_alias_findable_by_its_name()
    {
        var alias = OneOrMore(OneOf(TokenSet.Digits)).AliasedAs("year");

        var result = alias.Parse("1234");
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        var aliasNode = result.Tree!.Find(alias);
        Assert.That(aliasNode, Is.Not.Null,
            "Find(aliasRule) should locate the alias's Symbol in the tree.");
        Assert.That(aliasNode!.Name, Is.EqualTo("year"));
        Assert.That(aliasNode.ToString(), Is.EqualTo("1234"));
    }

    // --- Rebadge behavior (edge cases specific to this composite) --------

    [Test]
    public void Rebadge_unnamed_inner_puts_inner_content_directly_under_alias()
    {
        // Inner is unnamed (default Flatten). The alias names it. The tree
        // shows the alias as one named layer with the inner's children
        // directly beneath it, no extra inner layer.
        var alias = OneOrMore(Token('a')).AliasedAs("aSeq");

        var result = alias.Parse("aaa", Debug());
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        var aliasNode = result.Tree!.Find(alias);
        Assert.That(aliasNode, Is.Not.Null);
        Assert.That(aliasNode!.ToString(), Is.EqualTo("aaa"));
    }

    [Test]
    public void Rebadge_named_inner_hides_inner_name_under_alias()
    {
        // The inner has its OWN name. Under the alias path the inner's name
        // is invisible to Find: the alias's Symbol replaces the inner's.
        var inner = OneOrMore(OneOf(TokenSet.Digits)).As("digits");
        var alias = inner.AliasedAs("year");

        var result = alias.Parse("1234");
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        var aliasNode = result.Tree!.Find(alias);
        Assert.That(aliasNode, Is.Not.Null,
            "alias should be findable in the tree under its own name");
        Assert.That(aliasNode!.Find(inner), Is.Null,
            "Under rebadge, the inner rule's name is hidden when the inner " +
            "is reached through the alias.");
    }

    [Test]
    public void Inner_name_remains_findable_from_parallel_branch()
    {
        // One grammar, two paths to the same inner: one through an alias,
        // one direct. The direct use keeps the inner's name findable; the
        // alias path rebadges it away.
        var digits = OneOrMore(OneOf(TokenSet.Digits)).As("digits");
        var year   = digits.AliasedAs("year");
        var grammar = And(year, Token('-').Preserve(), digits);

        var result = grammar.Parse("1234-5678");
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        Assert.That(result.Find(year), Is.Not.Null);

        // FindAll(digits) yields only the direct-use occurrence; the
        // alias-wrapped one carries year's Id, not digits's.
        var directHits = result.FindAll(digits).ToList();
        Assert.That(directHits.Count, Is.EqualTo(1),
            "FindAll(digits) should only find the direct-use spot.");
        Assert.That(directHits[0].ToString(), Is.EqualTo("5678"));
    }

    [Test]
    public void Aliased_Symbol_carries_alias_Id_not_inner_Id()
    {
        var inner = OneOrMore(OneOf(TokenSet.Digits)).As("digits");
        var alias = inner.AliasedAs("year");

        var result = alias.Parse("1234");
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        Assert.That(result.Tree!.Is(alias), Is.True,
            "The top-level Symbol's Id should match the alias's Id.");
        Assert.That(result.Tree.Is(inner), Is.False,
            "The top-level Symbol should NOT be identified as the inner rule.");
    }

    [Test]
    public void Alias_inside_And_participates_normally()
    {
        // The alias's Symbol shows up between the And's other children:
        // standard composition, the alias is just another named rule from
        // the And's point of view.
        var digitSequence = OneOrMore(OneOf(TokenSet.Digits));
        var year  = digitSequence.AliasedAs("year");
        var month = digitSequence.AliasedAs("month");

        var result = And(year, Token('-').Preserve(), month).Parse("2026-05");
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        Assert.That(result.Find(year)!.ToString(), Is.EqualTo("2026"));
        Assert.That(result.Find(month)!.ToString(), Is.EqualTo("05"));
    }

    // --- Failure position ------------------------------------------------

    [Test]
    public void Alias_inner_failure_at_start_reports_position_zero()
    {
        var alias = Alias(Token('a'));
        var result = alias.Parse("b");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
    }

    [Test]
    public void Alias_failure_after_consumed_prefix_reports_at_alias_start()
    {
        // An earlier rule consumes "x", so the alias begins at offset 1.
        // The alias's failure must land at 1 (its own start), not at 0.
        var rule = And(Token('x'), Alias(Token('a')));
        var result = rule.Parse("xb");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
    }

    [Test]
    public void Alias_without_WithError_falls_back_to_positional_message()
    {
        // No WithError anywhere: the alias and the inner both record only
        // mechanical fallbacks, and BuildErrorMessage renders the generic
        // positional template.
        var alias = Alias(Token('a'));
        var result = alias.Parse("b");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.StartWith("Parse failed at offset 0"));
    }

    // --- WithError message propagation -----------------------------------

    [Test]
    public void Alias_WithError_surfaces_when_inner_fails()
    {
        var alias = Alias(Token('a')).As("letter").WithError("expected a letter");
        var result = alias.Parse("1");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected a letter"));
    }

    [Test]
    public void Alias_named_WithError_anchors_at_deepest_inner_failure()
    {
        // Literal("abc") on "abZ" reads 'a','b', then mismatches 'Z' at
        // offset 2 and records a mechanical failure there. The alias
        // carries a named WithError; composite anchoring records it at
        // the deepest position the inner reached (offset 2), where it
        // ties the mechanical failure on depth and wins the named-beats-
        // mechanical tie-break. See docs/ErrorArchitecture.md.
        var alias = Alias(Literal("abc")).As("word").WithError("expected the word");
        var result = alias.Parse("abZ");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected the word"));
    }

    [Test]
    public void Alias_without_WithError_surfaces_inner_WithError()
    {
        // The inner carries a WithError; the alias does not. The inner's
        // named failure must survive the alias's failure path. The alias
        // is a structural composite and never clears failures. The inner's
        // message surfaces, anchored where the inner recorded it.
        var inner = Literal("abc").WithError("expected abc here");
        var alias = Alias(inner).As("word");
        var result = alias.Parse("abZ");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected abc here"));
    }

    [Test]
    public void Alias_WithError_anchors_at_the_inner_failure_point_not_the_alias_start()
    {
        // "XX" is consumed first, so the alias begins at offset 2. Inside
        // it, Literal("abc") on "abZ" mismatches at offset 4. The alias's
        // WithError anchors at the deepest position the inner reached
        // (offset 4), the inner's failure point, not at the alias's own
        // start (offset 2). See docs/ErrorArchitecture.md, "Where each
        // rule records its failure".
        var alias = Alias(Literal("abc")).As("word").WithError("expected the word");
        var rule = And(Literal("XX").Preserve(), alias);
        var result = rule.Parse("XXabZ");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(4));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected the word"));
    }

    [Test]
    public void Alias_plain_WithError_does_not_override_an_inner_WithError()
    {
        // Both the inner rule and the alias carry a .WithError. The inner
        // records its message at the spot it got stuck (offset 2), and
        // the alias records its own at the deepest position the inner
        // reached, the same offset 2. An exact-depth tie goes to the
        // first writer, and the inner records before the alias, so the
        // inner's message wins. A plain .WithError on the alias can't
        // override an inner one. See docs/ErrorArchitecture.md.
        var inner = Literal("abc").WithError("inner: expected abc");
        var alias = Alias(inner).As("word").WithError("alias: expected a word");
        var result = alias.Parse("abZ");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
        Assert.That(result.ErrorMessage, Is.EqualTo("inner: expected abc"));
    }

    [Test]
    public void Alias_forced_WithError_overrides_an_inner_WithError()
    {
        // To override an inner .WithError from the alias, mark the
        // alias's .WithError forced. A forced failure beats every
        // non-forced failure at any depth, so the alias's message wins
        // over the inner's. See docs/ErrorArchitecture.md.
        var inner = Literal("abc").WithError("inner: expected abc");
        var alias = Alias(inner).As("word").WithError("alias: expected a word", forced: true);
        var result = alias.Parse("abZ");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
        Assert.That(result.ErrorMessage, Is.EqualTo("alias: expected a word"));
    }

    // --- Identity --------------------------------------------------------

    [Test]
    public void Two_aliases_of_same_inner_get_distinct_SymbolIds()
    {
        var digitSequence = OneOrMore(OneOf(TokenSet.Digits));
        var year  = digitSequence.AliasedAs("year");
        var month = digitSequence.AliasedAs("month");

        And(year, Token('-').Preserve(), month).Compile();

        Assert.That(year.Id, Is.Not.EqualTo(month.Id),
            "Two aliases with different names should get different SymbolIds.");
    }

    [Test]
    public void Two_aliases_with_same_name_fail_CheckNameUniqueness()
    {
        var digitSequence = OneOrMore(OneOf(TokenSet.Digits));
        var year1 = digitSequence.AliasedAs("year");
        var year2 = digitSequence.AliasedAs("year");

        var grammar = And(year1, Token('-').Preserve(), year2);

        var exception = Assert.Throws<InvalidOperationException>(() => grammar.Compile());
        Assert.That(exception!.Message, Does.Contain("year"));
        Assert.That(exception.Message, Does.Contain("share the name"));
    }

    [Test]
    public void AliasedAs_SymbolId_sets_explicit_alias_id()
    {
        var explicitId = new SymbolId(SymbolRanges.CustomRangeStart + 42);
        var alias = OneOrMore(OneOf(TokenSet.Digits)).AliasedAs(explicitId);

        alias.Compile();
        Assert.That(alias.Id, Is.EqualTo(explicitId));
    }

    [Test]
    public void AliasedAs_with_explicit_Delete_then_As_throws_the_flatten_contradiction()
    {
        // .Delete() on the alias sets a non-Preserve policy explicitly.
        // A later .As(...) needs Preserve so the alias's Symbol reaches
        // the tree, so the two requests contradict and .As throws. This
        // is the standard base-class guardrail, inherited unchanged.
        var alias = Alias(OneOrMore(Token('a'))).Delete();

        var exception = Assert.Throws<InvalidOperationException>(() => alias.As("foo"));
        Assert.That(exception!.Message, Does.Contain("flatten policy"));
    }

    // --- LateBound interaction and end-to-end ----------------------------

    [Test]
    public void Alias_wrapping_LateBound_constructs_and_names_correctly()
    {
        // LateBoundRule itself forbids .As(string). Wrapping it in an alias
        // gives a named entry point to the late-bound target.
        var lateBound = new LateBoundRule("placeholder");
        var alias = lateBound.AliasedAs("expr");
        lateBound.Bind(OneOrMore(OneOf(TokenSet.Digits)));

        var result = alias.Parse("123");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.Find(alias), Is.Not.Null,
            "Alias around a LateBoundRule should be findable by the alias's name.");
    }

    [Test]
    public void End_to_end_date_grammar_with_two_aliases_of_same_shape()
    {
        // The motivating use case: build one rule shape, give it three
        // names via alias, and verify each appears at its position with
        // the right matched text.
        var digitSequence = OneOrMore(OneOf(TokenSet.Digits));
        var year  = digitSequence.AliasedAs("year");
        var month = digitSequence.AliasedAs("month");
        var day   = digitSequence.AliasedAs("day");

        var grammar = And(year, Token('-').Preserve(), month, Token('-').Preserve(), day);
        var result = grammar.Parse("2026-05-14");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Find(year)!.ToString(),  Is.EqualTo("2026"));
        Assert.That(result.Find(month)!.ToString(), Is.EqualTo("05"));
        Assert.That(result.Find(day)!.ToString(),   Is.EqualTo("14"));
    }

    // --- Sealed-rule rejection -------------------------------------------

    [Test]
    public void Sealed_Alias_rejects_Flatten()
    {
        var rule = Alias(Token('a'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.Flatten(FlattenType.Preserve));
    }

    [Test]
    public void Sealed_Alias_rejects_WithError()
    {
        var rule = Alias(Token('a'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.WithError("late"));
    }

    [Test]
    public void Sealed_Alias_rejects_As()
    {
        var rule = Alias(Token('a'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.As("late"));
    }

    // --- Trace -----------------------------------------------------------

    [Test]
    [RecursiveEngineOnly]
    public void Alias_trace_success_produces_expected_output()
    {
        // Alias opens a transaction (depth=1). Token inside opens its own
        // (depth=2) and succeeds. Alias then emits its success line at
        // depth=1.
        var sink = NewSink();
        Alias(Token('a')).Parse("a", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'a', Consumed: 1",
            "      SUCC | Token: found 'a'",
            "   SUCC | Alias: inner matched"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Alias_trace_failure_produces_expected_output()
    {
        var sink = NewSink();
        Alias(Token('a')).Parse("b", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'b', Consumed: 1",
            "      FAIL | Token: found 'b', wanted 'a'",
            "   FAIL | Alias: inner failed"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    // --- SourceText / SourceRange ----------------------------------------

    [Test]
    public void SourceText_on_Alias_returns_matched_text_under_every_FlattenType()
    {
        SourceTextFlattenTypeMatrixHelper.AssertSourceTextUnderEveryFlattenType(
            ruleBuilder: () => Alias(Literal("abc")),
            input: "abc",
            expectedSourceText: "abc");
    }

    [Test]
    public void SourceRange_on_Alias_spans_the_inner_match_after_a_prefix()
    {
        // The alias's Symbol span covers exactly the inner's match. After
        // a two-char prefix the alias begins at offset 2 and "abc" ends
        // the range at offset 5.
        var alias = Alias(Literal("abc")).As("word");
        var rule = And(Literal("XX").Preserve(), alias).Preserve();
        var result = rule.Parse("XXabc");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        var aliasSymbol = result.Tree!.Find(alias)!;
        Assert.That(aliasSymbol.SourceText, Is.EqualTo("abc"));
        var range = aliasSymbol.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(2));
        Assert.That(range.End.CharIndex, Is.EqualTo(5));
    }
}
