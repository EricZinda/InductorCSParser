using System;
using System.Collections.Generic;
using System.IO;
using InductorParser;
using NUnit.Framework;
using static InductorParser.Tests.TestHelpers;

namespace InductorParser.Tests;

// Corpus tests for PrologGrammar. Same shape as the CSS/HTML/XML
// fixtures: valid input must parse, invalid input must fail. Covers
// both variable flavors, the full document rule, and the Query rule.
[TestFixture]
public class PrologGrammarTests
{
    // Corpus shared across both variable flavors. Standard Prolog and
    // HTN agree on everything that doesn't use a variable. Facts,
    // rules with no variables, lists, and comment handling all live
    // here.
    private static readonly string[] VariableFreeValidDocuments =
    {
        // Single fact.
        "foo.",
        "likes(mary, pizza).",
        "age(tom, 42).",

        // Multiple facts.
        "foo. bar. baz.",
        "likes(mary, pizza).\nlikes(tom, beer).\n",

        // Empty-argument functor.
        "foo().",

        // Numeric atoms.
        "pi(3.14).",
        "temperature(-5).",
        "version(1).",

        // Math-symbol atoms. Prolog operators like + and = are just
        // atoms under this grammar.
        "+.",
        "=.",
        "<>.",

        // Quoted atoms.
        "greeting('Hello, World').",
        "path(\"C:/foo/bar\").",

        // Lists.
        "items([]).",
        "items([1, 2, 3]).",
        "nested([[1, 2], [3, 4]]).",

        // List as a top-level statement (not inside a functor).
        "[1, 2, 3].",
        "[].",

        // Comments.
        "% a line comment\nfoo.",
        "/* a block comment */ foo.",
        "foo. % trailing comment\n",
        "foo. /* mid */ bar.",
        "% only comment then a fact\n\nfoo.",

        // Whitespace tolerance.
        "   foo   .   ",
        "foo\n\t\t.\n",
    };

    private static readonly string[] StandardValidDocuments =
    {
        // Variables lead with a capital letter under standard Prolog.
        "likes(X, pizza).",
        "likes(X, Y) :- friend(X, Y).",
        "mortal(X) :- human(X).",
        "append([], L, L).",
        "append([H | T], L, [H | R]) :- append(T, L, R).",

        // Underscore-leading variable also allowed.
        "member(_, []).",
        "length([_ | T], N) :- length(T, M).",

        // Rule with multi-goal body.
        "ancestor(X, Y) :- parent(X, Z), parent(Z, Y).",

        // Rule with a single-variable body.
        "identity(X) :- X.",
    };

    private static readonly string[] HtnValidDocuments =
    {
        // HTN variables lead with '?'.
        "likes(?x, pizza).",
        "likes(?x, ?y) :- friend(?x, ?y).",
        "mortal(?x) :- human(?x).",
        "append([], ?l, ?l).",
        "append([?h | ?t], ?l, [?h | ?r]) :- append(?t, ?l, ?r).",

        // Underscore-leading still allowed regardless of flavor.
        "member(_, []).",

        // HTN atoms can start with capital letters (only '?' marks a
        // variable), so "Foo" is a functor name under HTN.
        "Foo(a, b).",
        "Bar :- Foo(a, b).",
    };

    private static readonly string[] SharedInvalidDocuments =
    {
        // Empty document.
        "",
        "   ",
        "\n",

        // Unterminated statement (no period).
        "foo",
        "foo(a, b)",
        "foo(a, b) :- bar",

        // Mismatched parens.
        "foo(a, b.",
        "foo a, b).",

        // Mismatched brackets.
        "foo([1, 2).",
        "foo(1, 2]).",

        // Stray period inside arguments.
        "foo(a., b).",

        // Block comment never closed.
        "/* never closed\nfoo.",
    };

    // Flavor-specific negative cases.
    private static readonly string[] StandardOnlyInvalid =
    {
        // Bare '?' isn't anything in standard Prolog (not a variable,
        // not an atom prefix, not an operator).
        "likes(?x, pizza).",
    };

    private static readonly string[] HtnOnlyInvalid =
    {
        // Under HTN, a lowercase-leading bare atom is a valid functor
        // but "likes(x, pizza)" is a valid statement under both flavors
        // (x is an atom either way). So there isn't much that's valid
        // under standard but invalid under HTN. Both share most of
        // the grammar. The interesting divergence is that in HTN,
        // a ? prefix must be followed by an atom. "?." has nothing
        // after the ? so it fails.
        "foo(?).",
    };

