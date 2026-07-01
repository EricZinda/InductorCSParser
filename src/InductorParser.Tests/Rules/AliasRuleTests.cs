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
// Preserve, the alias substitutes its Symbol for the inner's rather than
// nesting it. Error behavior follows docs/ErrorArchitecture.md: the
// alias records at its own start (the Or / Peek / Not category), the
// identity substitution is success-only and never touches failures.
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
    public void Alias_factory_rejects_null_inner()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => Alias(null!));

        Assert.That(exception!.ParamName, Is.EqualTo("inner"));
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
        Assert.That(aliasNode!.DisplayName, Is.EqualTo("year"));
        Assert.That(aliasNode.ToString(), Is.EqualTo("1234"));
    }

    // --- Identity substitution (edge cases specific to this composite) ---

    [Test]
    public void Unnamed_inner_puts_inner_content_directly_under_alias()
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
    public void Named_inner_hides_inner_name_under_alias()
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
            "The inner rule's name is hidden when the inner is reached " +
            "through the alias.");
    }

    [Test]
    public void Alias_of_alias_of_leaf_substitutes_to_a_single_leaf()
    {
        // Aliasing an alias of a Preserve leaf. AliasRule's tenet: a
        // Preserve alias's Symbol behaves like the inner's Symbol would,
        // except for its identity. The inner here is itself an alias that
        // emits a leaf, so the outer alias should also be a single leaf
        // carrying "5", with the inner alias's identity hidden.
        var innerAlias = OneOf(TokenSet.Digits).AliasedAs("inner");
        var outerAlias = innerAlias.AliasedAs("outer");

        var result = outerAlias.Parse("5");
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        Assert.That(result.Tree!.Is(outerAlias), Is.True);
        Assert.That(result.Tree.ToString(), Is.EqualTo("5"));
        Assert.That(result.Tree.Find(innerAlias), Is.Null,
            "The inner alias's identity should be hidden under the outer alias.");
        Assert.That(result.Tree.Children.Count(), Is.EqualTo(0),
            "Outer alias over a leaf inner should itself be a leaf.");
    }

    [Test]
    public void Inner_name_remains_findable_from_parallel_branch()
    {
        // One grammar, two paths to the same inner: one through an alias,
        // one direct. The direct use keeps the inner's name findable; the
        // alias path substitutes it away.
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
        Assert.That(result.ErrorMessage, Is.EqualTo("Unexpected 'b' at line 1, column 1."));
    }

    // --- WithError message propagation -----------------------------------

    [Test]
    public void Alias_WithError_surfaces_when_inner_fails()
    {
        var alias = Alias(Token('a')).As("letter").WithError("expected a letter");
        var result = alias.Parse("1");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected a letter at line 1, column 1."));
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
        Assert.That(result.ErrorMessage, Is.EqualTo("expected the word at line 1, column 3."));
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
        Assert.That(result.ErrorMessage, Is.EqualTo("expected abc here at line 1, column 3."));
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
        Assert.That(result.ErrorMessage, Is.EqualTo("expected the word at line 1, column 5."));
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
        Assert.That(result.ErrorMessage, Is.EqualTo("inner: expected abc at line 1, column 3."));
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
        Assert.That(result.ErrorMessage, Is.EqualTo("alias: expected a word at line 1, column 3."));
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
        // The alias is the root rule and the LateBoundRule it wraps is
        // transparent, so the alias's Symbol must be the tree's top node
        // directly. Tree.Is(alias) verifies that shape. Tree.Find(alias)
        // would only prove an alias Symbol exists somewhere in the tree
        // and would still pass if a stray layer wrapped it.
        Assert.That(result.Tree, Is.Not.Null);
        Assert.That(result.Tree!.Is(alias), Is.True,
            "The alias's Symbol should be the top node of the tree.");
        Assert.That(result.Tree.ToString(), Is.EqualTo("123"));
    }

    [Test]
    public void LateBound_bound_to_an_Alias_forwards_and_keeps_the_alias_identity()
    {
        // The mirror of the test above: instead of an alias wrapping a
        // LateBoundRule, here a LateBoundRule's target IS an AliasRule.
        // The LateBoundRule forwards transparently, so the alias's Symbol
        // and its name survive into the tree.
        var alias = OneOrMore(OneOf(TokenSet.Digits)).AliasedAs("digits");
        var lateBound = new LateBoundRule("placeholder");
        lateBound.Bind(alias);

        var result = lateBound.Parse("123");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        // Check the tree's shape, not just that an alias Symbol exists
        // somewhere: result.Find searches recursively, so it would pass
        // even if a stray layer wrapped the alias. The LateBoundRule is
        // transparent, so the alias's Symbol must be the single top-level
        // Symbol, with nothing above it.
        Assert.That(result.Symbols.Count, Is.EqualTo(1));
        Assert.That(result.Symbols[0].Is(alias), Is.True,
            "The alias's Symbol should be the top node; the LateBoundRule adds no layer.");
        Assert.That(result.Symbols[0].ToString(), Is.EqualTo("123"));
    }

    [Test]
    public void Alias_wrapping_a_chain_of_LateBoundRules_is_findable()
    {
        // The alias wraps `outer`, a LateBoundRule bound to `inner`,
        // another LateBoundRule, bound to the real rule. The alias names
        // the whole two-link chain, and the chain forwards through to the
        // digits.
        var inner = new LateBoundRule("inner");
        var outer = new LateBoundRule("outer");
        var alias = outer.AliasedAs("number");
        outer.Bind(inner);
        inner.Bind(OneOrMore(OneOf(TokenSet.Digits)));

        var result = alias.Parse("2026");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        // The alias is the root rule, and the two-link LateBoundRule chain
        // it wraps is transparent, so the alias's Symbol is the tree's top
        // node directly. result.Tree.Is(alias) verifies that shape;
        // result.Find(alias) would only prove an alias Symbol exists
        // somewhere in the tree.
        Assert.That(result.Tree, Is.Not.Null);
        Assert.That(result.Tree!.Is(alias), Is.True,
            "The alias's Symbol should be the top node of the tree.");
        Assert.That(result.Tree.ToString(), Is.EqualTo("2026"));
    }

    [Test]
    public void LateBound_bound_to_an_Alias_that_wraps_another_LateBound()
    {
        // A mixed chain: outerLate -> alias -> innerLate -> real rule.
        // The AliasRule sits between two LateBoundRule layers. The outer
        // LateBoundRule forwards into the alias, the alias substitutes its
        // identity, and the inner LateBoundRule forwards into the real
        // digits rule.
        var innerLate = new LateBoundRule("innerLate");
        var alias = innerLate.AliasedAs("aliasLayer");
        var outerLate = new LateBoundRule("outerLate");
        outerLate.Bind(alias);
        innerLate.Bind(OneOrMore(OneOf(TokenSet.Digits)));

        var result = outerLate.Parse("777");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        // outerLate and innerLate are both transparent, so the alias is
        // the single top-level Symbol: no LateBoundRule layer above it and
        // none below it. result.Symbols verifies that shape; result.Find
        // would only prove an alias Symbol exists at some depth.
        Assert.That(result.Symbols.Count, Is.EqualTo(1));
        Assert.That(result.Symbols[0].Is(alias), Is.True,
            "The alias's Symbol should be the single top node of the tree.");
        Assert.That(result.Symbols[0].ToString(), Is.EqualTo("777"));
    }

    [Test]
    public void Alias_of_a_LateBound_substitutes_for_the_target_like_aliasing_it_directly()
    {
        // A LateBoundRule reports its target's FlattenType, so it's fully
        // transparent: Alias(lateBound) behaves exactly like Alias(target).
        // When the target is Preserve, the alias substitutes its identity
        // for it, so the target's Symbol is replaced by the alias's, not
        // kept as a layer.
        var target = And(Token('1'), Token('2')).As("target").Flatten(FlattenType.Preserve);
        var lateBound = new LateBoundRule("placeholder");
        lateBound.Bind(target);
        var alias = lateBound.AliasedAs("aliasName");

        var result = alias.Parse("12");
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        Assert.That(result.Tree!.Is(alias), Is.True,
            "The alias's Symbol is the top node.");
        Assert.That(result.Tree!.Find(target), Is.Null,
            "The target is replaced away, exactly as when aliasing it directly.");
    }

    [Test]
    public void Alias_of_a_LateBound_substitutes_consistently_in_normal_and_PreserveAllSymbols_mode()
    {
        // PreserveAllSymbols is a debug mode that turns every rule Preserve;
        // it must agree with normal parsing about structure (only adding
        // Delete leaves, never moving or dropping nodes). A LateBoundRule
        // reporting its target's FlattenType makes the alias substitute for
        // the target identically in both modes. Before the fix the modes
        // disagreed: normal mode kept the target as a layer, PreserveAll
        // dropped it.
        var target = And(Token('1'), Token('2')).As("target").Flatten(FlattenType.Preserve);
        var lateBound = new LateBoundRule("placeholder");
        lateBound.Bind(target);
        var alias = lateBound.AliasedAs("aliasName");

        var normal = alias.Parse("12");
        var debug = alias.Parse("12", new ParseOptions { PreserveAllSymbols = true });

        Assert.That(normal.Success, Is.True, normal.ErrorMessage);
        Assert.That(debug.Success, Is.True, debug.ErrorMessage);
        Assert.That(normal.Tree!.Find(target), Is.Null,
            "normal mode replaces the target's identity with the alias's");
        Assert.That(debug.Tree!.Find(target), Is.Null,
            "PreserveAllSymbols replaces it the same way, so the two modes agree");
    }

    // Serialize a Symbol subtree to "id(child...)" / "id'text'" so the
    // regular parse tree and the PreserveAllSymbols-then-Flatten tree can
    // be compared id-for-id. The two have to match exactly. PreserveAllSymbols
    // is a debug view of what production parsing produces, so capturing
    // every Symbol and then collapsing via post-hoc Flatten() should give
    // back the same tree the regular parse built directly.
    private static string Serialize(Symbol symbol)
    {
        var builder = new System.Text.StringBuilder();
        Append(symbol, builder);
        return builder.ToString();

        static void Append(Symbol s, System.Text.StringBuilder b)
        {
            b.Append(s.Id.Value);
            if (s.Children.Count == 0)
            {
                b.Append('\'').Append(s.ToString()).Append('\'');
                return;
            }
            b.Append('(');
            foreach (var child in s.Children) Append(child, b);
            b.Append(')');
        }
    }

    private static string SerializeForest(System.Collections.Generic.IEnumerable<Symbol> symbols)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var s in symbols) builder.Append(Serialize(s));
        return builder.ToString();
    }

    [Test]
    public void Alias_over_a_leaf_inner_flattens_back_to_the_default_tree()
    {
        // Parsing with PreserveAllSymbols and then calling Symbol.Flatten()
        // on the resulting tree must reproduce the regular (no-flag) parse
        // tree exactly, ids included. PreserveAllSymbols is a debug-mode
        // view that captures every Symbol. Post-hoc Flatten() then applies
        // the FlattenType policies that would have been applied at parse
        // time, recovering the production shape. The two paths have to
        // agree, or the debug view diverges from production and isn't a
        // faithful representation.
        //
        // The specific case: an unnamed alias is FlattenType.Flatten
        // (transparent) in the regular parse, so the inner OneOf leaf
        // surfaces in the parent with its rune id. PreserveAllSymbols
        // captures the alias as Preserve; a careless implementation
        // substituted the alias's custom id onto the leaf during capture,
        // and because a FlattenType.Flatten leaf survives post-hoc
        // Flatten() unchanged, the captured tree no longer flattened back
        // to the rune-id leaf that the regular parse produced.
        var grammar = OneOrMore(Alias(OneOf(TokenSet.Runes("ab")))).Preserve();
        grammar.Compile();

        var def = grammar.Parse("ab");
        var debug = grammar.Parse("ab", new ParseOptions { PreserveAllSymbols = true });
        Assert.That(def.Success, Is.True, def.ErrorMessage);
        Assert.That(debug.Success, Is.True, debug.ErrorMessage);

        string defTree = SerializeForest(def.Symbols);
        string flattenedDebug = SerializeForest(debug.Symbols.SelectMany(s => s.Flatten()));
        Assert.That(flattenedDebug, Is.EqualTo(defTree),
            "PreserveAllSymbols+Flatten must reproduce the default parse tree for an aliased leaf");
        // And the leaves carry their rune ids, not the alias's custom id.
        Assert.That(def.Symbols.Single().Children.Select(c => c.Id),
            Is.EqualTo(new[] { new SymbolId('a'), new SymbolId('b') }));
    }

    [Test]
    public void Alias_over_a_Flatten_inner_flattens_back_to_the_default_tree()
    {
        // The mirror case: a Preserve (named) alias over a Flatten leaf
        // inner. The alias collapses to one alias leaf carrying the matched
        // text uniformly under default mode and PreserveAllSymbols, so the
        // captured tree flattens back to the same alias leaf either way.
        var word = Alias(Literal("ab").Flatten(FlattenType.Flatten)).As("word");
        var grammar = And(word, Eof()).Preserve();
        grammar.Compile();

        var def = grammar.Parse("ab");
        var debug = grammar.Parse("ab", new ParseOptions { PreserveAllSymbols = true });
        Assert.That(def.Success, Is.True, def.ErrorMessage);
        Assert.That(debug.Success, Is.True, debug.ErrorMessage);

        string defTree = SerializeForest(def.Symbols);
        string flattenedDebug = SerializeForest(debug.Symbols.SelectMany(s => s.Flatten()));
        Assert.That(flattenedDebug, Is.EqualTo(defTree),
            "PreserveAllSymbols+Flatten must reproduce the default parse tree for an alias over a Flatten inner");
    }

    [Test]
    public void Alias_over_a_Delete_inner_flattens_back_to_the_default_tree()
    {
        // The third inner-FlattenType case: a Preserve (named) alias over a
        // Delete composite inner that has Preserve content underneath. In a
        // default parse the Delete inner contributes nothing, so the alias
        // node is empty. PreserveAllSymbols captures every node (the Delete
        // inner returns a real Symbol instead of Discarded), but post-hoc
        // Flatten() has to re-apply the inner's Delete and reproduce the
        // empty default node. Before the fix the alias lifted the Delete
        // inner's children during the PreserveAllSymbols capture, so the
        // collapsed debug tree carried content the production parse dropped.
        var grammar = Or(Token('c').Preserve(), Token('b').Preserve())
                          .Flatten(FlattenType.Delete).AliasedAs("ROOT");
        grammar.Compile();

        var def = grammar.Parse("c");
        var debug = grammar.Parse("c", new ParseOptions { PreserveAllSymbols = true });
        Assert.That(def.Success, Is.True, def.ErrorMessage);
        Assert.That(debug.Success, Is.True, debug.ErrorMessage);

        string defTree = SerializeForest(def.Symbols);
        string flattenedDebug = SerializeForest(debug.Symbols.SelectMany(s => s.Flatten()));
        Assert.That(flattenedDebug, Is.EqualTo(defTree),
            "PreserveAllSymbols+Flatten must reproduce the default parse tree for an alias over a Delete inner");
    }

    [Test]
    public void Alias_over_a_Delete_leaf_inner_flattens_back_to_the_default_tree()
    {
        // The leaf flavor of the same shape: a named alias over a default-
        // Delete Token. Default parse leaves the alias empty (the documented
        // "AliasedAs over a Delete inner is empty" behavior); PreserveAll +
        // Flatten() must agree rather than surfacing the inner leaf.
        var grammar = Token('a').AliasedAs("x");
        grammar.Compile();

        var def = grammar.Parse("a");
        var debug = grammar.Parse("a", new ParseOptions { PreserveAllSymbols = true });
        Assert.That(def.Success, Is.True, def.ErrorMessage);
        Assert.That(debug.Success, Is.True, debug.ErrorMessage);

        string defTree = SerializeForest(def.Symbols);
        string flattenedDebug = SerializeForest(debug.Symbols.SelectMany(s => s.Flatten()));
        Assert.That(flattenedDebug, Is.EqualTo(defTree),
            "PreserveAllSymbols+Flatten must reproduce the default parse tree for an alias over a Delete leaf inner");
    }

    [Test]
    public void Alias_wrapping_a_LateBound_bound_back_to_the_alias_aborts_with_DepthLimitExceeded()
    {
        // The alias analog of the self-bound LateBoundRule test: the alias
        // wraps a LateBoundRule that is bound straight back to the alias,
        // so alias -> lateBound -> alias is a cycle with no base case.
        // Parsing recurses forever; the depth budget has to catch it and
        // fail gracefully rather than overflow the .NET call stack.
        var lateBound = new LateBoundRule("placeholder");
        var alias = lateBound.AliasedAs("self");
        lateBound.Bind(alias);

        var result = alias.Parse("x");

        Assert.That(result.Outcome, Is.EqualTo(ParseOutcome.DepthLimitExceeded));
    }

    [Test]
    public void Recursion_routed_through_an_alias_wrapping_a_LateBoundRule_parses()
    {
        // The recursion cycle runs through an AliasRule: the alias wraps a
        // LateBoundRule whose target references the alias again. Both the
        // alias's and the LateBoundRule's ComputeRuleStart pass through to
        // a child that's part of the cycle, so Compile's cycle detection
        // has to settle them without looping.
        var lateBound = new LateBoundRule("expr");
        var alias = lateBound.AliasedAs("expr");
        lateBound.Bind(Or(Integer(), And(Token('('), alias, Token(')'))));

        foreach (var input in new[] { "42", "(42)", "((42))", "(((7)))" })
        {
            var result = alias.Parse(input);
            Assert.That(result.Success, Is.True, $"{input}: {result.ErrorMessage}");
        }
    }

    [Test]
    public void Aliased_LateBoundRule_as_an_Or_alternative_is_not_skipped_in_a_cycle()
    {
        // The alias is a direct Or alternative, so the Or's lookahead
        // shortcut consults the alias's first-token set via
        // CannotMatchLookahead. The alias passes through to a LateBoundRule
        // in a cycle: if the cycle left the alias with a wrong, too-narrow
        // first-token set, the shortcut would skip the alias and the nested
        // inputs would fail to parse. A cycle has to leave it at the safe
        // pessimistic default instead.
        var lateBound = new LateBoundRule("nested");
        var alias = lateBound.AliasedAs("nested");
        lateBound.Bind(And(Token('['), Or(alias, OneOrMore(OneOf(TokenSet.Digits))), Token(']')));

        foreach (var input in new[] { "[5]", "[[5]]", "[[[9]]]" })
        {
            var result = alias.Parse(input);
            Assert.That(result.Success, Is.True, $"{input}: {result.ErrorMessage}");
        }
    }

    [Test]
    public void Alias_of_a_Preserve_target_directly_replaces_it_without_the_extra_layer()
    {
        // Contrast with the test above. Aliasing the Preserve target
        // DIRECTLY, with no LateBoundRule in between, substitutes for it:
        // the alias's Symbol replaces the target's, so the target is no
        // longer a findable layer. This is the "aliasing the target rule
        // directly avoids the extra layer" advice from the AliasRule.cs
        // comment.
        var target = And(Token('1'), Token('2')).As("target").Flatten(FlattenType.Preserve);
        var alias = target.AliasedAs("aliasName");

        var result = alias.Parse("12");
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        Assert.That(result.Tree!.Find(alias), Is.Not.Null);
        Assert.That(result.Tree!.Find(target), Is.Null,
            "Aliasing the Preserve target directly replaces its identity, " +
            "so the target is not kept as a layer.");
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
    public void Alias_of_a_Preserve_leaf_inner_renders_the_matched_text_in_ToString()
    {
        // Aliasing a Preserve leaf rule. A leaf (Literal here) carries its
        // match as text rather than as child Symbols, so the alias
        // substitutes directly for the leaf: its node takes over the
        // matched text under the alias's own identity. SourceText and
        // ToString both render that text, and the inner leaf's identity is
        // hidden under the alias path, the same as when the inner is a
        // composite.
        var inner = Literal("abc").Preserve();
        var alias = inner.AliasedAs("word");

        var result = alias.Parse("abc");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.SourceText, Is.EqualTo("abc"));
        Assert.That(result.Tree!.ToString(), Is.EqualTo("abc"),
            "ToString() on the alias node should render the matched text, " +
            "the same as the inner leaf rendered directly.");
        Assert.That(result.Tree!.Find(inner), Is.Null,
            "The inner leaf's identity stays hidden under the alias.");
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

    [Test]
    public void Alias_preserves_inner_behavior_except_for_identity()
    {
        // Direct tenet check: parse the same input through `inner` alone
        // and through `inner.AliasedAs("alias")`, then assert the two
        // results match in matched text and SourceText but differ in
        // identity (the inner's id is hidden under the alias; the
        // alias's id is findable). Covers the four shape × FlattenType
        // combinations that put a Symbol in the tree. The Delete-inner
        // case is intentionally excluded: a Delete inner contributes
        // nothing to the bare tree but the alias still wraps the match
        // in a placeholder composite, so the "same behavior" framing
        // doesn't apply.
        // Every rule's FlattenType is set explicitly (not left to a
        // default) so the cases stay deterministic if a default changes.
        var cases = new (string Name, System.Func<Rule> InnerFactory, string Input)[]
        {
            ("leaf-Preserve",      () => OneOf(TokenSet.Runes("a")).Preserve(),                                      "a"),
            ("leaf-Flatten",       () => OneOf(TokenSet.Runes("a")).Flatten(FlattenType.Flatten),                    "a"),
            ("composite-Preserve", () => And(Token('1').Preserve(), Token('2').Preserve()).Preserve(),               "12"),
            ("composite-Flatten",  () => OneOrMore(OneOf(TokenSet.Runes("a")).Preserve()).Flatten(FlattenType.Flatten), "aaa"),
        };

        foreach (var (name, innerFactory, input) in cases)
        {
            var bareInner = innerFactory();
            var aliasedInner = innerFactory();
            var alias = aliasedInner.AliasedAs("alias");

            var bareResult = bareInner.Parse(input);
            var aliasResult = alias.Parse(input);

            Assert.That(bareResult.Success, Is.True, $"[{name}] bare: {bareResult.ErrorMessage}");
            Assert.That(aliasResult.Success, Is.True, $"[{name}] alias: {aliasResult.ErrorMessage}");

            // The total rendered text matches.
            string bareToString = string.Concat(bareResult.Symbols.Select(s => s.ToString()));
            string aliasToString = string.Concat(aliasResult.Symbols.Select(s => s.ToString()));
            Assert.That(aliasToString, Is.EqualTo(bareToString),
                $"[{name}] ToString of alias result matches inner-alone result.");

            // The total SourceText matches.
            string bareSource = string.Concat(bareResult.Symbols.Select(s => s.SourceText));
            string aliasSource = string.Concat(aliasResult.Symbols.Select(s => s.SourceText));
            Assert.That(aliasSource, Is.EqualTo(bareSource),
                $"[{name}] SourceText of alias result matches inner-alone result.");

            // Identity differs: the alias is findable under its own
            // identity, but the inner's identity isn't reachable under the
            // alias.
            Assert.That(aliasResult.Find(alias), Is.Not.Null,
                $"[{name}] alias is findable in the result.");
            Assert.That(aliasResult.Find(aliasedInner), Is.Null,
                $"[{name}] inner identity is hidden under the alias.");
        }
    }

    [Test]
    public void Alias_over_a_Delete_inner_emits_a_placeholder_composite_over_the_match()
    {
        // The Delete-inner case is the one combination where the tenet
        // "alias behaves like inner" doesn't apply. A Delete inner
        // contributes no Symbol to the bare tree, but a Preserve alias
        // still emits a composite over the matched span (no children,
        // SourceText carries the matched text, ToString renders empty).
        // Lock in that shape so it can't drift silently.
        // Separate Token instances: a Rule can only belong to one
        // grammar, and compiling the bare inner seals it. FlattenType is
        // set explicitly so the case stays deterministic if a default
        // changes.
        var bareInner = Token('a').Flatten(FlattenType.Delete);
        var aliasInner = Token('a').Flatten(FlattenType.Delete);
        var alias = aliasInner.AliasedAs("alias");    // alias auto-flips to Preserve

        var bareResult = bareInner.Parse("a");
        var aliasResult = alias.Parse("a");

        Assert.That(bareResult.Success, Is.True, bareResult.ErrorMessage);
        Assert.That(aliasResult.Success, Is.True, aliasResult.ErrorMessage);

        // Bare inner contributes nothing: empty Symbols, no Tree, no
        // SourceText anywhere.
        Assert.That(bareResult.Symbols.Count, Is.EqualTo(0),
            "Bare Delete inner contributes nothing to the tree.");
        Assert.That(bareResult.Tree, Is.Null);
        string bareSourceText = string.Concat(bareResult.Symbols.Select(s => s.SourceText));
        Assert.That(bareSourceText, Is.EqualTo(""),
            "Bare has no Symbols, so no SourceText to render.");

        // Aliased inner: alias emits a Preserve composite that wraps the
        // (empty) inner content but still covers the matched span. So
        // SourceText returns the inner's match even though ToString and
        // the children list are empty. This is the deliberate divergence
        // from the tenet (the bare and alias trees differ here, where
        // the four non-Delete cases agree).
        var aliasNode = aliasResult.Tree!;
        Assert.That(aliasNode.Children.Count, Is.EqualTo(0),
            "Delete inner contributes no children, so the alias composite is empty.");
        Assert.That(aliasNode.ToString(), Is.EqualTo(""),
            "ToString walks children; no children means empty rendering.");
        Assert.That(aliasNode.SourceText, Is.EqualTo("a"),
            "SourceText reads the alias's recorded span, which covers the inner's match.");
        Assert.That(aliasNode.SourceText, Is.Not.EqualTo(bareSourceText),
            "Bare and alias SourceText diverge for a Delete inner: the tenet doesn't apply.");
        Assert.That(aliasResult.Find(alias), Is.Not.Null,
            "Alias is findable by its own identity.");
        Assert.That(aliasResult.Find(aliasInner), Is.Null,
            "Delete inner has no Symbol in the tree to find.");
    }
}
