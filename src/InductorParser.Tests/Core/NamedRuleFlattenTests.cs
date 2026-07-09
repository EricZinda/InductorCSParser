using System;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Tests for the .As / .Flatten interaction. The rule under test is:
// a caller who identifies a rule with .As(name) or .As(SymbolId) almost
// always wants Tree.Find / Tree.FindAll to surface it later, and that
// only works if the rule's Symbol reaches the parse tree
// (FlattenType.Preserve). Two behaviors fall out of that rule:
//
//   * .As on a rule whose flatten policy is still the class default
//     silently flips the policy to Preserve. Token / Literal default
//     to Delete. And / Or / OneOrMore / ZeroOrMore default to Flatten.
//     Naming any of them used to leave their Symbol absent
//     from the tree, so Tree.Find returned null even when the rule
//     matched. The auto-flip removes that silent surprise.
//
//   * .As on a rule whose flatten policy was explicitly set to a
//     non-Preserve value, OR .Flatten(non-Preserve) on a rule that's
//     already been .As'd, throws InvalidOperationException. The two
//     requests contradict each other and the safe answer is to throw
//     rather than pick a winner.
[TestFixture]
public class NamedRuleFlattenTests
{
    // -----------------------------------------------------------------
    // .As auto-flips default-non-Preserve flatten policies to Preserve.
    // -----------------------------------------------------------------

    [Test]
    public void As_string_on_Token_flips_default_Delete_to_Preserve()
    {
        // Token('!') defaults to FlattenType.Delete, which would
        // remove the rule's Symbol and make Tree.Find return null.
        // The auto-flip is what makes the natural-looking grammar
        // (no manual .Preserve()) work for finding.
        var marker = Token('!').As("marker");

        Assert.That(marker.FlattenType, Is.EqualTo(FlattenType.Preserve));
        Assert.That(marker.Name, Is.EqualTo("marker"));
    }

    [Test]
    public void As_string_on_ZeroOrMore_flips_default_Flatten_to_Preserve()
    {
        // ZeroOrMore / OneOrMore default to FlattenType.Flatten,
        // which lifts children into the parent and removes the
        // repetition's Symbol. .As(name) flips it so the Symbol
        // stays in the tree as a findable node.
        var repeated = ZeroOrMore(OneOf(TokenSet.Letters)).As("letters");

        Assert.That(repeated.FlattenType, Is.EqualTo(FlattenType.Preserve));
    }

    [Test]
    public void As_string_on_And_flips_default_Flatten_to_Preserve()
    {
        // And defaults to Flatten. Same auto-flip story for composites.
        var sequence = And(Token('a'), Token('b')).As("ab");

        Assert.That(sequence.FlattenType, Is.EqualTo(FlattenType.Preserve));
    }

    [Test]
    public void As_string_on_OneOf_leaves_default_Preserve_alone()
    {
        // OneOf already defaults to Preserve. No flip needed, and
        // the rule's policy stays at its class default (not
        // explicitly set, so a later .Delete() would still throw
        // because Name is set).
        var letter = OneOf(TokenSet.Letters).As("letter");

        Assert.That(letter.FlattenType, Is.EqualTo(FlattenType.Preserve));
    }

    [Test]
    public void As_SymbolId_on_Token_flips_default_Delete_to_Preserve()
    {
        // The SymbolId overload of .As has the same identify-implies-
        // Preserve story as the string overload: an explicit id is only
        // useful if the Symbol that stores it reaches the tree.
        var explicitId = new SymbolId(SymbolRanges.CustomRangeStart + 42);
        var marker = Token('!').As(explicitId);

        Assert.That(marker.FlattenType, Is.EqualTo(FlattenType.Preserve));
        Assert.That(marker.Id, Is.EqualTo(explicitId));
    }

    // -----------------------------------------------------------------
    // Tree.Find regression: the original failing case from the backlog.
    // -----------------------------------------------------------------