    [Test]
    public void Standard_grammar_accepts_variable_free_corpus()
    {
        AssertAllParse(PrologGrammar.Standard.Document, VariableFreeValidDocuments, "Standard");
    }

    [Test]
    public void Standard_grammar_accepts_standard_corpus()
    {
        AssertAllParse(PrologGrammar.Standard.Document, StandardValidDocuments, "Standard");
    }

    [Test]
    public void Htn_grammar_accepts_variable_free_corpus()
    {
        AssertAllParse(PrologGrammar.Htn.Document, VariableFreeValidDocuments, "Htn");
    }

    [Test]
    public void Htn_grammar_accepts_htn_corpus()
    {
        AssertAllParse(PrologGrammar.Htn.Document, HtnValidDocuments, "Htn");
    }

    [Test]
    public void Standard_grammar_rejects_shared_invalid()
    {
        AssertAllReject(PrologGrammar.Standard.Document, SharedInvalidDocuments, "Standard");
    }

    [Test]
    public void Htn_grammar_rejects_shared_invalid()
    {
        AssertAllReject(PrologGrammar.Htn.Document, SharedInvalidDocuments, "Htn");
    }

    [Test]
    public void Standard_grammar_rejects_standard_specific_invalid()
    {
        AssertAllReject(PrologGrammar.Standard.Document, StandardOnlyInvalid, "Standard");
    }

    [Test]
    public void Htn_grammar_rejects_htn_specific_invalid()
    {
        AssertAllReject(PrologGrammar.Htn.Document, HtnOnlyInvalid, "Htn");
    }

    // Targeted fragment tests for the pieces of the grammar that don't
    // usually fail visibly at the document level.
    [Test]
    public void Comment_fragment_handles_both_forms_and_eof_termination()
    {
        Assert.That(PrologGrammar.Comment.Parse("% line\n").Success, Is.True);
        Assert.That(PrologGrammar.Comment.Parse("% line-terminated-by-eof").Success, Is.True,
            "line comments must succeed when the file ends without a newline");
        Assert.That(PrologGrammar.Comment.Parse("/* block */").Success, Is.True);
        Assert.That(PrologGrammar.Comment.Parse("/* unterminated").Success, Is.False);
        Assert.That(PrologGrammar.Comment.Parse("plain").Success, Is.False);
    }

    [Test]
    public void Atom_fragment_covers_every_branch()
    {
        // Bare word.
        Assert.That(PrologGrammar.Atom.Parse("foo").Success, Is.True);
        // Starts with hyphen (C++ allows this).
        Assert.That(PrologGrammar.Atom.Parse("-foo").Success, Is.False,
            "math-symbol run commits to the leading '-' and leaves 'foo' behind, which EOF test rejects");
        // Numeric.
        Assert.That(PrologGrammar.Atom.Parse("42").Success, Is.True);
        Assert.That(PrologGrammar.Atom.Parse("3.14").Success, Is.True);
        Assert.That(PrologGrammar.Atom.Parse("-5").Success, Is.True);
        // Math-symbol atoms.
        Assert.That(PrologGrammar.Atom.Parse("+").Success, Is.True);
        Assert.That(PrologGrammar.Atom.Parse("=<").Success, Is.True);
        // Exclamation (Prolog's cut).
        Assert.That(PrologGrammar.Atom.Parse("!").Success, Is.True);
        // Quoted.
        Assert.That(PrologGrammar.Atom.Parse("'hello world'").Success, Is.True);
        Assert.That(PrologGrammar.Atom.Parse("\"with spaces\"").Success, Is.True);
    }

    [Test]
    public void Query_fragment_parses_comma_separated_goals()
    {
        Assert.That(PrologGrammar.Standard.Query.Parse("foo(X).").Success, Is.True);
        Assert.That(PrologGrammar.Standard.Query.Parse("foo(X), bar(Y).").Success, Is.True);
        Assert.That(PrologGrammar.Standard.Query.Parse("likes(mary, X), likes(tom, X).").Success, Is.True);
        Assert.That(PrologGrammar.Standard.Query.Parse("foo(X)").Success, Is.False,
            "query must end with '.'");
    }

