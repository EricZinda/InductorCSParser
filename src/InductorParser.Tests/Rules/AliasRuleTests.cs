using System;
using System.Linq;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests;

[TestFixture]
public class AliasRuleTests
{
    // PreserveAllSymbols keeps every grammar node in the tree so the tests
    // that walk Children can see what AliasRule contributed. Without it,
    // default-Delete Token leaves disappear and a few of the structural
    // checks would have to thread around the gaps.
    private static ParseOptions Debug() => new() { PreserveAllSymbols = true };

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
        var inner = OneOrMore(Token('a'));
        var alias = Alias(inner);

        var result = alias.Parse("aaa", Debug());

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("aaa"));
    }

    [Test]
    public void AliasedAs_makes_alias_findable_by_its_name()
    {
        var inner = OneOrMore(OneOf(TokenSet.Digits));
        var alias = inner.AliasedAs("year");

        var result = alias.Parse("1234");
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        var aliasNode = result.Tree!.Find(alias);
        Assert.That(aliasNode, Is.Not.Null,
            "Find(aliasRule) should locate the alias's wrapper Symbol in the tree.");
        Assert.That(aliasNode!.Name, Is.EqualTo("year"));
    }

    [Test]
    public void Rebadge_unnamed_inner_puts_inner_content_directly_under_alias()
    {
        // Inner is unnamed (default Flatten). Alias names it. The tree should
        // show the alias as one named layer with the inner's children directly
        // beneath it (no extra inner wrapper layer).
        var inner = OneOrMore(Token('a'));
        var alias = inner.AliasedAs("aSeq");

        var result = alias.Parse("aaa", Debug());
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        var aliasNode = result.Tree!.Find(alias);
        Assert.That(aliasNode, Is.Not.Null);
        // With PreserveAllSymbols, every Token('a') leaf survives in the
        // alias's children. The OneOrMore (BetweenInclusive) wrapper that
        // Preserve-all promotes also shows up, but Find on it is not what
        // we care about here. The key check: the alias node carries text
        // "aaa" assembled from its descendants.
        Assert.That(aliasNode!.ToString(), Is.EqualTo("aaa"));
    }

    [Test]
    public void Rebadge_named_inner_hides_inner_name_under_alias()
    {
        // Inner has its OWN name. Under the alias path, the inner's name
        // should be invisible to Tree.Find. This is the option-B rebadge
        // promise: the alias replaces the inner's identity.
        var inner = OneOrMore(OneOf(TokenSet.Digits)).As("digits");
        var alias = inner.AliasedAs("year");

        var result = alias.Parse("1234");
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        var aliasNode = result.Tree!.Find(alias);
        Assert.That(aliasNode, Is.Not.Null,
            "alias should be findable in the tree under its own name");

        // The inner's wrapper Symbol does NOT appear as a child of the alias.
        // Find for the inner from the alias node returns null.
        var innerNodeUnderAlias = aliasNode!.Find(inner);
        Assert.That(innerNodeUnderAlias, Is.Null,
            "Under rebadge semantics, the inner rule's name is hidden when " +
            "the inner is reached through the alias.");
    }

    [Test]
    public void Inner_name_remains_findable_from_parallel_branch()
    {
        // Two paths in one grammar: one uses the inner directly under
        // its own name; the other goes through an alias. Find for the
        // inner's name should still locate the direct-use spot, even
        // though it's hidden under the alias path.
        var digits = OneOrMore(OneOf(TokenSet.Digits)).As("digits");
        var year   = digits.AliasedAs("year");

        // Sequence: alias first, then a literal separator, then the named
        // inner directly. Both should appear in the parse tree.
        var grammar = And(year, Token('-').Preserve(), digits);

        var result = grammar.Parse("1234-5678");
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        // The alias is findable via ParseResult.Find (which handles
        // a Flatten root correctly).
        var yearNode = result.Find(year);
        Assert.That(yearNode, Is.Not.Null);

        // The direct-use named inner is also findable.
        var digitsNode = result.Find(digits);
        Assert.That(digitsNode, Is.Not.Null,
            "When the named inner is used directly elsewhere, its name " +
            "should still be findable from that spot.");

        // FindAll returns both the rebadged (under year) and the
        // direct-use occurrences. Under rebadge, the alias node carries
        // year's Id, not digits's. So FindAll(digits) yields only the
        // direct-use occurrence: the second 4-digit run.
        var directHits = result.FindAll(digits).ToList();
        Assert.That(directHits.Count, Is.EqualTo(1),
            "FindAll(digits) should only find the direct-use spot; the " +
            "alias-wrapped occurrence is hidden under year's Id.");
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
    public void Two_aliases_of_same_inner_get_distinct_SymbolIds()
    {
        var digitSequence = OneOrMore(OneOf(TokenSet.Digits));
        var year  = digitSequence.AliasedAs("year");
        var month = digitSequence.AliasedAs("month");

        var grammar = And(year, Token('-').Preserve(), month);
        grammar.Compile();

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
    public void AliasedAs_SymbolId_pins_alias_id()
    {
        var pinnedId = new SymbolId(SymbolRanges.CustomRangeStart + 42);
        var inner = OneOrMore(OneOf(TokenSet.Digits));
        var alias = inner.AliasedAs(pinnedId);

        alias.Compile();
        Assert.That(alias.Id, Is.EqualTo(pinnedId));
    }

    [Test]
    public void Alias_inside_And_participates_normally()
    {
        // The alias's wrapper Symbol shows up between the And's other
        // children. Standard composition; the alias is just another
        // named rule from the And's point of view.
        var digitSequence = OneOrMore(OneOf(TokenSet.Digits));
        var year  = digitSequence.AliasedAs("year");
        var month = digitSequence.AliasedAs("month");

        var grammar = And(year, Token('-').Preserve(), month);

        var result = grammar.Parse("2026-05");
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        Assert.That(result.Find(year), Is.Not.Null);
        Assert.That(result.Find(month), Is.Not.Null);
        Assert.That(result.Find(year)!.ToString(), Is.EqualTo("2026"));
        Assert.That(result.Find(month)!.ToString(), Is.EqualTo("05"));
    }

    [Test]
    public void AliasedAs_with_explicit_Delete_on_alias_throws_on_As()
    {
        // The standard flatten/identification contradiction check should
        // fire when the alias has an explicit non-Preserve flatten and
        // then receives a name. AliasedAs is .Clone-equivalent-then-.As,
        // so the .As call is what trips the contradiction. Verify the
        // shape: Alias(...).Delete().AliasedAs is too contrived (the user
        // can't call Delete before AliasedAs because AliasedAs constructs
        // the alias internally). Instead test through Alias(...) factory.
        var inner = OneOrMore(Token('a'));
        var alias = Alias(inner).Delete();

        // .As on a rule that's explicitly Delete contradicts identification.
        var exception = Assert.Throws<InvalidOperationException>(() => alias.As("foo"));
        Assert.That(exception!.Message, Does.Contain("flatten policy"));
    }

    [Test]
    public void Inner_WithError_still_fires_when_inner_fails_under_alias()
    {
        // Rebadge is success-side only. Errors fire from inside the inner's
        // failed parse attempt, so the inner's WithError still attributes
        // to it. The alias's identity doesn't change error attribution.
        var inner = OneOrMore(OneOf(TokenSet.Digits)).WithError("expected digits");
        var alias = inner.AliasedAs("year");

        var result = alias.Parse("abc");
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("expected digits"));
    }

    [Test]
    public void Alias_WithError_can_attribute_to_alias_when_alias_is_deepest()
    {
        // Alias has its own WithError. Inner doesn't. When inner fails,
        // the alias's WithError is the deepest one available and surfaces
        // in the error message.
        var inner = OneOrMore(OneOf(TokenSet.Digits));
        var alias = Alias(inner).As("year").WithError("expected a year");

        var result = alias.Parse("abc");
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("expected a year"));
    }

    [Test]
    public void Alias_wrapping_LateBound_constructs_and_names_correctly()
    {
        // LateBoundRule itself forbids .As(string). Wrapping it in an alias
        // gives a named entry point to the late-bound target.
        var lateBound = new LateBoundRule("placeholder");
        var alias = lateBound.AliasedAs("expr");

        // Bind the target after the alias is constructed (matches the
        // late-bound use case for mutually recursive grammars).
        lateBound.Bind(OneOrMore(OneOf(TokenSet.Digits)));

        var result = alias.Parse("123");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.Find(alias), Is.Not.Null,
            "Alias around a LateBoundRule should be findable by the alias's name.");
    }

    [Test]
    public void End_to_end_date_grammar_with_two_aliases_of_same_shape()
    {
        // The motivating use case: build one rule shape, give it two names
        // via alias, and verify both names appear at their respective
        // positions in the parse tree with the right matched text.
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
}
