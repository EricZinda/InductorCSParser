using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests.DocExamples;

// Verifies the alias example in docs/primerFailure.md (the "Parsing
// Errors" primer). The primer shipped with no backing example test, the
// ozpb gap, and its alias snippet drifted the worst way possible: it
// constructed `new AliasRule(comma)`, but AliasRule is internal, so the
// example failed CS0122 for any real consumer. Neither test assembly can
// catch that class of drift by compiling it: InductorParser.Tests is a
// friend assembly (InternalsVisibleTo) and ExternalContractTests
// source-links AliasRule.cs, so `new AliasRule(...)` compiles in both.
// Doc snippets must stick to the Rules factories (here `Alias(...)`),
// and these tests run the corrected snippet's exact API shape.
//
// The primer's other examples (the default message, the WithError
// walkthrough, and the French templates) reuse the primer2 INI grammar
// and are covered in Primer2Examples.cs next to that grammar's builder.
[TestFixture]
public class PrimerFailureExamples
{
    // The doc's scenario: one shared comma rule referenced from several
    // places, where a single caller wants its own error message. The
    // snippet's two lines are reproduced verbatim inside the grammar.
    private static Rule BuildListGrammar()
    {
        // shared, no error message
        var comma = Token(',').Flatten(FlattenType.Delete);

        var word = OneOrMore(OneOf(TokenSet.Letters));

        return And(
            word.AliasedAs("first"),
            // At the one caller that wants a message:
            Alias(comma).WithError("expected ',' after citation key"),
            word.AliasedAs("second"),
            comma,
            word.AliasedAs("third"));
    }

    [Test]
    public void Alias_gives_the_shared_comma_a_caller_specific_error()
    {
        var list = BuildListGrammar();

        var result = list.Parse("ab;cd,ef");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage,
            Is.EqualTo("expected ',' after citation key at line 1, column 3."),
            "the alias's WithError surfaces where the aliased comma was expected");
    }

    [Test]
    public void Shared_comma_stays_untouched_at_its_other_callers()
    {
        var list = BuildListGrammar();

        var result = list.Parse("ab,cd;ef");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage,
            Is.EqualTo("Unexpected ';' at line 1, column 6."),
            "the bare shared comma keeps the generic default message");
    }

    [Test]
    public void Alias_contributes_no_tree_node_for_the_delimiter()
    {
        var list = BuildListGrammar();

        var result = list.Parse("ab,cd,ef");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        // The alias defaults to FlattenType.Flatten and the comma is
        // Delete, so the tree holds exactly the three named words.
        Assert.That(result.Symbols.Count, Is.EqualTo(3));
        Assert.That(result.Symbols[0].ToString(), Is.EqualTo("ab"));
        Assert.That(result.Symbols[1].ToString(), Is.EqualTo("cd"));
        Assert.That(result.Symbols[2].ToString(), Is.EqualTo("ef"));
        Assert.That(result.DisplayName(result.Symbols[0]!), Is.EqualTo("first"));
        Assert.That(result.DisplayName(result.Symbols[1]!), Is.EqualTo("second"));
        Assert.That(result.DisplayName(result.Symbols[2]!), Is.EqualTo("third"));
    }
}