    [Test]
    public void List_fragment_handles_empty_head_tail_and_nesting()
    {
        var list = PrologGrammar.Standard.List;
        Assert.That(list.Parse("[]").Success, Is.True);
        Assert.That(list.Parse("[1]").Success, Is.True);
        Assert.That(list.Parse("[1, 2, 3]").Success, Is.True);
        Assert.That(list.Parse("[H | T]").Success, Is.True);
        Assert.That(list.Parse("[1, 2 | Rest]").Success, Is.True);
        Assert.That(list.Parse("[[a, b], [c, d]]").Success, Is.True);
        Assert.That(list.Parse("[1, 2,").Success, Is.False);
    }

    [Test]
    public void Term_disambiguates_variable_from_functor_per_flavor()
    {
        // Under standard Prolog, "X" is a variable.
        var stdTerm = PrologGrammar.Standard.Term;
        Assert.That(stdTerm.Parse("X").Success, Is.True);
        Assert.That(stdTerm.Parse("foo").Success, Is.True);
        Assert.That(stdTerm.Parse("?x").Success, Is.False,
            "standard Prolog treats '?' as an unknown leading char");

        // Under HTN, "?x" is a variable and "X" is just an atom/functor name.
        var htnTerm = PrologGrammar.Htn.Term;
        Assert.That(htnTerm.Parse("?x").Success, Is.True);
        Assert.That(htnTerm.Parse("X").Success, Is.True,
            "HTN treats capitalized bare words as atoms/functors");
        Assert.That(htnTerm.Parse("foo").Success, Is.True);
    }

    // ---------------------------------------------------------------
    // Corpora ported verbatim from InductorProlog's
    // src/Tests/Prolog/PrologCompilerTests.cpp
    // (https://github.com/EricZinda/InductorProlog/blob/master/src/Tests/Prolog/PrologCompilerTests.cpp).
    // Each section mirrors a
    // TestTryParse<Rule>("input", ...) call from the C++ file. Tests
    // that the C++ put in the "same for both VariableRule alternates"
    // block go in the *_BothFlavors fields. Tests that the C++ gated
    // on htnStyle go in the per-flavor fields.
    // ---------------------------------------------------------------

    private static readonly string[] CppAtomCorpus =
    {
        "constant", "con-stant", "con_stant",
        ">", ">=",
        "1", "+1", "-1", "1.2", "+1.2", "-1.2",
        "!",
    };

    private static readonly string[] CppCommentCorpus =
    {
        "%\r\n",
        "%foo\r\n",
        "%foo\n",
        "%foo\r",
        "%foo\n\r\n",
        "/* test */",
        "/* test \r\n test2 */",
    };

    private static readonly string[] CppOptionalWhitespaceCorpus =
    {
        " ",
        " \r\n",
        "%foo\n\r\n",
        "\r\n  %foo\n\r\n",
    };

    private static readonly string[] CppTermCorpus_BothFlavors =
    {
        "a", "a(b)", "A",
    };

    private static readonly string[] CppTermListCorpus_BothFlavors =
    {
        "foo(a)",
        "foo(a), !",
    };

    private static readonly string[] CppFunctorCorpus_BothFlavors =
    {
        "a()", "a(b)", "-(b,c)", "a(b,c)", "a",
    };

    private static readonly string[] CppListCorpus_BothFlavors =
    {
        "[]", "[a]", "[a, b]", "[ a , b ]",
        "[ a( b , c ) , b ]",
        "[ [ a( b , c(1, 2, 3) ) , b ] ]",
        "[ [], [[[a], b]], [] ]",
        // Lists with |-tail.
        "[a | [b]]",
        "[ a, b, c | [ d ] ]",
        "[a|[b | [c]]]",
        "[ a, b | c(d, e) ]",
    };

    private static readonly string[] CppListInvalidCorpus_BothFlavors =
    {
        "[a | ]",                  // empty tail
        "[ | [ d ] ]",             // empty head
        "[ a, b, c, | [ d ]]",     // comma just before tail
        "[ a, b, c | [ d ], e ]",  // trailing element after tail
    };

    private static readonly string[] CppRuleCorpus_BothFlavors =
    {
        "a :- ",
        "a(g) :- ",
        "a( g , ef ) :- ",
        "a(g) :- b(c)",
        "a(g) :- b(c), d(a,b,c)",
        "a( g, ef) :- foo(a)",
        "a( g, ef) :- foo(a), !",
    };