    [Test]
    public void Find_locates_named_Token_without_manual_Preserve()
    {
        // The natural-looking grammar from the Versionize ConventionalCommit
        // rewrite. Pre-fix, Tree.Find(marker) returned null because Token
        // defaults to Delete and the rule's Symbol never reached the tree.
        // After the fix, .As flips the policy and Find succeeds.
        var marker = Token('!').As("marker");
        var rule = And(Token('a'), Optional(marker), Token('b')).Preserve();
        rule.Compile();

        var result = rule.Parse("a!b");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree, Is.Not.Null);
        Assert.That(result.Tree!.Find(marker), Is.Not.Null);
    }

    [Test]
    public void Find_locates_named_ZeroOrMore_without_manual_Preserve()
    {
        // The "type" rule shape from the same Versionize grammar.
        // ZeroOrMore defaults to Flatten, so without the auto-flip
        // its Symbol gets lifted into the parent and Find
        // returns null. After the fix, the Symbol survives.
        var type = ZeroOrMore(OneOf(TokenSet.Letters)).As("type");
        var rule = And(type, Eof()).Preserve();
        rule.Compile();

        var result = rule.Parse("feat");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.Find(type), Is.Not.Null);
    }

    // -----------------------------------------------------------------
    // .Flatten(non-Preserve) before .As(name) throws.
    // -----------------------------------------------------------------

    [Test]
    public void As_string_after_explicit_Delete_throws()
    {
        // The caller explicitly asked for Delete (the rule's Symbol
        // doesn't reach the tree) and for the rule to be findable
        // by name. Those two requests contradict each other, so
        // .As fails rather than silently overriding the explicit
        // .Delete() decision.
        var marker = Token('!').Delete();

        var exception = Assert.Throws<InvalidOperationException>(() => marker.As("marker"));
        Assert.That(exception!.Message, Does.Contain("FlattenType.Delete"));
        Assert.That(exception.Message, Does.Contain("Preserve"));
        Assert.That(exception.Message, Does.Contain("Tree.Find"));
    }

    [Test]
    public void As_string_after_explicit_Flatten_throws()
    {
        // Same story for the Flatten-shortcut: the caller set a
        // non-Preserve policy explicitly, so .As refuses to undo it.
        var rule = And(Token('a'), Token('b')).Flatten();

        var exception = Assert.Throws<InvalidOperationException>(() => rule.As("ab"));
        Assert.That(exception!.Message, Does.Contain("FlattenType.Flatten"));
    }

    [Test]
    public void As_string_after_explicit_Flatten_with_Preserve_argument_works()
    {
        // .Flatten(FlattenType.Preserve) is the one explicit-Flatten call
        // that doesn't contradict .As. The caller set Preserve themselves,
        // so .As just sets the name and moves on.
        var marker = Token('!').Flatten(FlattenType.Preserve).As("marker");

        Assert.That(marker.FlattenType, Is.EqualTo(FlattenType.Preserve));
        Assert.That(marker.Name, Is.EqualTo("marker"));
    }

    [Test]
    public void As_SymbolId_after_explicit_Delete_throws()
    {
        // Same identify-implies-Preserve gate for the SymbolId overload.
        var marker = Token('!').Delete();
        var explicitId = new SymbolId(SymbolRanges.CustomRangeStart + 42);

        var exception = Assert.Throws<InvalidOperationException>(
            () => marker.As(explicitId));
        Assert.That(exception!.Message, Does.Contain("FlattenType.Delete"));
    }

    // -----------------------------------------------------------------
    // .Flatten(non-Preserve) after .As(name) throws.
    // -----------------------------------------------------------------

    [Test]
    public void Flatten_Delete_after_As_string_throws()
    {
        // Reverse order. .As(name) ran first and auto-flipped to
        // Preserve. The caller then asks for Delete, which would
        // remove the rule's Symbol and break Tree.Find. .Flatten
        // refuses rather than silently undoing the implicit
        // Preserve that .As set.
        var marker = Token('!').As("marker");

        var exception = Assert.Throws<InvalidOperationException>(
            () => marker.Flatten(FlattenType.Delete));
        Assert.That(exception!.Message, Does.Contain("Delete"));
        Assert.That(exception.Message, Does.Contain("marker"));
        Assert.That(exception.Message, Does.Contain("Tree.Find"));
    }

    [Test]
    public void Flatten_via_Delete_shortcut_after_As_string_throws()
    {
        // .Delete() is the shorthand for .Flatten(FlattenType.Delete),
        // so the same gate fires.
        var marker = Token('!').As("marker");

        var exception = Assert.Throws<InvalidOperationException>(() => marker.Delete());
        Assert.That(exception!.Message, Does.Contain("Delete"));
    }

    [Test]
    public void Flatten_via_no_arg_Flatten_shortcut_after_As_string_throws()
    {
        // .Flatten() (no argument) maps to .Flatten(FlattenType.Flatten).
        var rule = And(Token('a'), Token('b')).As("ab");

        var exception = Assert.Throws<InvalidOperationException>(() => rule.Flatten());
        Assert.That(exception!.Message, Does.Contain("Flatten"));
    }

    [Test]
    public void Flatten_Preserve_after_As_string_works()
    {
        // Explicitly setting Preserve after .As is a no-op in effect
        // (the auto-flip already set it) but doesn't throw, since the
        // caller's two requests don't contradict each other.
        var marker = Token('!').As("marker").Preserve();

        Assert.That(marker.FlattenType, Is.EqualTo(FlattenType.Preserve));
    }

    [Test]
    public void Flatten_Delete_after_As_SymbolId_throws()
    {
        var explicitId = new SymbolId(SymbolRanges.CustomRangeStart + 42);
        var marker = Token('!').As(explicitId);

        var exception = Assert.Throws<InvalidOperationException>(
            () => marker.Flatten(FlattenType.Delete));
        Assert.That(exception!.Message, Does.Contain("Delete"));
        Assert.That(exception.Message, Does.Contain("Tree.Find"));
    }

    // -----------------------------------------------------------------
    // Mix-and-match: explicit Preserve, then non-Preserve after .As.
    // -----------------------------------------------------------------

    [Test]
    public void Explicit_Preserve_then_As_then_Delete_throws()
    {
        // Even when the rule started with an explicit .Preserve(),
        // adding .As locks the policy in: .Delete() afterwards still
        // breaks Tree.Find, so it still throws.
        var marker = Token('!').Preserve().As("marker");

        var exception = Assert.Throws<InvalidOperationException>(() => marker.Delete());
        Assert.That(exception!.Message, Does.Contain("Delete"));
    }

    [Test]
    public void As_string_rejects_null_name()
    {
        // .As(null!) rejects the null at the API boundary, before
        // ApplyIdentificationFlattenPolicy gets a chance to flip Token's
        // default Delete FlattenType to Preserve. The post-throw state
        // assertions verify that no side effect ran: FlattenType stayed
        // Delete, Name stayed null, and a follow-up legitimate .As(...)
        // would still see the rule in its untouched "no name yet" state.
        var token = Token('!');
        var exception = Assert.Throws<ArgumentNullException>(() => token.As((string)null!));
        Assert.That(exception!.ParamName, Is.EqualTo("name"));
        Assert.That(token.FlattenType, Is.EqualTo(FlattenType.Delete));
        Assert.That(token.Name, Is.Null);
    }
}