    private static readonly string[] CppDocumentCorpus_BothFlavors =
    {
        "a.  ",
        "a.  b.",
        "a(g, ef) :- foo(a).",
        "north :- go(north). \r\nsouth :- go(south).",
        "a(g).  ",
        "a(g).  \r\nb(d,e(f,g)) :-.",
        "goals(findSolution(a)).",
        // Documents interleaved with comments.
        "a(a). b(b). a(b, c). % This is a comment\r\n",
        "a(%\na%\n)%\n.%\n b(%\nb%\n)%\n.%\n a(%\nb,%\n c%\n)%\n. % This is a comment\r\n",
    };

    private static readonly string[] CppQueryCorpus_BothFlavors =
    {
        "a.",
        "a(a).",
        "a(a). \r\n",
        "a(X, a). \r\n",
    };

    // Inputs the C++ expects to PARSE under HTN and FAIL under standard.
    // Every one uses the '?' variable prefix somewhere, or uses
    // capitalized functor names (illegal under standard Prolog because
    // those would parse as variables first).
    private static readonly string[] CppFunctorCorpus_HtnOnly =
    {
        // Capitalized functor names. "Move" reads as a variable under
        // standard Prolog, which then has no way to form a compound term.
        "do(Move(unit,from,to),SetEnergy(energy,-(energy,moveCost)))",
        // '?' variables as arguments.
        "a(?b,?c)",
        "a( ?b , ?c )",
        "a( d(e,f,g) , ?c )",
    };

    private static readonly string[] CppDocumentCorpus_HtnOnly =
    {
        "operator(SetEnergy(?old,?new), del(Energy(?old)), add(Energy(?new)) ).",
        "method(MoveUnit(?unit, ?to), if(UnitIdle(?unit),At(?unit,?from)),   do(Move(?unit,?from,?to),SetEnergy(?energy,-(?energy,?moveCost))) ).",
    };

    private static readonly string[] CppQueryCorpus_HtnOnly =
    {
        "a(?X, a). \r\n",
    };

    private static readonly string[] CppListCorpus_HtnOnly =
    {
        "[?X]",
        "[a(?X, a), ?Y, [ ?z, [], ?a]]",
        "[?A|?B]",
        "[ a, ?B, c | [ ?B ]]",
        "[ a, ?B, c | ?B ]",
    };

    [Test]
    public void Cpp_atom_corpus_parses_under_both_flavors()
    {
        AssertAllParse(PrologGrammar.Atom, CppAtomCorpus, "Atom");
    }

    [Test]
    public void Cpp_comment_corpus_parses()
    {
        AssertAllParse(PrologGrammar.Comment, CppCommentCorpus, "Comment");
    }

    [Test]
    public void Cpp_optional_whitespace_corpus_parses()
    {
        // OptionalWhitespace.Parse with the default options (AllowTrailingInput =
        // false) already rejects a half-matched run, so we don't need to
        // wrap it in And(..., Eof()). Wrapping a shared static rule in a
        // new composite is also rejected by the parser's fresh-child
        // check when the static rule has been compiled by another test in
        // this fixture.
        AssertAllParse(PrologGrammar.OptionalWhitespace, CppOptionalWhitespaceCorpus, "OptionalWhitespace");
    }

    [Test]
    public void Cpp_term_corpus_parses_under_both_flavors()
    {
        AssertAllParse(PrologGrammar.Standard.Term, CppTermCorpus_BothFlavors, "Standard.Term");
        AssertAllParse(PrologGrammar.Htn.Term, CppTermCorpus_BothFlavors, "Htn.Term");
    }

    [Test]
    public void Cpp_term_list_corpus_parses_under_both_flavors()
    {
        AssertAllParse(PrologGrammar.Standard.TermList, CppTermListCorpus_BothFlavors, "Standard.TermList");
        AssertAllParse(PrologGrammar.Htn.TermList, CppTermListCorpus_BothFlavors, "Htn.TermList");
    }

    [Test]
    public void Cpp_functor_corpus_parses_under_both_flavors()
    {
        AssertAllParse(PrologGrammar.Standard.Functor, CppFunctorCorpus_BothFlavors, "Standard.Functor");
        AssertAllParse(PrologGrammar.Htn.Functor, CppFunctorCorpus_BothFlavors, "Htn.Functor");
    }

    [Test]
    public void Cpp_list_corpus_parses_under_both_flavors()
    {
        AssertAllParse(PrologGrammar.Standard.List, CppListCorpus_BothFlavors, "Standard.List");
        AssertAllParse(PrologGrammar.Htn.List, CppListCorpus_BothFlavors, "Htn.List");
    }

    [Test]
    public void Cpp_list_negative_corpus_rejected_under_both_flavors()
    {
        AssertAllReject(PrologGrammar.Standard.List, CppListInvalidCorpus_BothFlavors, "Standard.List");
        AssertAllReject(PrologGrammar.Htn.List, CppListInvalidCorpus_BothFlavors, "Htn.List");
    }

    [Test]
    public void Cpp_rule_corpus_parses_under_both_flavors()
    {
        AssertAllParse(PrologGrammar.Standard.Rule, CppRuleCorpus_BothFlavors, "Standard.Rule");
        AssertAllParse(PrologGrammar.Htn.Rule, CppRuleCorpus_BothFlavors, "Htn.Rule");
    }

    [Test]
    public void Cpp_document_corpus_parses_under_both_flavors()
    {
        AssertAllParse(PrologGrammar.Standard.Document, CppDocumentCorpus_BothFlavors, "Standard.Document");
        AssertAllParse(PrologGrammar.Htn.Document, CppDocumentCorpus_BothFlavors, "Htn.Document");
    }

    [Test]
    public void Cpp_query_corpus_parses_under_both_flavors()
    {
        AssertAllParse(PrologGrammar.Standard.Query, CppQueryCorpus_BothFlavors, "Standard.Query");
        AssertAllParse(PrologGrammar.Htn.Query, CppQueryCorpus_BothFlavors, "Htn.Query");
    }

    [Test]
    public void Cpp_htn_only_functor_corpus_flips_per_flavor()
    {
        AssertAllParse(PrologGrammar.Htn.Functor, CppFunctorCorpus_HtnOnly, "Htn.Functor");
        AssertAllReject(PrologGrammar.Standard.Functor, CppFunctorCorpus_HtnOnly, "Standard.Functor");
    }

    [Test]
    public void Cpp_htn_only_document_corpus_flips_per_flavor()
    {
        AssertAllParse(PrologGrammar.Htn.Document, CppDocumentCorpus_HtnOnly, "Htn.Document");
        AssertAllReject(PrologGrammar.Standard.Document, CppDocumentCorpus_HtnOnly, "Standard.Document");
    }

    [Test]
    public void Cpp_htn_only_query_corpus_flips_per_flavor()
    {
        AssertAllParse(PrologGrammar.Htn.Query, CppQueryCorpus_HtnOnly, "Htn.Query");
        AssertAllReject(PrologGrammar.Standard.Query, CppQueryCorpus_HtnOnly, "Standard.Query");
    }

    [Test]
    public void Cpp_htn_only_list_corpus_flips_per_flavor()
    {
        AssertAllParse(PrologGrammar.Htn.List, CppListCorpus_HtnOnly, "Htn.List");
        AssertAllReject(PrologGrammar.Standard.List, CppListCorpus_HtnOnly, "Standard.List");
    }

    // ---------------------------------------------------------------
    // Real-world fixture files from InductorHtn/Examples
    // (https://github.com/EricZinda/InductorHtn/tree/master/Examples).
    // End-to-end sanity: the grammar must parse actual documents the HTN
    // project ships and uses, not just hand-crafted one-liners. Files are
    // copied to the test output directory by the csproj.
    // ---------------------------------------------------------------

    private static string LoadFixture(string fileName)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "E2EExamples", "PrologFixtures", fileName);
        return File.ReadAllText(path);
    }

    [Test]
    public void JordanAdventure_pl_parses_as_standard_prolog()
    {
        var source = LoadFixture("JordanAdventure.pl");
        var result = PrologGrammar.Standard.Document.Parse(source);
        Assert.That(result.Success, Is.True,
            result.Success ? "" : $"col {result.ErrorCharIndex}: {result.ErrorMessage}");
    }

    [Test]
    public void Taxi_htn_parses_as_htn_prolog()
    {
        var source = LoadFixture("Taxi.htn");
        var result = PrologGrammar.Htn.Document.Parse(source);
        Assert.That(result.Success, Is.True,
            result.Success ? "" : $"col {result.ErrorCharIndex}: {result.ErrorMessage}");
    }

    [Test]
    public void Game_htn_parses_as_htn_prolog()
    {
        var source = LoadFixture("Game.htn");
        var result = PrologGrammar.Htn.Document.Parse(source);
        Assert.That(result.Success, Is.True,
            result.Success ? "" : $"col {result.ErrorCharIndex}: {result.ErrorMessage}");
    }

}
